using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.IntegrationTests.Support;
using UglyToad.PdfPig;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.20 over HTTP: the staff-editable default terms of the quote PDF. Read with <c>quotes.view</c>, change with <c>quotes.approve</c>
/// (Finance, Admin); validation, stale <c>rowVersion</c>, the audit row, and the frozen-snapshot rule: a quote that was sent keeps printing the
/// terms it was sent with, also when its missing file is rendered again (same SHA-256).
/// </summary>
public sealed class QuoteTermsEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private const string Url = "/api/v1/admin/quote-terms";
    private readonly QuoteIssueKit _kit = new(factory);

    private Task<RfqApiKit.Person> FinanceAsync() => _kit.StaffAsync(Permissions.Roles.Finance);

    private Task<RfqApiKit.Person> SupportAsync() => _kit.StaffAsync(Permissions.Roles.Support);

    private static object Body(IEnumerable<string?>? en, IEnumerable<string?>? bn, uint? rowVersion) => new { termsEn = en, termsBn = bn, rowVersion };

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK) => await RfqApiKit.DataAsync(r, expected);

    private static uint Version(JsonElement data) => data.GetProperty("rowVersion").GetUInt32();

    private static string[] Lines(JsonElement data, string name) => [.. data.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Concat(doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text)));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<JsonElement> SetAsync(string[] en, string[] bn)
    {
        var finance = await FinanceAsync();
        var current = await DataAsync(await finance.Client.GetAsync(Url));
        return await DataAsync(await finance.Client.PutAsJsonAsync(Url, Body(en, bn, Version(current))));
    }

    [PostgresFact]
    public async Task Get_And_Put_HappyPath_ReturnTheSeededDefaults_ThenTheNewLines_PrivateNoStore_WithAnAuditRow()
    {
        await using (var reset = _kit.Db()) await reset.Database.ExecuteSqlRawAsync("DELETE FROM quote_terms_settings");   // back to "never read": seeded on first read
        var finance = await FinanceAsync();
        var first = await finance.Client.GetAsync(Url);
        first.Headers.CacheControl!.NoStore.Should().BeTrue();
        first.Headers.CacheControl.Private.Should().BeTrue();
        var seeded = await DataAsync(first);
        Lines(seeded, "termsEn").Should().Contain(l => l.StartsWith("Payment: 70% advance")).And.HaveCountGreaterThan(3);
        Lines(seeded, "termsBn").Should().NotBeEmpty();

        var put = await finance.Client.PutAsJsonAsync(Url, Body(["  Payment: 50% advance.\u0001  ", "Offer valid until {validUntil}."], ["পেমেন্ট: ৫০% অগ্রিম।"], Version(seeded)));
        put.Headers.CacheControl!.NoStore.Should().BeTrue();
        var saved = await DataAsync(put);
        Lines(saved, "termsEn").Should().Equal("Payment: 50% advance.", "Offer valid until {validUntil}.");   // the control character is stripped and the line trimmed
        Lines(saved, "termsBn").Should().Equal("পেমেন্ট: ৫০% অগ্রিম।");
        Version(saved).Should().NotBe(Version(seeded));
        saved.GetProperty("updatedBy").GetGuid().Should().Be(finance.Id);

        var viewer = await SupportAsync();
        Lines(await DataAsync(await viewer.Client.GetAsync(Url)), "termsEn").Should().Equal(Lines(saved, "termsEn"), "quotes.view may read");

        await using var db = _kit.Db();
        var changes = await db.AuditLogs.CountAsync(a => a.Action == "quote.terms_changed");
        var audit = await db.AuditLogs.Where(a => a.Action == "quote.terms_changed").OrderByDescending(a => a.CreatedAtUtc).FirstAsync();
        audit.ActorId.Should().Be(finance.Id);
        audit.OldValuesJson.Should().Contain("Payment: 70% advance");
        audit.NewValuesJson.Should().Contain("Payment: 50% advance.");

        var unchanged = await DataAsync(await finance.Client.PutAsJsonAsync(Url, Body(Lines(saved, "termsEn"), Lines(saved, "termsBn"), Version(saved))));
        Version(unchanged).Should().Be(Version(saved), "an identical save writes nothing");
        (await db.AuditLogs.CountAsync(a => a.Action == "quote.terms_changed")).Should().Be(changes);
    }

    [PostgresFact]
    public async Task Put_Refuses_BadLists_With400()
    {
        var finance = await FinanceAsync();
        var v = Version(await DataAsync(await finance.Client.GetAsync(Url)));
        string[] ok = ["One line."];
        var bad = new (string Name, object Body)[]
        {
            ("13 lines", Body(Enumerable.Repeat("x", 13), ok, v)),
            ("301 characters", Body([new string('x', 301)], ok, v)),
            ("empty line", Body(["fine", ""], ok, v)),
            ("only control characters", Body(["\u0001\u0002"], ok, v)),
            ("whitespace only", Body(ok, ["   "], v)),
            ("missing english list", Body(null, ok, v)),
            ("missing bangla list", Body(ok, null, v)),
            ("null line", Body([null], ok, v)),
            ("missing rowVersion", Body(ok, ok, null)),
        };
        foreach (var (name, body) in bad) (await finance.Client.PutAsJsonAsync(Url, body)).StatusCode.Should().Be(HttpStatusCode.BadRequest, name);

        var edge = await DataAsync(await finance.Client.PutAsJsonAsync(Url, Body(Enumerable.Repeat(new string('é', 300), 12), Array.Empty<string>(), v)));
        Lines(edge, "termsEn").Should().HaveCount(12, "12 lines of 300 characters are allowed, and an empty list too");
        Lines(edge, "termsBn").Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Put_WithAStaleRowVersion_Is409_AndChangesNothing()
    {
        var finance = await FinanceAsync();
        var start = await DataAsync(await finance.Client.GetAsync(Url));
        var winner = await DataAsync(await finance.Client.PutAsJsonAsync(Url, Body(["Winner line."], ["বিজয়ী।"], Version(start))));
        var (code, _) = await RfqApiKit.ErrorAsync(await finance.Client.PutAsJsonAsync(Url, Body(["Loser line."], ["পরাজিত।"], Version(start))), HttpStatusCode.Conflict);
        code.Should().Be("QUOTE_TERMS_CHANGED");
        Lines(await DataAsync(await finance.Client.GetAsync(Url)), "termsEn").Should().Equal(Lines(winner, "termsEn"));
    }

    [PostgresFact]
    public async Task Permissions_ViewIsNotEnoughToChange_BuyerIsRefused_AndAnonymousIs401()
    {
        var support = await SupportAsync();
        var v = Version(await DataAsync(await support.Client.GetAsync(Url)));
        (await support.Client.PutAsJsonAsync(Url, Body(["Nope."], ["না।"], v))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "quotes.edit and quotes.view do not change the terms");

        var buyer = await _kit.StaffAsync(Permissions.Roles.Buyer, "terms-buyer");
        (await buyer.Client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await buyer.Client.PutAsJsonAsync(Url, Body(["Nope."], ["না।"], v))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync(Url, Body(["Nope."], ["না।"], v))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Lines(await DataAsync(await support.Client.GetAsync(Url)), "termsEn").Should().NotContain("Nope.");
    }

    [PostgresFact]
    public async Task ASentVersion_KeepsThePdfTermsItWasSentWith_AlsoWhenTheMissingFileIsRenderedAgain()
    {
        await _kit.TenPercentFloorAsync();
        await SetAsync(["Markeralpha: valid until {validUntil}."], ["Bnmarkeralpha: {validUntil}."]);
        var buyer = await _kit.StaffAsync(Permissions.Roles.Buyer, "terms-pdf-buyer");
        var w = await _kit.CompanyAsync("Terms Pdf Ltd", (buyer.Id, CompanyRoles.Buyer));
        var item = await _kit.ItemAsync();
        var support = await SupportAsync();
        var sent = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SentAsync(support.Client, sent, "sent");
        var draft = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);

        // The defaults change after the send: the sent quote must not notice.
        await SetAsync(["Markerbeta: valid until {validUntil}."], ["Bnmarkerbeta."]);

        string PdfUrl(Guid quote, string locale) => $"/api/v1/companies/{w.Company}/quotes/{quote}/versions/1/pdf?locale={locale}";
        var en = await (await buyer.Client.GetAsync(PdfUrl(sent, "en"))).Content.ReadAsByteArrayAsync();
        Text(en).Should().Contain("Markeralpha:validuntil").And.NotContain("Markerbeta").And.NotContain("{validUntil}", "the line prints the version's real date");
        var bn = await (await buyer.Client.GetAsync(PdfUrl(sent, "bn"))).Content.ReadAsByteArrayAsync();
        Text(bn).Should().Contain("Bnmarkeralpha").And.NotContain("Bnmarkerbeta");

        // A draft previews the defaults of this moment; a quote sent now freezes them.
        var preview = await support.Client.GetAsync($"/api/v1/admin/quotes/{draft}/versions/1/pdf");
        Text(await preview.Content.ReadAsByteArrayAsync()).Should().Contain("Markerbeta").And.NotContain("Markeralpha");
        await SentAsync(support.Client, draft, "sent");
        Text(await (await buyer.Client.GetAsync(PdfUrl(draft, "en"))).Content.ReadAsByteArrayAsync()).Should().Contain("Markerbeta:validuntil");

        // The stored file goes missing and the defaults change again: the new file is the same bytes (the recorded SHA-256 holds).
        await SetAsync(["Markergamma."], ["Bnmarkergamma."]);
        await factory.Services.GetRequiredService<IObjectStorage>().DeleteAsync($"quotes/{sent:N}/v1-en.pdf");
        var again = await buyer.Client.GetAsync(PdfUrl(sent, "en"));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        Hex(await again.Content.ReadAsByteArrayAsync()).Should().Be(Hex(en));

        await using var db = _kit.Db();
        (await db.Database.SqlQuery<string>($"SELECT terms_snapshot::text AS \"Value\" FROM quote_versions WHERE quote_id = {sent}").SingleAsync()).Should().Contain("Markeralpha");
    }
}
