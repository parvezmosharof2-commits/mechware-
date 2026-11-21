using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.7 races over HTTP (ADR-0011 §9): two editors saving one draft with the same <c>rowVersion</c> at the same instant, and a save racing a
/// close. The quote and version rows are locked FOR NO KEY UPDATE and the token is compared under the lock, so exactly one save wins, the
/// other gets 409 <c>QUOTE_VERSION_CHANGED</c> (never a lost update, never a 500). Its own class, so its own login budget.
/// </summary>
[Collection(ExclusiveTimingCollection.Name)]
public sealed class StaffQuoteDraftConcurrencyTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private const int Rounds = 6;
    private readonly StaffQuoteKit _kit = new(factory);

    private static async Task<(HttpStatusCode Status, string? Code)[]> AtOnceAsync(params Func<Task<HttpResponseMessage>>[] calls)
    {
        using var start = new SemaphoreSlim(0);
        var tasks = calls.Select(async call =>
        {
            await start.WaitAsync();
            var r = await call();
            var body = await r.Content.ReadAsStringAsync();
            var code = r.IsSuccessStatusCode ? null : JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();
            return (r.StatusCode, code);
        }).ToList();
        start.Release(calls.Length);
        return await Task.WhenAll(tasks);
    }

    private static object Body(uint rowVersion, Guid variant, long price) =>
        new { rowVersion, validityDays = 15, lines = new[] { new { variantId = variant, quantity = 2, unitPriceMinor = price } } };

    [PostgresFact]
    public async Task TwoEditorsSavingOneDraftAtOnce_OneWins_TheOtherGets409_AndNoneIsA500()
    {
        var (a, b) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await _kit.Rfq.PersonAsync(Permissions.Roles.Admin));
        var owner = await _kit.BuyerAsync();
        var company = await _kit.Rfq.CompanyAsync(owner, "Draft Race Ltd", true);
        var variant = (await _kit.Rfq.VariantAsync(null)).VariantId;
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await CreateAsync(a.Client, new { companyId = company, contactUserId = owner });
            var rowVersion = (await DataAsync(await a.Client.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[0].GetProperty("rowVersion").GetUInt32();
            var url = $"/api/v1/admin/quotes/{quote}/versions/1";
            var results = await AtOnceAsync(() => a.Client.PutAsJsonAsync(url, Body(rowVersion, variant, 11_111)),
                () => b.Client.PutAsJsonAsync(url, Body(rowVersion, variant, 22_222)));

            results.Select(r => r.Status).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], $"round {round}");
            results.Single(r => r.Status == HttpStatusCode.Conflict).Code.Should().Be("QUOTE_VERSION_CHANGED", "the loser's token went stale under the lock");
            var winner = results[0].Status == HttpStatusCode.OK ? (a.Id, 11_111L) : (b.Id, 22_222L);
            await using var db = _kit.Db();
            var q = await LoadAsync(db, quote);
            q.Version(1).Lines.Single().UnitPriceMinor.Should().Be(winner.Item2, "the stored draft is the winner's, whole");
            q.Version(1).Contributors.Select(c => c.UserId).Should().BeEquivalentTo(new[] { a.Id, winner.Item1 }.Distinct(), "the loser left no trace");
            (await db.AuditLogs.CountAsync(x => x.Action == "quote.draft_changed" && x.EntityId == quote.ToString())).Should().Be(1);
        }
    }

    [PostgresFact]
    public async Task ASaveRacingACloseOfTheQuote_NeverWritesIntoAClosedQuote_AndNoneIsA500()
    {
        var (a, b) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await _kit.Rfq.PersonAsync(Permissions.Roles.Admin));
        var owner = await _kit.BuyerAsync();
        var company = await _kit.Rfq.CompanyAsync(owner, "Draft Race Close Ltd", true, (await _kit.BuyerAsync(), CompanyRoles.Buyer));
        var variant = (await _kit.Rfq.VariantAsync(null)).VariantId;
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await CreateAsync(a.Client, new { companyId = company, contactUserId = owner });
            var rowVersion = (await DataAsync(await a.Client.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[0].GetProperty("rowVersion").GetUInt32();
            var results = await AtOnceAsync(() => a.Client.PutAsJsonAsync($"/api/v1/admin/quotes/{quote}/versions/1", Body(rowVersion, variant, 33_333)),
                () => PostAsync(b.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Closed while editing" }));

            results.Should().NotContain(r => (int)r.Status >= 500, $"round {round}");
            results[1].Status.Should().Be(HttpStatusCode.OK, "closing always succeeds: before the save or after it");
            await using var db = _kit.Db();
            var lines = await db.QuoteVersions.Where(v => v.QuoteId == quote).SelectMany(v => v.Lines).CountAsync();
            (results[0].Status, results[0].Code, lines).Should().BeOneOf((HttpStatusCode.OK, null, 1), (HttpStatusCode.Conflict, "QUOTE_CLOSED", 0));
        }
    }
}
