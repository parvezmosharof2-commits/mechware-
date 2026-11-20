using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Approvals;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Companies;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Approvals;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Pricing;
using Mechware.Infrastructure.Quotes;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// Shared set-up of the D4.9 service tests (no HTTP, so no sign-ins): a company team with credit terms, priced catalogue items with a cost
/// under the 10 % floor, quotes written and sent through the real staff desk (D4.6-D4.8, no flag, so Sent), and acceptance through the real
/// <see cref="OrderService.PlaceFromQuoteAsync"/> with the real <see cref="QuoteAcceptanceGate"/>. Every service gets its own context.
/// </summary>
public sealed class QuoteAcceptKit(PostgresDatabaseFixture fixture)
{
    public const string Ip = "203.0.113.94";
    private static long _binSeq = 3_990_000_000;

    public sealed class Who(Guid id, string correlationId = "corr-accept") : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => Ip;
        public string? UserAgent => null;
        public string? CorrelationId => correlationId;
        public bool HasPermission(string permission) => true;
    }

    public sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static TimeProvider Clock(DateTime? at) => at is { } t ? new FixedClock(t) : TimeProvider.System;

    public MechwareDbContext Db() => fixture.CreateContext();

    public Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    public static string Key() => "acc-" + Guid.NewGuid().ToString("N");

    public sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers, Guid Approver, Guid Staff)
    {
        public Guid Buyer => Buyers[0];
    }

    /// <summary>
    /// An active company on Net-30 credit: an Admin, <paramref name="buyers"/> Buyers (the first is the quote contact), an Approver, and an
    /// outside staff user who writes its quotes. <paramref name="ruleFrom"/>: an approval rule from that amount, decided by the Approver.
    /// </summary>
    public async Task<Team> TeamAsync(int buyers = 1, long creditLimit = 100_000_000, long? ruleFrom = null, long? spendLimit = null)
    {
        var (admin, approver, staff) = (await UserAsync(), await UserAsync(), await UserAsync());
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await UserAsync());
        await using var db = Db();
        var companies = new CompanyService(db);
        var bin = (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Quote Buyers Ltd " + bin[^4..], "TL-D49-" + bin[^5..], bin, null, null))).Id;
        foreach (var buyer in buyerIds) await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, spendLimit, null));
        await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null));
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, creditLimit));
        if (ruleFrom is { } from)
            await new ApprovalService(db, TimeProvider.System).AddRuleAsync(admin, company, new ApprovalRuleRequest("Quotes from " + from, from, null, null, [approver]));
        await FloorAsync(db);
        return new Team(company, admin, buyerIds, approver, staff);
    }

    /// <summary>The owner's 10 % global floor (cost + 10 %), whatever an earlier test of this database left.</summary>
    private static async Task FloorAsync(MechwareDbContext db)
    {
        var global = await db.MarginFloors.FirstOrDefaultAsync(f => f.Scope == MarginFloorScope.Global);
        if (global is null) db.MarginFloors.Add(MarginFloor.Create(Guid.NewGuid(), MarginFloorScope.Global, null, null, 10m, Guid.NewGuid(), DateTime.UtcNow));
        else if (global.MinMarkupPercent != 10m) global.ChangeMarkup(10m, Guid.NewGuid(), DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>A sellable item; its net price is <see cref="Item.Net"/>, its cost half of that (so the floor is 55 % of the net price).</summary>
    public sealed record Item(Guid Variant, Guid Seller, Guid Category, long Net, Guid InventoryItem);

    public async Task<Item> ItemAsync(int onHand = 50, long grossMinor = 157_500)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: grossMinor);
        var row = await (from v in db.ProductVariants
                         join p in db.Products on v.ProductId equals p.Id
                         join c in db.Categories on p.CategoryId equals c.Id
                         where v.Id == seeded.VariantId
                         select new { v.Price, c.VatRate, p.SellerId, p.CategoryId }).SingleAsync();
        var net = PriceResolver.ToOrderBasis(row.Price.ToMinorUnits(), row.VatRate, TaxMode.Exclusive);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {net / 2} WHERE id = {seeded.VariantId}");
        return new Item(seeded.VariantId, row.SellerId, row.CategoryId, net, seeded.InventoryItemId);
    }

    public StaffQuoteService Desk(MechwareDbContext db, Guid staff, DateTime? at = null) =>
        new(db, new PriceListResolver(db), new PriceCheckFactsReader(db, new PricingOptions()), new QuoteOptions(), new Who(staff, "corr-desk"), Clock(at));

    public sealed record Line(Item Item, int Quantity = 2, long? Price = null);

    /// <summary>Fixed delivery to <paramref name="Site"/> at <paramref name="FeePerSeller"/> for every seller of the lines.</summary>
    public sealed record FixedDelivery(Guid Site, long FeePerSeller);

    /// <summary>A quote for the team (contact: the first Buyer) written and sent through the staff desk at <paramref name="at"/>; no flag, so Sent.</summary>
    public async Task<Guid> SentAsync(Team t, Line[] lines, string terms = "as_account", FixedDelivery? delivery = null, int validityDays = 15, DateTime? at = null)
    {
        await using var db = Db();
        var desk = Desk(db, t.Staff, at);
        var quote = (await desk.CreateQuoteAsync(new CreateStaffQuoteRequest(null, t.Company, t.Buyer))).Quote.Id;
        await EditAsync(desk, quote, 1, lines, terms, delivery, validityDays);
        (await desk.SendAsync(quote, 1)).Status.Should().Be("sent");
        return quote;
    }

    public static async Task EditAsync(StaffQuoteService desk, Guid quote, int n, Line[] lines, string terms = "as_account", FixedDelivery? delivery = null,
        int validityDays = 15)
    {
        var v = (await desk.GetQuoteAsync(quote)).Versions.Single(x => x.VersionNo == n);
        await desk.EditDraftAsync(quote, n, new EditQuoteDraftRequest(v.RowVersion, null, validityDays, terms, delivery is null ? "calculated" : "fixed",
            delivery?.Site, delivery is null ? null : [.. lines.Select(l => l.Item.Seller).Distinct().Select(s => new QuoteShippingFeeInput(s, delivery.FeePerSeller))],
            null, "Thank you for the request.", null, [.. lines.Select(l => new QuoteDraftLineInput(l.Item.Variant, l.Quantity, l.Price ?? l.Item.Net, null, null, null))]));
    }

    public async Task<QuoteVersion> VersionAsync(Guid quote, int n = 1)
    {
        await using var db = Db();
        return await db.QuoteVersions.AsNoTracking().Include(v => v.Lines).SingleAsync(v => v.QuoteId == quote && v.VersionNo == n);
    }

    public OrderService Orders(MechwareDbContext db, Guid actor, DateTime? at = null, CreditOptions? credit = null) =>
        new(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), Clock(at), ShippingTestKit.DevelopmentQuoter(db), new PriceListResolver(db),
            credit: credit ?? new CreditOptions(), quotes: new QuoteAcceptanceGate(db, new QuoteOptions(), new Who(actor)));

    /// <summary>
    /// The buyer's acceptance of version <paramref name="n"/>: by default the first Buyer, with the stored hash and, as the previewed total,
    /// the version's own total (what the accept preview shows while the VAT rate and the delivery fee are unchanged).
    /// </summary>
    public async Task<PlacedOrder> AcceptAsync(Team t, Guid quote, int n = 1, Guid? buyer = null, bool onCredit = false, string? key = null, string? hash = null,
        long? expected = null, DateTime? at = null, Guid? site = null, Guid? address = null, Guid? costCentre = null, Guid? company = null, string? po = null)
    {
        var v = await VersionAsync(quote, n);
        await using var db = Db();
        var who = buyer ?? t.Buyer;
        return await Orders(db, who, at).PlaceFromQuoteAsync(new AcceptQuoteRequest(who, key ?? Key(), company ?? t.Company, quote, n, hash ?? v.ContentHash,
            expected ?? v.GrandTotalMinor ?? v.GoodsTotalMinor, onCredit, site, address, costCentre, po));
    }

    public async Task<Domain.Orders.Order> OrderAsync(Guid id)
    {
        await using var db = Db();
        return await db.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(s => s.Items).SingleAsync(o => o.Id == id);
    }

    public async Task<Quote> QuoteAsync(Guid quote)
    {
        await using var db = Db();
        return await StaffQuoteKit.LoadAsync(db, quote);
    }

    /// <summary>Orders that name this quote, any status.</summary>
    public async Task<List<Domain.Orders.Order>> OrdersOfAsync(Guid quote)
    {
        await using var db = Db();
        return await db.Orders.AsNoTracking().Where(o => o.QuoteId == quote).ToListAsync();
    }

    public async Task<(int OnHand, int Reserved)> StockAsync(Item item)
    {
        await using var db = Db();
        var row = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == item.InventoryItem);
        return (row.OnHand, row.Reserved);
    }

    public async Task SqlAsync(FormattableString sql)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    public static async Task<string> CodeOf(Func<Task> act) => (await FluentActions.Awaiting(act).Should().ThrowAsync<Domain.Common.Exceptions.DomainException>()).Which.Code;
}
