using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Companies;
using Mechware.Application.Orders;
using Mechware.Application.Pricing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Pricing;
using Mechware.IntegrationTests.Support;
using Entry = Mechware.IntegrationTests.Support.PriceListTestData.Entry;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// D2.5 / D2.6: orders priced through <see cref="IPriceResolver"/> (ADR-0008 §5–§10; threat model §৮ PL6, PL7, PL13–PL16,
/// PL18). The catalogue price 1,050.00 at 5 % VAT is 1,000.00 net for a company (owner decision 2), so the numbers stay
/// readable. Lists are approved two hours before the orders' instant: started, and inside their window.
/// </summary>
public class OrderPriceListTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const long Catalogue = 105_000;     // VAT-inclusive; 100,000 net at 5 %
    private static readonly DateTime Now = PriceListTestData.WholeSecond(DateTime.UtcNow);
    private static readonly DateTime ApprovedAt = Now.AddHours(-2);
    private static readonly DateTime ValidFrom = Now.AddHours(-3);
    private static readonly DateTime ValidTo = Now.AddDays(30);
    private static long _binSeq;

    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<T> With<T>(Func<OrderService, Task<T>> action, DateTime? now = null, Func<MechwareDbContext, IPriceResolver>? resolver = null)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), new FixedClock(now ?? Now),
            ShippingTestKit.DevelopmentQuoter(db), resolver?.Invoke(db) ?? new PriceListResolver(db)));
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<Guid> VariantAsync(long price = Catalogue, decimal? vat = null, int onHand = 500)
    {
        await using var db = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
        if (vat is { } rate)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE categories SET vat_rate = {rate} WHERE id = (SELECT category_id FROM products WHERE id = {seeded.ProductId})");
        return seeded.VariantId;
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    /// <summary>A verified company with these members (accepted), each with the given roles.</summary>
    private async Task<Guid> CompanyAsync(params (Guid User, CompanyRoles Roles)[] members)
    {
        var admin = await UserAsync();
        await using var db = fixture.CreateContext();
        var svc = new CompanyService(db);
        var bin = (Interlocked.Increment(ref _binSeq) + 6_250_000_000_000L).ToString("D13");
        var company = await svc.RegisterAsync(admin, new RegisterCompanyRequest("Contract Buyer Ltd", "TL-D25-" + bin[^4..], bin, null, null));
        foreach (var (user, roles) in members) await svc.AddAcceptedMemberAsync(admin, company.Id, new AddMemberRequest(user, roles, null, null));
        await svc.AdminActivateAsync(company.Id);
        return company.Id;
    }

    private async Task<PriceList> ContractAsync(Guid company, params Entry[] entries)
    {
        await using var db = fixture.CreateContext();
        return await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, company, ValidFrom, ValidTo, ApprovedAt, entries);
    }

    private async Task WithdrawAsync(Guid listId, DateTime at)
    {
        await using var db = fixture.CreateContext();
        await PriceListTestData.WithdrawAsync(db, listId, at);
    }

    private static PlaceOrderRequest Order(Guid buyer, Guid? company, params (Guid Variant, int Qty)[] lines) =>
        new(buyer, Key(), lines.Select(l => new PlaceOrderLine(l.Variant, l.Qty)).ToList(), null, company);

    private async Task<OrderItem> ItemAsync(Guid orderId, Guid variant)
    {
        await using var db = fixture.CreateContext();
        return await db.OrderItems.AsNoTracking()
            .SingleAsync(i => i.VariantId == variant && db.SubOrders.Any(s => s.Id == i.SubOrderId && s.OrderId == orderId));
    }

    // ---- D2.5: the seam --------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ACompanyWithACheaperContractEntry_PaysTheContractPrice_AndTheBuyerSeesItsSource()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var list = await ContractAsync(company, new Entry(v, 1, 90_000));

        var placed = await With(s => s.PlaceAsync(Order(buyer, company, (v, 2))));

        placed.GrandTotalMinor.Should().Be(189_000, "2 × 900.00 contract (net) + 5 % VAT");
        var item = await ItemAsync(placed.OrderId, v);
        (item.UnitPriceMinor, item.NetMinor, item.TaxMinor, item.TotalMinor).Should().Be((90_000, 180_000, 9_000, 189_000));
        (item.PriceSource, item.PriceBasis, item.PriceListId, item.PriceListVersion, item.PriceListEntryId, item.ContractRef, item.PublicUnitPriceMinor)
            .Should().Be((PriceSource.Contract, PriceBasis.NetOfVat, list.Id, 1, list.Entries.Single().Id, list.ContractRef, 100_000));

        var dto = await With(s => s.GetAsync(buyer, placed.OrderId));
        var line = dto.SubOrders.Single().Items.Single();
        (line.PriceSource, line.ContractRef, line.UnitPriceMinor).Should().Be(("contract", list.ContractRef, 90_000));
    }

    [PostgresFact]
    public async Task AContractPriceAboveThePublicPrice_Loses_RuleA()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await ContractAsync(company, new Entry(v, 1, 100_001));

        var placed = await With(s => s.PlaceAsync(Order(buyer, company, (v, 1))));

        placed.GrandTotalMinor.Should().Be(Catalogue, "the public price (1,000.00 net + 5 % VAT) is lower, and the lowest valid price wins");
        var item = await ItemAsync(placed.OrderId, v);
        (item.UnitPriceMinor, item.PriceSource, item.PriceListId, item.ContractRef).Should().Be((100_000, PriceSource.List, (Guid?)null, (string?)null));
    }

    [PostgresFact]
    public async Task ABreakTable_PicksTheBreakWithTheLargestMinQtyNotAboveTheQuantity()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var g = await VariantAsync();
        await ContractAsync(company, new Entry(v, 1, 95_000), new Entry(v, 10, 90_000), new Entry(v, 50, 85_000));
        await using (var db = fixture.CreateContext())
            await PriceListTestData.ApprovedAsync(db, PriceListScope.General, null, ValidFrom, ValidTo, ApprovedAt, new Entry(g, 5, 99_750));

        // Each order is cancelled after reading it: a buyer may hold only a few unpaid orders at once.
        async Task<OrderItem> PricedAsync(Guid? forCompany, Guid variant, int qty)
        {
            var placed = await With(s => s.PlaceAsync(Order(buyer, forCompany, (variant, qty))));
            var item = await ItemAsync(placed.OrderId, variant);
            await With(s => s.CancelAsync(buyer, placed.OrderId, "Only checking the price"));
            return item;
        }

        foreach (var (qty, unit) in new[] { (9, 95_000L), (10, 90_000L), (49, 90_000L), (60, 85_000L) })
            (await PricedAsync(company, v, qty)).UnitPriceMinor.Should().Be(unit, $"{qty} units");

        // A public break (VAT-inclusive) applies to a guest or personal order from its quantity on.
        var four = await PricedAsync(null, g, 4);
        var five = await PricedAsync(null, g, 5);
        (four.UnitPriceMinor, four.PriceSource).Should().Be((Catalogue, PriceSource.List));
        (five.UnitPriceMinor, five.PriceSource, five.TotalMinor).Should().Be((99_750, PriceSource.QuantityBreak, 5 * 99_750));
    }

    [PostgresFact]
    public async Task AListOutsideItsWindow_OrADraftOrPendingList_IsNeverUsed()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await using (var db = fixture.CreateContext())
        {
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, company, ValidFrom, Now.AddMinutes(-1), ApprovedAt, new Entry(v, 1, 50_000)); // ended
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, company, Now.AddHours(1), ValidTo, ApprovedAt, new Entry(v, 1, 51_000));  // not yet
            await PriceListTestData.DraftAsync(db, PriceListScope.Company, company, ValidFrom, ValidTo, ApprovedAt, new Entry(v, 1, 52_000));
            await PriceListTestData.PendingAsync(db, PriceListScope.Company, company, ValidFrom, ValidTo, ApprovedAt, new Entry(v, 1, 53_000));
        }

        var placed = await With(s => s.PlaceAsync(Order(buyer, company, (v, 1))));

        placed.GrandTotalMinor.Should().Be(Catalogue);
        (await ItemAsync(placed.OrderId, v)).PriceSource.Should().Be(PriceSource.List);
    }

    [PostgresFact]
    public async Task GuestOrPersonalOrder_NeverGetsACompanyPrice()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await ContractAsync(company, new Entry(v, 1, 50_000));

        // The same person, ordering in their own name: VAT-inclusive public price, no list, no contract.
        var personal = await With(s => s.PlaceAsync(Order(buyer, null, (v, 1))));
        var quote = await With(s => s.QuoteShippingAsync(new ShippingQuoteRequest(buyer, [new PlaceOrderLine(v, 1)])));

        (personal.GrandTotalMinor, quote.GrandTotalMinor).Should().Be((Catalogue, Catalogue));
        var item = await ItemAsync(personal.OrderId, v);
        (item.UnitPriceMinor, item.PriceSource, item.PriceBasis, item.PriceListId, item.ContractRef)
            .Should().Be((Catalogue, PriceSource.List, PriceBasis.GrossInclVat, (Guid?)null, (string?)null));
    }

    [PostgresFact]
    public async Task MemberOfTwoCompanies_GetsOnlyTheOrderingCompanysPrices()
    {
        var buyer = await UserAsync();
        var a = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var b = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var requesterOnly = await CompanyAsync((buyer, CompanyRoles.Requester));
        var stranger = await CompanyAsync();
        var v = await VariantAsync();
        await ContractAsync(a, new Entry(v, 1, 80_000));
        await ContractAsync(b, new Entry(v, 1, 70_000));
        await ContractAsync(requesterOnly, new Entry(v, 1, 60_000));
        await ContractAsync(stranger, new Entry(v, 1, 50_000));

        var forA = await With(s => s.PlaceAsync(Order(buyer, a, (v, 1))));
        var forB = await With(s => s.PlaceAsync(Order(buyer, b, (v, 1))));
        (await ItemAsync(forA.OrderId, v)).UnitPriceMinor.Should().Be(80_000, "company A's contract, not the cheaper one of B");
        (await ItemAsync(forB.OrderId, v)).UnitPriceMinor.Should().Be(70_000);

        // A company id the membership check does not back never reaches the resolver: no order, no quote, no price.
        (await FluentActions.Awaiting(() => With(s => s.QuoteShippingAsync(new ShippingQuoteRequest(buyer, [new PlaceOrderLine(v, 1)], CompanyId: requesterOnly))))
            .Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("COMPANY_ROLE_REQUIRED");
        await FluentActions.Awaiting(() => With(s => s.QuoteShippingAsync(new ShippingQuoteRequest(buyer, [new PlaceOrderLine(v, 1)], CompanyId: stranger))))
            .Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Awaiting(() => With(s => s.PlaceAsync(Order(buyer, stranger, (v, 1))))).Should().ThrowAsync<KeyNotFoundException>();
    }

    [PostgresFact]
    public async Task AnOrderKeepsThePriceListVersionItWasPlacedWith()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var v1 = await ContractAsync(company, new Entry(v, 1, 90_000));
        var first = await With(s => s.PlaceAsync(Order(buyer, company, (v, 1))));

        PriceList v2;
        await using (var db = fixture.CreateContext())
            v2 = await PriceListTestData.ReviseAsync(db, v1.Id, Now.AddMinutes(1), [new Entry(v, 1, 85_000)]);   // starts at Now + 2 min
        var second = await With(s => s.PlaceAsync(Order(buyer, company, (v, 1))), Now.AddMinutes(5));

        var a = await ItemAsync(first.OrderId, v);
        var b = await ItemAsync(second.OrderId, v);
        (a.UnitPriceMinor, a.PriceListId, a.PriceListVersion, a.PriceListEntryId).Should().Be((90_000, v1.Id, 1, v1.Entries.Single().Id));
        (b.UnitPriceMinor, b.PriceListId, b.PriceListVersion, b.PriceListEntryId).Should().Be((85_000, v2.Id, 2, v2.Entries.Single().Id));
        (await With(s => s.GetAsync(buyer, first.OrderId))).GrandTotalMinor.Should().Be(94_500, "the first order still has version 1's price");
    }

    [PostgresFact]
    public async Task WithdrawingAList_DoesNotChangePlacedOrders()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var list = await ContractAsync(company, new Entry(v, 1, 90_000));
        var placed = await With(s => s.PlaceAsync(Order(buyer, company, (v, 2))));
        var before = await With(s => s.GetAsync(buyer, placed.OrderId));

        await WithdrawAsync(list.Id, Now.AddMinutes(1));

        var after = await With(s => s.GetAsync(buyer, placed.OrderId), Now.AddMinutes(2));
        after.Should().BeEquivalentTo(before, "a placed order is never re-priced");
        (await ItemAsync(placed.OrderId, v)).PriceListId.Should().Be(list.Id);

        // A new order no longer gets the withdrawn price.
        var later = await With(s => s.PlaceAsync(Order(buyer, company, (v, 2))), Now.AddMinutes(2));
        later.GrandTotalMinor.Should().Be(2 * Catalogue);
        (await ItemAsync(later.OrderId, v)).PriceSource.Should().Be(PriceSource.List);
    }

    /// <summary>
    /// The adapter loads every Approved and Withdrawn version, never filtered by time in SQL (D2.2 HANDOFF): a later version that
    /// started and was then withdrawn, or has expired, keeps superseding the older, cheaper one (ADR-0008 §3, PL14).
    /// </summary>
    [PostgresFact]
    public async Task AWithdrawnOrExpiredLaterVersion_NeverBringsBackTheOlderCheaperOne()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var (withdrawn, expired) = (await VariantAsync(), await VariantAsync());
        var w1 = await ContractAsync(company, new Entry(withdrawn, 1, 80_000));
        var e1 = await ContractAsync(company, new Entry(expired, 1, 80_000));
        await using (var db = fixture.CreateContext())
        {
            var w2 = await PriceListTestData.ReviseAsync(db, w1.Id, Now.AddMinutes(-90), [new Entry(withdrawn, 1, 95_000)]);
            await PriceListTestData.WithdrawAsync(db, w2.Id, Now.AddMinutes(-30));
            await PriceListTestData.ReviseAsync(db, e1.Id, Now.AddMinutes(-90), [new Entry(expired, 1, 95_000)], validTo: Now.AddMinutes(-1));
        }

        var placed = await With(s => s.PlaceAsync(Order(buyer, company, (withdrawn, 1), (expired, 1))));

        (await ItemAsync(placed.OrderId, withdrawn)).PriceSource.Should().Be(PriceSource.List, "version 2 started, so its withdrawal leaves the public price");
        (await ItemAsync(placed.OrderId, expired)).PriceSource.Should().Be(PriceSource.List, "version 2 started, so its expiry leaves the public price");
        placed.GrandTotalMinor.Should().Be(2 * Catalogue);
    }

    [PostgresFact]
    public async Task IdempotentReplay_ReturnsTheOriginalPrices()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var list = await ContractAsync(company, new Entry(v, 1, 90_000));
        var request = Order(buyer, company, (v, 1));
        var first = await With(s => s.PlaceAsync(request));

        await WithdrawAsync(list.Id, Now.AddMinutes(1));
        var again = await With(s => s.PlaceAsync(request), Now.AddMinutes(2));

        (again.Replayed, again.OrderId, again.GrandTotalMinor).Should().Be((true, first.OrderId, 94_500));
        await using var db = fixture.CreateContext();
        (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(1);
    }

    [PostgresFact]
    public async Task ResolvingAgainAtTheOrdersInstant_GivesTheSamePriceAndSource()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var (contract, plain) = (await VariantAsync(), await VariantAsync(price: 99_999, vat: 0.075m));
        var list = await ContractAsync(company, new Entry(contract, 1, 90_000), new Entry(contract, 5, 88_000));
        var placed = await With(s => s.PlaceAsync(Order(buyer, company, (contract, 6), (plain, 3))));
        // A later version approved after the order starts later still (approval + 60 s): it cannot change that instant.
        await using (var db = fixture.CreateContext())
            await PriceListTestData.ReviseAsync(db, list.Id, Now.AddSeconds(1), [new Entry(contract, 5, 80_000)]);

        await using var check = fixture.CreateContext();
        var order = await check.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(s => s.Items).SingleAsync(o => o.Id == placed.OrderId);
        var items = order.SubOrders.SelectMany(s => s.Items).ToList();
        var facts = await (from x in check.ProductVariants.AsNoTracking()
                           join p in check.Products.AsNoTracking() on x.ProductId equals p.Id
                           join c in check.Categories.AsNoTracking() on p.CategoryId equals c.Id
                           where x.Id == contract || x.Id == plain
                           select new { Variant = x, c.VatRate }).ToListAsync();
        var again = await new PriceListResolver(check).ResolveAsync(company,
            items.Select(i => new PriceRequestLine(i.VariantId, i.Quantity, PricingInput.From(facts.Single(f => f.Variant.Id == i.VariantId).Variant,
                facts.Single(f => f.Variant.Id == i.VariantId).VatRate))).ToList(), TaxMode.Exclusive, order.PlacedAtUtc);

        foreach (var i in items)
        {
            var r = again.Single(x => x.Price.VariantId == i.VariantId).Price;
            (r.UnitPriceMinor, r.Source, r.PriceListId, r.PriceListVersion, r.EntryId, r.PublicUnitPriceMinor)
                .Should().Be((i.UnitPriceMinor, i.PriceSource!.Value, i.PriceListId, i.PriceListVersion, i.PriceListEntryId, i.PublicUnitPriceMinor!.Value));
        }

        items.Single(i => i.VariantId == contract).UnitPriceMinor.Should().Be(88_000, "6 units: the break from 5");
        items.Single(i => i.VariantId == plain).PriceSource.Should().Be(PriceSource.List);
    }

    [PostgresFact]
    public async Task ACompanyPaysWhatAGuestPays_ForTheSameCataloguePricedCart()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var (a, b, c) = (await VariantAsync(), await VariantAsync(price: 500_000, vat: 0.15m), await VariantAsync(price: 99_999, vat: 0.075m));
        (Guid, int)[] cart = [(a, 1), (b, 1), (c, 3)];

        var guest = await With(s => s.PlaceAsync(Order(buyer, null, cart)));
        var forCompany = await With(s => s.PlaceAsync(Order(buyer, company, cart)));

        guest.GrandTotalMinor.Should().Be(105_000 + 500_000 + 3 * 99_999);
        // Owner decision 2: net = floor(gross / (1 + VAT)) per unit, VAT added per line. 1,050.00 at 5 % is exact; 5,000.00 at
        // 15 % is 4,347.82 + 652.17; 3 × 999.99 at 7.5 % is 2,790.66 + 209.30. Never more than a guest, at most a few poisha less.
        forCompany.GrandTotalMinor.Should().Be(105_000 + 499_999 + 299_996);
        (await ItemAsync(forCompany.OrderId, a)).TotalMinor.Should().Be(105_000);
    }

    [PostgresFact]
    public async Task TheShippingQuote_TotalsLikeThePlacedOrder_WithContractLines()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var (contract, plain) = (await VariantAsync(), await VariantAsync(price: 250_000, vat: 0.15m));
        await ContractAsync(company, new Entry(contract, 1, 91_234));
        var lines = new List<PlaceOrderLine> { new(contract, 3), new(plain, 2) };

        var quote = await With(s => s.QuoteShippingAsync(new ShippingQuoteRequest(buyer, lines, CompanyId: company)));
        var placed = await With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), lines, null, company)));
        var order = await With(s => s.GetAsync(buyer, placed.OrderId));

        (quote.TaxMinor, quote.GrandTotalMinor).Should().Be((order.TaxMinor, order.GrandTotalMinor), "the review step shows what placement charges");
        order.SubOrders.SelectMany(s => s.Items).Single(i => i.VariantId == contract).PriceSource.Should().Be("contract");
    }

    // ---- D2.6: guards ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Coupon_DoesNotApplyToContractPricedLines()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var (contract, plain) = (await VariantAsync(), await VariantAsync());
        await ContractAsync(company, new Entry(contract, 1, 90_000));
        var code = "D25" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using (var db = fixture.CreateContext())
        {
            db.Promotions.Add(PromotionDefinition.Create(Guid.NewGuid(), "Ten off", code, PromotionKind.PercentOff, PromotionScope.Everything, percent: 10m));
            await db.SaveChangesAsync();
        }

        var mixed = await With(s => s.PlaceAsync(Order(buyer, company, (contract, 1), (plain, 1)) with { CouponCodes = [code] }));
        (await ItemAsync(mixed.OrderId, contract)).DiscountMinor.Should().Be(0, "a negotiated price takes no promotion (ADR-0008 §6)");
        (await ItemAsync(mixed.OrderId, plain)).DiscountMinor.Should().Be(10_000, "10 % of the 1,000.00 net public price");

        (await FluentActions.Awaiting(() => With(s => s.PlaceAsync(Order(buyer, company, (contract, 1)) with { CouponCodes = [code] })))
            .Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("COUPON_NO_ELIGIBLE_LINES");

        var personal = await With(s => s.PlaceAsync(Order(buyer, null, (contract, 1)) with { CouponCodes = [code] }));
        (await ItemAsync(personal.OrderId, contract)).DiscountMinor.Should().Be(10_500, "the same item at the public price takes the coupon");
    }

    [PostgresFact]
    public async Task ClientExpectedTotal_CanOnlyRefuseAnOrder_NeverLowerItsPrice()
    {
        var buyer = await UserAsync();
        var v = await VariantAsync(onHand: 10);

        (await FluentActions.Awaiting(() => With(s => s.PlaceAsync(Order(buyer, null, (v, 1)) with { ExpectedGrandTotalMinor = Catalogue - 1 })))
            .Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("PRICE_CHANGED");
        await using (var db = fixture.CreateContext())
        {
            (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(0, "a refused order writes nothing");
            (await db.InventoryItems.SingleAsync(i => i.VariantId == v)).Reserved.Should().Be(0, "and holds no stock");
        }

        var higher = await With(s => s.PlaceAsync(Order(buyer, null, (v, 1)) with { ExpectedGrandTotalMinor = 1_000_000 }));
        var equal = await With(s => s.PlaceAsync(Order(buyer, null, (v, 1)) with { ExpectedGrandTotalMinor = Catalogue }));
        (higher.GrandTotalMinor, equal.GrandTotalMinor).Should().Be((Catalogue, Catalogue), "the server's price, never the number the client sent");
    }

    [PostgresFact]
    public async Task ListExpiredBetweenCartAndCheckout_OrderIsRefusedWithPriceChanged()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await using (var db = fixture.CreateContext())
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, company, ValidFrom, Now.AddMinutes(10), ApprovedAt, new Entry(v, 1, 90_000));
        var shown = await With(s => s.QuoteShippingAsync(new ShippingQuoteRequest(buyer, [new PlaceOrderLine(v, 1)], CompanyId: company)));
        shown.GrandTotalMinor.Should().Be(94_500);

        var atExpiry = Now.AddMinutes(10);   // the window is [from, to): expired is expired, no grace period
        (await FluentActions.Awaiting(() => With(s => s.PlaceAsync(Order(buyer, company, (v, 1)) with { ExpectedGrandTotalMinor = shown.GrandTotalMinor }), atExpiry))
            .Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("PRICE_CHANGED");

        // The buyer confirms the new total; without the field the order would simply be priced at the new price.
        var confirmed = await With(s => s.PlaceAsync(Order(buyer, company, (v, 1)) with { ExpectedGrandTotalMinor = Catalogue }), atExpiry);
        confirmed.GrandTotalMinor.Should().Be(Catalogue);
    }

    [PostgresFact]
    public async Task WithdrawRacingAnOrder_NoOrderCommittedAfterTheWithdrawalCarriesItsPrice()
    {
        var buyer = await UserAsync();
        var company = await CompanyAsync((buyer, CompanyRoles.Buyer));
        var v = await VariantAsync(onHand: 10);

        // 1. The withdrawal commits after the order was priced and before it is saved: the save re-reads the list and refuses.
        var first = await ContractAsync(company, new Entry(v, 1, 90_000));
        var act = () => With(s => s.PlaceAsync(Order(buyer, company, (v, 1))),
            resolver: db => new AfterResolving(new PriceListResolver(db), () => WithdrawAsync(first.Id, Now)));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("PRICE_CHANGED");

        // 2. The withdrawal holds the row (not committed yet) while the order saves: the order waits for it, then refuses.
        var second = await ContractAsync(company, new Entry(v, 1, 89_000));
        await using (var withdrawer = fixture.CreateContext())
        {
            await using var tx = await withdrawer.Database.BeginTransactionAsync();
            (await PriceListTestData.LoadAsync(withdrawer, second.Id)).Withdraw(Guid.NewGuid(), "Withdrawn while an order is saved", Now);
            await withdrawer.SaveChangesAsync();

            var order = Task.Run(() => With(s => s.PlaceAsync(Order(buyer, company, (v, 1)))));
            await WaitUntilAnOrderWaitsForAPriceListAsync(order);
            await tx.CommitAsync();
            (await FluentActions.Awaiting(() => order).Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("PRICE_CHANGED");
        }

        await using var check = fixture.CreateContext();
        (await check.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(0);
        (await check.OrderItems.CountAsync(i => i.PriceListId == first.Id || i.PriceListId == second.Id)).Should().Be(0);
        (await check.InventoryItems.SingleAsync(i => i.VariantId == v)).Reserved.Should().Be(0, "refused orders give their stock back");

        // Control: with no withdrawal the same order goes through at the contract price.
        await ContractAsync(company, new Entry(v, 1, 88_000));
        (await With(s => s.PlaceAsync(Order(buyer, company, (v, 1))))).GrandTotalMinor.Should().Be(92_400);
    }

    /// <summary>Polls until a session waits on a row lock in the order's <c>FOR SHARE</c> re-read; fails if the order finished instead.</summary>
    private async Task WaitUntilAnOrderWaitsForAPriceListAsync(Task order)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        for (var i = 0; i < 200 && !order.IsCompleted; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%price_lists%FOR SHARE%'", conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(50);
        }

        throw new InvalidOperationException(order.IsCompleted
            ? "The order finished without waiting for the withdrawal's row lock."
            : "No order waited for the price list row within 10 seconds.");
    }

    /// <summary>Runs <paramref name="between"/> after the prices were resolved and before the order is saved.</summary>
    private sealed class AfterResolving(IPriceResolver inner, Func<Task> between) : IPriceResolver
    {
        public async Task<IReadOnlyList<ResolvedPrice>> ResolveAsync(
            Guid? verifiedCompanyId, IReadOnlyList<PriceRequestLine> lines, TaxMode mode, DateTime asOfUtc, CancellationToken ct = default)
        {
            var resolved = await inner.ResolveAsync(verifiedCompanyId, lines, mode, asOfUtc, ct);
            await between();
            return resolved;
        }
    }
}
