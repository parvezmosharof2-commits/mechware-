using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Support;
using UglyToad.PdfPig;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.11 over HTTP: the quote PDF in English and Bangla (ADR-0011 §10, threat model QT1, QT9, QT17). Real host with its own class: the buyer route, the
/// staff route, the document check; lazy render, first writer wins, SHA-256 checked on every download, the same 404 as the reads, a draft preview that is
/// never stored. The PDF text is read back with PdfPig.
/// </summary>
public sealed class QuotePdfEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private const string NotFound = "NOT_FOUND|Quote not found.";
    private readonly QuoteIssueKit _kit = new(factory);

    private IObjectStorage Storage => factory.Services.GetRequiredService<IObjectStorage>();

    private Task<RfqApiKit.Person> PersonAsync(string name) => _kit.StaffAsync(Permissions.Roles.Buyer, name);

    private Task<RfqApiKit.Person> SupportAsync() => _kit.StaffAsync(Permissions.Roles.Support);

    private static string BuyerUrl(Guid company, Guid quote, int n = 1, string tail = "") => $"/api/v1/companies/{company}/quotes/{quote}/versions/{n}/pdf{tail}";

    private static string StaffUrl(Guid quote, int n = 1, string tail = "") => $"/api/v1/admin/quotes/{quote}/versions/{n}/pdf{tail}";

    private static string Key(Guid quote, string locale, int n = 1) => $"quotes/{quote:N}/v{n}-{locale}.pdf";

    /// <summary>The text of the file with the spaces taken out (PdfPig splits on glyph gaps, so only the letters are compared).</summary>
    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Concat(doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text)));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<(RfqApiKit.Person Buyer, QuoteIssueKit.World W, Guid Quote, string Number)> QuoteAsync(bool send = true, string company = "Pdf Quotes Ltd")
    {
        await _kit.TenPercentFloorAsync();
        var buyer = await PersonAsync("pdf-buyer-" + Guid.NewGuid().ToString("N")[..8]);
        var w = await _kit.CompanyAsync(company, (buyer.Id, CompanyRoles.Buyer));
        var item = await _kit.ItemAsync();
        var support = await SupportAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        if (send) await QuoteIssueKit.SentAsync(support.Client, quote, "sent");
        await using var db = _kit.Db();
        return (buyer, w, quote, await db.Quotes.Where(q => q.Id == quote).Select(q => q.Number).SingleAsync());
    }

    private async Task<(string? Key, string? Sha)> RecordedAsync(Guid quote, string locale, int n = 1)
    {
        await using var db = _kit.Db();
        var query = locale == "en"
            ? db.Database.SqlQuery<string>($"SELECT coalesce(pdf_en_key, '') || '|' || coalesce(pdf_en_sha256, '') AS \"Value\" FROM quote_versions WHERE quote_id = {quote} AND version_no = {n}")
            : db.Database.SqlQuery<string>($"SELECT coalesce(pdf_bn_key, '') || '|' || coalesce(pdf_bn_sha256, '') AS \"Value\" FROM quote_versions WHERE quote_id = {quote} AND version_no = {n}");
        var row = (await query.ToListAsync()).Single().Split('|');
        return (row[0].Length == 0 ? null : row[0], row[1].Length == 0 ? null : row[1]);
    }

    [PostgresFact]
    public async Task TheFirstDownloadRendersAndStoresTheFile_AuditsOnce_AndLaterOnesServeTheSameBytes()
    {
        var (buyer, w, quote, number) = await QuoteAsync();
        foreach (var locale in new[] { "en", "bn" })
        {
            var first = await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=" + locale));
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            first.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            first.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            (first.Content.Headers.ContentDisposition.FileNameStar ?? first.Content.Headers.ContentDisposition.FileName).Should().Be($"{number}-r1-{locale}.pdf");
            RfqApiKit.ShouldBePrivate(first);
            var bytes = await first.Content.ReadAsByteArrayAsync();
            bytes.Take(5).Should().Equal("%PDF-"u8.ToArray());

            var (key, sha) = await RecordedAsync(quote, locale);
            key.Should().Be(Key(quote, locale), "the server builds the key from ids");
            sha.Should().Be(Hex(bytes));
            await using (var stored = await Storage.OpenReadAsync(key!)) stored.Should().NotBeNull();

            (await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=" + locale))).Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
            (await _kit.AuditAsync(quote, "quote.pdf_rendered")).Should().HaveCount(locale == "en" ? 1 : 2, "one row per rendered file, not per download");
        }
        var rows = await _kit.AuditAsync(quote, "quote.pdf_rendered");
        rows.Select(r => r.NewValuesJson).Should().OnlyContain(v => v!.Contains(number));
        rows.Should().OnlyContain(r => r.ActorId == buyer.Id);
    }

    [PostgresFact]
    public async Task ThePdfIsInTheBuyersLanguage_BanglaLabelsAndDigits_AndEnglishByDefault()
    {
        var (buyer, w, quote, number) = await QuoteAsync();
        var en = Text(await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote))).Content.ReadAsByteArrayAsync());
        var bn = Text(await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=bn"))).Content.ReadAsByteArrayAsync());
        en.Should().Contain("Quotation").And.Contain("Amountinwords").And.Contain(number).And.Contain("MECHWARE").And.Contain("Termsandconditions");
        en.Should().Contain("Validuntil").And.Contain("Offervalidity:thisquotationisvaliduntil").And.NotContain("untilfr", "the validity line prints the version's real date, not a period");
        bn.Should().Contain("২০২৬").And.NotContain("Quotation").And.NotContain("Amountinwords");
        bn.Any(c => c is >= 'ঀ' and <= '৿').Should().BeTrue("the labels are Bangla");
        (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=xx"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task QuotePdf_UsesTheFrozenCompanyName_NotTheLiveOne()
    {
        var (buyer, w, quote, _) = await QuoteAsync(company: "Frozen Name Ltd");
        string frozen = (await _kit.LoadAsync(quote)).Version(1).CompanySnapshot!.Name;
        await using (var db = _kit.Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET legal_name = 'Renamed Afterwards Ltd' WHERE id = {w.Company}");

        var text = Text(await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote))).Content.ReadAsByteArrayAsync());
        text.Should().Contain(frozen.Replace(" ", "")).And.NotContain("RenamedAfterwards");
    }

    [PostgresFact]
    public async Task EveryDownloadChecksTheStoredFile_AMismatchIsNeverSent_AndIsAudited()
    {
        var (buyer, w, quote, _) = await QuoteAsync();
        var support = await SupportAsync();
        var good = await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote))).Content.ReadAsByteArrayAsync();
        var key = Key(quote, "en");
        var tampered = (byte[])good.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        await Storage.DeleteAsync(key);
        await Storage.PutAsync(key, new MemoryStream(tampered));

        foreach (var who in new[] { buyer.Client, support.Client })
        {
            var r = await who.GetAsync(who == buyer.Client ? BuyerUrl(w.Company, quote) : StaffUrl(quote));
            var (code, _) = await RfqApiKit.ErrorAsync(r, HttpStatusCode.ServiceUnavailable);
            code.Should().Be("QUOTE_PDF_UNAVAILABLE");
            r.Content.Headers.ContentType!.MediaType.Should().NotBe("application/pdf");
        }
        (await _kit.AuditAsync(quote, "quote.pdf_integrity_failed")).Should().HaveCount(2);
        (await RecordedAsync(quote, "en")).Sha.Should().Be(Hex(good), "the recorded hash is never rewritten");
    }

    [PostgresFact]
    public async Task AMissingStoredFile_IsRenderedAgainFromTheSnapshot_WithTheSameHash()
    {
        var (buyer, w, quote, _) = await QuoteAsync();
        var good = await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote))).Content.ReadAsByteArrayAsync();
        await Storage.DeleteAsync(Key(quote, "en"));

        var again = await buyer.Client.GetAsync(BuyerUrl(w.Company, quote));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadAsByteArrayAsync()).Should().Equal(good);
        await using (var stored = await Storage.OpenReadAsync(Key(quote, "en"))) stored.Should().NotBeNull("the file is put back");
        (await _kit.AuditAsync(quote, "quote.pdf_rendered")).Should().HaveCount(1);
    }

    [PostgresFact]
    public async Task ParallelFirstDownloads_RenderOneFile_AndEveryoneGetsTheSameBytes()
    {
        var (buyer, w, quote, _) = await QuoteAsync();
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=bn"))));
        answers.Select(a => a.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.OK);
        var bodies = await Task.WhenAll(answers.Select(a => a.Content.ReadAsByteArrayAsync()));
        bodies.Select(Hex).Distinct().Should().ContainSingle();
        (await RecordedAsync(quote, "bn")).Sha.Should().Be(Hex(bodies[0]));
        (await _kit.AuditAsync(quote, "quote.pdf_rendered")).Should().ContainSingle("the loser of the race writes no second row");
    }

    [PostgresFact]
    public async Task TheFileKeyComesFromTheServer_NeverFromTheRequest()
    {
        var (buyer, w, quote, _) = await QuoteAsync();
        foreach (var bad in new[] { "../../x", "en/../bn", "%2e%2e%2fx", "e%00n", "evil.pdf" })
            (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=" + bad))).StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);

        var ok = await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=EN&key=quotes/other/x.pdf&path=../../etc&sha256=" + new string('a', 64)));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RecordedAsync(quote, "en")).Key.Should().Be(Key(quote, "en"));
        (await RecordedAsync(quote, "bn")).Key.Should().BeNull();
        await using var stray = await Storage.OpenReadAsync("quotes/other/x.pdf");
        stray.Should().BeNull();
    }

    [PostgresFact]
    public async Task Idor_ThePdfOfAnotherCompany_Draft_OrUnknownVersion_IsTheSame404()
    {
        var (buyer, w, quote, _) = await QuoteAsync();
        var (outsider, lone, requester) = (await PersonAsync("pdf-outsider"), await PersonAsync("pdf-lone"), await PersonAsync("pdf-requester"));
        var support = await SupportAsync();
        var theirs = await _kit.CompanyAsync("Pdf Outsider Ltd", (outsider.Id, CompanyRoles.Admin | CompanyRoles.Buyer), (buyer.Id, CompanyRoles.Buyer));
        await _kit.CompanyAsync("Pdf Requester Ltd", (requester.Id, CompanyRoles.Requester));
        var item = await _kit.ItemAsync();
        var draft = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);

        var answers = new List<string>();
        async Task Add(HttpClient who, string url, string because) => answers.Add(await RfqApiKit.NotFoundShapeAsync(await who.GetAsync(url), because));
        await Add(outsider.Client, BuyerUrl(w.Company, quote), "another company's admin");
        await Add(outsider.Client, BuyerUrl(theirs.Company, quote), "under their own company");
        await Add(buyer.Client, BuyerUrl(theirs.Company, quote), "a member of both, under the other company");
        await Add(lone.Client, BuyerUrl(w.Company, quote), "a buyer of no company");
        await Add(support.Client, BuyerUrl(w.Company, quote), "platform staff on the buyer route");
        await Add(buyer.Client, BuyerUrl(w.Company, Guid.NewGuid()), "an unknown quote");
        await Add(buyer.Client, BuyerUrl(w.Company, quote, 9), "an unknown version");
        await Add(buyer.Client, BuyerUrl(w.Company, draft), "a quote that was never sent");
        await Add(buyer.Client, BuyerUrl(w.Company, quote, 0), "version zero");
        answers.Distinct().Should().ContainSingle().Which.Should().Be(NotFound);
        (await factory.CreateClient().GetAsync(BuyerUrl(w.Company, quote))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var db = _kit.Db();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM quote_versions WHERE quote_id = {quote} AND pdf_en_key IS NOT NULL").SingleAsync()).Should().Be(0, "no refused call rendered anything");
    }

    [PostgresFact]
    public async Task EveryPdfAnswer_IsPrivateNoStore_ErrorsToo()
    {
        var (buyer, w, quote, _) = await QuoteAsync();
        var support = await SupportAsync();
        foreach (var r in new[]
                 {
                     await buyer.Client.GetAsync(BuyerUrl(w.Company, quote)), await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=xx")),
                     await buyer.Client.GetAsync(BuyerUrl(w.Company, Guid.NewGuid())), await support.Client.GetAsync(StaffUrl(quote)),
                     await support.Client.GetAsync(StaffUrl(Guid.NewGuid())), await support.Client.GetAsync($"/api/v1/admin/quote-documents/{new string('b', 64)}"),
                 })
            r.Headers.CacheControl!.NoStore.Should().BeTrue((int)r.StatusCode + " " + r.RequestMessage!.RequestUri);
    }

    [PostgresFact]
    public async Task ADraftPreview_ForStaff_IsNeverStoredOrRecorded_AndSaysItIsNotAnOffer()
    {
        var (_, _, draft, _) = await QuoteAsync(send: false);
        var support = await SupportAsync();
        var r = await support.Client.GetAsync(StaffUrl(draft, tail: "?locale=en"));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Headers.GetValues("X-Quote-Pdf").Should().Equal("preview");
        Text(await r.Content.ReadAsByteArrayAsync()).Should().Contain("DRAFT,NOTANOFFER");

        (await RecordedAsync(draft, "en")).Should().Be((null, null));
        await using (var none = await Storage.OpenReadAsync(Key(draft, "en"))) none.Should().BeNull("a preview is not stored");
        (await _kit.AuditAsync(draft, "quote.pdf_rendered")).Should().BeEmpty();
        (await support.Client.GetAsync(StaffUrl(draft, 3))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task StaffGetTheSameFileAsTheBuyer_AndCheckADocumentBySha256()
    {
        var (buyer, w, quote, number) = await QuoteAsync();
        var support = await SupportAsync();
        var bought = await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote, tail: "?locale=bn"))).Content.ReadAsByteArrayAsync();
        var staff = await support.Client.GetAsync(StaffUrl(quote, tail: "?locale=bn"));
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        staff.Headers.Contains("X-Quote-Pdf").Should().BeFalse();
        (await staff.Content.ReadAsByteArrayAsync()).Should().Equal(bought);

        var check = await StaffQuoteKit.DataAsync(await support.Client.GetAsync("/api/v1/admin/quote-documents/" + Hex(bought).ToUpperInvariant()));
        check.GetProperty("number").GetString().Should().Be(number);
        check.GetProperty("versionNo").GetInt32().Should().Be(1);
        check.GetProperty("locale").GetString().Should().Be("bn");
        check.GetProperty("quoteId").GetGuid().Should().Be(quote);

        foreach (var sha in new[] { new string('0', 64), "xyz", Hex(bought)[..63] })
            (await support.Client.GetAsync("/api/v1/admin/quote-documents/" + sha)).StatusCode.Should().Be(HttpStatusCode.NotFound, sha);
        (await buyer.Client.GetAsync("/api/v1/admin/quote-documents/" + Hex(bought))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await buyer.Client.GetAsync(StaffUrl(quote))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [PostgresFact]
    public async Task QuoteTextWithMarkup_IsPrintedAsTextOnThePdf()
    {
        await _kit.TenPercentFloorAsync();
        var buyer = await PersonAsync("pdf-buyer-" + Guid.NewGuid().ToString("N")[..8]);
        var w = await _kit.CompanyAsync("Markup Ltd", (buyer.Id, CompanyRoles.Buyer));
        var support = await SupportAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)],
            header: new { termsText = "<b>Bold</b> & <script>alert(1)</script> ) Tj (", buyerNote = "See <a href=\"http://x\">here</a>" });
        await QuoteIssueKit.SentAsync(support.Client, quote, "sent");

        var text = Text(await (await buyer.Client.GetAsync(BuyerUrl(w.Company, quote))).Content.ReadAsByteArrayAsync());
        text.Should().Contain("<b>Bold</b>&<script>alert(1)</script>)Tj(").And.Contain("<ahref=\"http://x\">here</a>");
    }
}

/// <summary>D4.11, threat model QT8: the PDF is under the <c>QuoteRead</c> limit, per signed-in user, like the by-number lookup.</summary>
public sealed class QuotePdfRateLimitTests(QuoteReadLimitFactory factory) : IClassFixture<QuoteReadLimitFactory>
{
    [PostgresFact]
    public async Task QuotePdf_IsRateLimited()
    {
        var kit = new RfqApiKit(factory);
        var (buyer, other) = (await kit.PersonAsync(), await kit.PersonAsync());
        var company = await kit.CompanyAsync(buyer.Id, "Pdf Limit Ltd " + Guid.NewGuid().ToString("N")[..6], true);
        var url = $"/api/v1/companies/{company}/quotes/{Guid.NewGuid()}/versions/1/pdf";

        for (var i = 0; i < 3; i++) (await buyer.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await buyer.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await other.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the budget is per user");
    }
}
