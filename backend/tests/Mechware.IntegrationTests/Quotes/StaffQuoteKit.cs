using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// Shared set-up of the D4.6 staff desk tests: buyers that never sign in (only staff do, so each class stays far below the 25 logins a
/// minute), RFQs written with the domain as D4.5 would, and a quote sent with the domain because sending is D4.8.
/// </summary>
public sealed class StaffQuoteKit(DbBackedFactory factory)
{
    public RfqApiKit Rfq { get; } = new(factory);

    public MechwareDbContext Db() => Rfq.Db();

    /// <summary>A verified buyer account that never signs in.</summary>
    public async Task<Guid> BuyerAsync()
    {
        await using var db = Db();
        var user = User.Create($"rfqb.{Guid.NewGuid():N}"[..23] + "@example.test", null, IdentityTestKit.Hasher.HashPassword(RfqApiKit.Password), "Desk Buyer", "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.NewGuid(), Permissions.Roles.Buyer);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>An RFQ as D4.5 stores it: each line is (variant or null, quantity); a line without a variant did not match the catalogue.</summary>
    public async Task<Guid> RfqAsync(Guid company, Guid requester, params (Guid? Variant, int Quantity)[] lines)
    {
        await using var db = Db();
        var now = DateTime.UtcNow;
        var rfq = Mechware.Domain.Quotes.Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(now), company, requester, RfqSource.Text, "Please quote these", null, null,
            [.. lines.Select((l, i) => new RfqLineInput($"PART-{i + 1} x {l.Quantity}", $"PART{i + 1}", l.Quantity,
                l.Variant is null ? RfqMatchStatus.NotFound : RfqMatchStatus.Matched, l.Variant, null, null))],
            "desk-" + Guid.NewGuid().ToString("N"), new string('a', 64), now);
        db.Rfqs.Add(rfq);
        await db.SaveChangesAsync();
        return rfq.Id;
    }

    public static Task<Quote> LoadAsync(MechwareDbContext db, Guid id) => db.Quotes
        .Include(q => q.Versions.OrderBy(v => v.VersionNo)).ThenInclude(v => v.Lines)
        .Include(q => q.Versions).ThenInclude(v => v.Contributors)
        .AsSplitQuery().SingleAsync(q => q.Id == id);

    /// <summary>
    /// Sends version <paramref name="versionNo"/> as D4.8 will, at <paramref name="at"/>: cost 1, a 0 % floor and the buyer's price equal
    /// to the quote price, so no flag and the version is Sent (its check figures are then on the lines).
    /// </summary>
    public async Task SendAsync(Guid quoteId, int versionNo = 1, DateTime? at = null)
    {
        await using var db = Db();
        var quote = await LoadAsync(db, quoteId);
        var v = quote.Version(versionNo);
        var check = QuoteApprovalPolicy.Evaluate(v, [.. v.Lines.Select(l => new VariantCostFacts(l.VariantId, 1, null, [], l.VatRate))],
            [new MarginFloorRow(MarginFloorScope.Global, null, null, 0m)],
            [.. v.Lines.Select(l => new PriceResolution(l.VariantId, l.UnitPriceMinor, PriceSource.List, PriceBasis.NetOfVat, null, null, null, null, l.PublicUnitPriceMinor))],
            new QuoteOptions(), false);
        var facts = v.Lines.Select(l => new QuoteLineFacts(l.VariantId, true, l.SellerId, l.Sku, l.PartNumber, l.TitleEn, l.TitleBn, l.VatRate, l.PublicUnitPriceMinor, null)).ToList();
        quote.Send(versionNo, new QuoteIssue(new QuoteCompanySnapshot("Desk Buyer Ltd", null, "Dhaka"), "Rahim Uddin", facts, check, v.ShipTo),
            new QuoteActor(Guid.NewGuid(), []), false, new QuoteOptions(), at ?? DateTime.UtcNow).Should().Be(QuoteVersionStatus.Sent);
        await db.SaveChangesAsync();
    }

    public static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK) => await RfqApiKit.DataAsync(r, expected);

    public static async Task<string> CodeAsync(HttpResponseMessage r, HttpStatusCode expected) => (await RfqApiKit.ErrorAsync(r, expected)).Code;

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object? body = null) =>
        client.PostAsJsonAsync(url, body ?? new { });

    /// <summary>Creates a quote over HTTP and returns its id.</summary>
    public static async Task<Guid> CreateAsync(HttpClient staff, object body) =>
        (await DataAsync(await PostAsync(staff, "/api/v1/admin/quotes", body), HttpStatusCode.Created)).GetProperty("quote").GetProperty("id").GetGuid();
}
