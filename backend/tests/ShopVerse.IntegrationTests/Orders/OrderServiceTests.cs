using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Domain.Orders;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

public class OrderServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<Guid> NewBuyerAsync() => await TestUsers.CreateAsync(fixture);

    private async Task<T> With<T>(Func<OrderService, Task<T>> action, Func<DateTime, string>? orderNumbers = null)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System, orderNumbers));
    }

    private async Task<SeededVariant> SeedAsync(int onHand, long price = 150_000)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    private static PlaceOrderRequest Req(Guid user, string key, params (Guid Variant, int Qty)[] lines) =>
        new(user, key, lines.Select(l => new PlaceOrderLine(l.Variant, l.Qty)).ToList());

    private async Task<(int Reserved, int OnHand)> StockAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == variantId);
        return (item.Reserved, item.OnHand);
    }

    [PostgresFact]
    public async Task PlacingAnOrder_PricesItFromTheDatabase_ReservesStock_AndRecordsEverything()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(onHand: 10, price: 150_000);

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 3))));

        // Guest checkout is VAT-inclusive: 3 x 1,500.00 stays 4,500.00 with the 5 % VAT inside it.
        placed.GrandTotalMinor.Should().Be(450_000);
        placed.Status.Should().Be("pending_payment");
        placed.Replayed.Should().BeFalse();
        OrderNumber.IsValid(placed.OrderNumber).Should().BeTrue();
        (await StockAsync(v.VariantId)).Reserved.Should().Be(3);

        await using var db = fixture.CreateContext();
        var order = await db.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(s => s.Items).Include(o => o.Events).SingleAsync(o => o.Id == placed.OrderId);
        order.UserId.Should().Be(buyer);
        order.SubOrders.Should().ContainSingle();
        var item = order.SubOrders.Single().Items.Single();
        (item.Quantity, item.UnitPriceMinor, item.TotalMinor, item.TaxMinor, item.NetMinor).Should().Be((3, 150_000, 450_000, 21_429, 428_571));
        order.GrandTotalMinor.Should().Be(order.SubOrders.Sum(s => s.TotalMinor));
        order.Events.Should().ContainSingle().Which.Reason.Should().Be("Order placed");
        order.SubOrders.Single().CommissionMinor.Should().Be(order.SubOrders.Single().GoodsNetMinor - order.SubOrders.Single().SellerEarningMinor);
    }

    [PostgresFact]
    public async Task ACartWithTwoSellers_BecomesOneOrderWithTwoSubOrders()
    {
        var buyer = await NewBuyerAsync();
        var a = await SeedAsync(5, 100_000);
        var b = await SeedAsync(5, 50_000);

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (a.VariantId, 1), (b.VariantId, 2))));

        await using var db = fixture.CreateContext();
        var order = await db.Orders.AsNoTracking().Include(o => o.SubOrders).SingleAsync(o => o.Id == placed.OrderId);
        order.SubOrders.Should().HaveCount(2);
        order.SubOrders.Select(s => s.SubOrderNumber).Should().BeEquivalentTo([$"{order.OrderNumber}-01", $"{order.OrderNumber}-02"]);   // a join has no row order
        order.GrandTotalMinor.Should().Be(200_000);
        order.SubOrders.Sum(s => s.TotalMinor).Should().Be(order.GrandTotalMinor);
    }

    [PostgresFact]
    public async Task TheSameKeyAndRequest_ReplaysTheFirstOrder_WithoutReservingAgain()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10);
        var key = Key();

        var first = await With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2))));
        var second = await With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2))));

        second.OrderId.Should().Be(first.OrderId);
        second.OrderNumber.Should().Be(first.OrderNumber);
        second.Replayed.Should().BeTrue();
        (await StockAsync(v.VariantId)).Reserved.Should().Be(2, "a retry must not hold stock twice");
        await using var db = fixture.CreateContext();
        (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TheSameKeyWithADifferentRequest_IsRefused()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10);
        var key = Key();
        await With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2))));

        var act = () => With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 3))));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await StockAsync(v.VariantId)).Reserved.Should().Be(2);
    }

    [PostgresFact]
    public async Task EightParallelRetriesOfOneKey_CreateExactlyOneOrder_AndHoldTheStockOnce()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10);
        var key = Key();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2)))))));

        // Every retry gets the one order back; none of them is told the stock ran out.
        results.Select(r => r.OrderId).Distinct().Should().ContainSingle();
        results.Count(r => !r.Replayed).Should().Be(1);
        await using var db = fixture.CreateContext();
        (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(1);
        (await StockAsync(v.VariantId)).Reserved.Should().Be(2);
    }

    [PostgresFact]
    public async Task TwentyBuyersForFiveUnits_ExactlyFiveOrders_AndNeverNegativeStock()
    {
        var v = await SeedAsync(onHand: 5);
        var buyers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => NewBuyerAsync()));

        var outcomes = await Task.WhenAll(buyers.Select(b => Task.Run(async () =>
        {
            try { await With(s => s.PlaceAsync(Req(b, Key(), (v.VariantId, 1)))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "ok").Should().Be(5);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "INSUFFICIENT_STOCK");
        var (reserved, onHand) = await StockAsync(v.VariantId);
        (reserved, onHand).Should().Be((5, 5));
        await using var db = fixture.CreateContext();
        (await db.OrderItems.Where(i => i.VariantId == v.VariantId).CountAsync()).Should().Be(5);
    }

    [PostgresFact]
    public async Task APriceChange_AffectsNewOrders_ButNeverOneAlreadyPlaced()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10, 100_000);
        var first = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET price_minor = 200000 WHERE id = {v.VariantId}");
        }

        var second = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        first.GrandTotalMinor.Should().Be(100_000);
        second.GrandTotalMinor.Should().Be(200_000, "the server prices from the database every time");
        await using var check = fixture.CreateContext();
        (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == first.OrderId)).GrandTotalMinor.Should().Be(100_000);
    }

    [PostgresFact]
    public async Task UnknownOrWithdrawnItems_AreRefused_AndNothingIsReservedOrWritten()
    {
        var buyer = await NewBuyerAsync();
        var good = await SeedAsync(5);
        var withdrawn = await SeedAsync(5);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET status = 'inactive' WHERE id = {withdrawn.VariantId}");
        }

        foreach (var bad in new[] { Guid.NewGuid(), withdrawn.VariantId })
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, Key(), (good.VariantId, 1), (bad, 1))));
            (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("VARIANT_UNAVAILABLE");
        }

        (await StockAsync(good.VariantId)).Reserved.Should().Be(0);
        await using var check = fixture.CreateContext();
        (await check.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AProductThatIsNotLive_CannotBeOrdered_EvenIfItsVariantIsActive()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET status = 'draft' WHERE id = {v.ProductId}");
        }

        var act = () => With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("VARIANT_UNAVAILABLE");
        (await StockAsync(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task AnOrderNumberClash_IsRetriedWithAFreshNumber_AndTheFirstAttemptsStockIsGivenBack()
    {
        var buyer = await NewBuyerAsync();
        var first = await SeedAsync(5);
        var placedFirst = await With(s => s.PlaceAsync(Req(buyer, Key(), (first.VariantId, 1))));

        var second = await SeedAsync(5);
        var attempts = 0;
        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (second.VariantId, 2))),
            now => ++attempts == 1 ? placedFirst.OrderNumber : OrderNumber.Generate(now));   // first try collides with an existing number

        attempts.Should().Be(2);
        placed.OrderNumber.Should().NotBe(placedFirst.OrderNumber);
        (await StockAsync(second.VariantId)).Reserved.Should().Be(2, "the collided attempt released its hold; only the final order holds stock");
    }

    [PostgresFact]
    public async Task IfSavingFails_TheStockHeldForTheOrderIsReleased()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);

        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION test_block_orders() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'orders are blocked for this test'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_block_orders BEFORE INSERT ON orders FOR EACH ROW EXECUTE FUNCTION test_block_orders();
                """);
        }

        try
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 3))));
            await act.Should().ThrowAsync<Exception>();
            (await StockAsync(v.VariantId)).Reserved.Should().Be(0, "an order that was not saved must not keep stock");
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS test_block_orders ON orders; DROP FUNCTION IF EXISTS test_block_orders();");
        }
    }

    private async Task<Guid> CompanyAsync(Guid admin, bool activate, CompanyRoles buyerRoles, Guid? buyer = null)
    {
        await using var db = fixture.CreateContext();
        var svc = new ShopVerse.Infrastructure.Companies.CompanyService(db);
        var bin = (9_000_000_000_000L + Random.Shared.NextInt64(0, 999_999_999_999L)).ToString("D13");
        var company = await svc.RegisterAsync(admin, new ShopVerse.Application.Companies.RegisterCompanyRequest("Orders Ltd", "TL-O-1", bin, null, null));
        if (buyer is { } b) await svc.AddMemberAsync(admin, company.Id, new ShopVerse.Application.Companies.AddMemberRequest(b, buyerRoles, null, null));
        if (activate) await svc.AdminActivateAsync(company.Id);
        return company.Id;
    }

    private static PlaceOrderRequest ForCompany(Guid user, Guid company, Guid variant) =>
        new(user, Key(), [new PlaceOrderLine(variant, 1)], null, company);

    [PostgresFact]
    public async Task AVerifiedCompanyBuyer_OrdersWithVatAddedOnTop()
    {
        var admin = await NewBuyerAsync();
        var buyer = await NewBuyerAsync();
        var company = await CompanyAsync(admin, activate: true, CompanyRoles.Buyer, buyer);
        var v = await SeedAsync(5, 100_000);

        var placed = await With(s => s.PlaceAsync(ForCompany(buyer, company, v.VariantId)));

        placed.GrandTotalMinor.Should().Be(105_000, "company orders are VAT-exclusive: 5 % is added to the 1,000.00 price");
        await using var db = fixture.CreateContext();
        (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.OrderId)).CompanyId.Should().Be(company);
    }

    [PostgresFact]
    public async Task CompanyOrders_AreRefusedUnlessTheCallerIsABuyerOfAnActiveCompany()
    {
        var admin = await NewBuyerAsync();
        var requesterOnly = await NewBuyerAsync();
        var outsider = await NewBuyerAsync();
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(20, 100_000);

        var active = await CompanyAsync(admin, activate: true, CompanyRoles.Requester, requesterOnly);
        var pending = await CompanyAsync(admin, activate: false, CompanyRoles.Buyer, buyer);

        (await Assert.ThrowsAsync<KeyNotFoundException>(() => With(s => s.PlaceAsync(ForCompany(outsider, active, v.VariantId))))).Message.Should().Contain("not found");
        (await Assert.ThrowsAsync<ForbiddenException>(() => With(s => s.PlaceAsync(ForCompany(requesterOnly, active, v.VariantId))))).Code.Should().Be("COMPANY_ROLE_REQUIRED");
        (await Assert.ThrowsAsync<ConflictException>(() => With(s => s.PlaceAsync(ForCompany(buyer, pending, v.VariantId))))).Code.Should().Be("COMPANY_NOT_VERIFIED");

        await using (var db = fixture.CreateContext())
        {
            var svc = new ShopVerse.Infrastructure.Companies.CompanyService(db);
            await svc.AdminActivateAsync(pending);
            await svc.AdminSuspendAsync(pending);
        }

        (await Assert.ThrowsAsync<ConflictException>(() => With(s => s.PlaceAsync(ForCompany(buyer, pending, v.VariantId))))).Code.Should().Be("COMPANY_SUSPENDED");
        (await StockAsync(v.VariantId)).Reserved.Should().Be(0, "no refused order may hold stock");
    }

    private async Task<(Guid Company, Guid Buyer)> CreditCompanyAsync(long limitMinor, CreditTerms terms = CreditTerms.Net30)
    {
        var admin = await NewBuyerAsync();
        var buyer = await NewBuyerAsync();
        var company = await CompanyAsync(admin, activate: true, CompanyRoles.Buyer, buyer);
        if (terms != CreditTerms.None)
        {
            await using var db = fixture.CreateContext();
            await new ShopVerse.Infrastructure.Companies.CompanyService(db).AdminSetCreditAsync(company, new ShopVerse.Application.Companies.SetCreditRequest(terms, limitMinor));
        }

        return (company, buyer);
    }

    private static PlaceOrderRequest OnCredit(Guid user, Guid company, Guid variant, int qty = 1, string? key = null) =>
        new(user, key ?? Key(), [new PlaceOrderLine(variant, qty)], null, company, PayOnCredit: true);

    private async Task<long> CreditUsedAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company)).CreditUsedMinor;
    }

    [PostgresFact]
    public async Task AnOrderOnCredit_IsConfirmedAtOnce_UsesTheCreditLimit_AndDeductsStock()
    {
        var (company, buyer) = await CreditCompanyAsync(limitMinor: 500_000);
        var v = await SeedAsync(onHand: 10, price: 100_000);

        var placed = await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId, qty: 2)));

        placed.Status.Should().Be("confirmed");
        placed.GrandTotalMinor.Should().Be(210_000);                    // 2 x 1,000.00 plus 5 % VAT on top
        (await CreditUsedAsync(company)).Should().Be(210_000);
        var (reserved, onHand) = await StockAsync(v.VariantId);
        (reserved, onHand).Should().Be((0, 8), "credit orders deduct stock on confirmation");

        await using var db = fixture.CreateContext();
        var events = await db.OrderEvents.AsNoTracking().Where(e => e.OrderId == placed.OrderId).OrderBy(e => e.Sequence).ToListAsync();
        events.Select(e => e.ToStatus).Should().Equal(OrderStatus.PendingPayment, OrderStatus.Confirmed);
    }

    [PostgresFact]
    public async Task AnOrderThatBreaksTheCreditLimit_IsRefused_LeavesCreditAndStockUntouched()
    {
        var (company, buyer) = await CreditCompanyAsync(limitMinor: 250_000);
        var v = await SeedAsync(10, 100_000);

        await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId, qty: 2)));       // 210,000 of 250,000 used
        var act = () => With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId, qty: 1)));   // +105,000 would be 315,000

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("CREDIT_LIMIT_EXCEEDED");
        (await CreditUsedAsync(company)).Should().Be(210_000);
        (await StockAsync(v.VariantId)).Should().Be((0, 8), "the refused order gave its stock back");
        await using var db = fixture.CreateContext();
        (await db.Orders.CountAsync(o => o.CompanyId == company)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TwelveParallelCreditOrders_NeverPassTheLimit_ExactlyTheAffordableOnesSucceed()
    {
        var (company, buyer) = await CreditCompanyAsync(limitMinor: 320_000);   // room for exactly three 105,000 orders

        // One variant per order: nothing in the inventory serialises them, so only the credit check stands in the way.
        var variants = new List<SeededVariant>();
        for (var i = 0; i < 12; i++) variants.Add(await SeedAsync(5, 100_000));

        using var gate = new ManualResetEventSlim(false);
        var tasks = variants.Select(v => Task.Run(async () =>
        {
            gate.Wait();
            try { await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o == "ok").Should().Be(3);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "CREDIT_LIMIT_EXCEEDED");
        (await CreditUsedAsync(company)).Should().Be(315_000).And.BeLessThanOrEqualTo(320_000);
        foreach (var v in variants) (await StockAsync(v.VariantId)).Reserved.Should().Be(0, "refused orders hold no stock");
    }

    [PostgresFact]
    public async Task CreditNeedsTermsAndACompany()
    {
        var (noTermsCompany, buyer) = await CreditCompanyAsync(0, CreditTerms.None);
        var v = await SeedAsync(10, 100_000);

        (await Assert.ThrowsAsync<ConflictException>(() => With(s => s.PlaceAsync(OnCredit(buyer, noTermsCompany, v.VariantId))))).Code.Should().Be("NO_CREDIT_TERMS");

        var personal = new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], null, null, PayOnCredit: true);
        await Assert.ThrowsAsync<ValidationException>(() => With(s => s.PlaceAsync(personal)));
        (await StockAsync(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task ReplayingACreditOrder_ReturnsConfirmed_AndSpendsTheCreditOnce()
    {
        var (company, buyer) = await CreditCompanyAsync(500_000);
        var v = await SeedAsync(10, 100_000);
        var key = Key();

        var first = await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId, key: key)));
        var again = await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId, key: key)));

        again.OrderId.Should().Be(first.OrderId);
        again.Status.Should().Be("confirmed");
        again.Replayed.Should().BeTrue();
        (await CreditUsedAsync(company)).Should().Be(105_000);
    }

    [PostgresFact]
    public async Task EveryEventGetsItsOwnSequenceNumber_EvenWhenTwoShareATimestamp()
    {
        var (company, buyer) = await CreditCompanyAsync(500_000);
        var v = await SeedAsync(10, 100_000);
        var placed = await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId)));   // "placed" and "confirmed" happen in one request

        await using var db = fixture.CreateContext();
        var events = await db.OrderEvents.AsNoTracking().Where(e => e.OrderId == placed.OrderId).OrderBy(e => e.Sequence).ToListAsync();
        events.Select(e => e.Sequence).Should().Equal(1, 2);
        events.Select(e => e.ToStatus).Should().Equal(OrderStatus.PendingPayment, OrderStatus.Confirmed);

        var clash = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO order_events (id, order_id, seq, from_status, to_status, at, created_at) VALUES ({Guid.NewGuid()}, {placed.OrderId}, 2, 'confirmed', 'processing', now(), now())");
        (await clash.Should().ThrowAsync<Npgsql.PostgresException>()).Which.ConstraintName.Should().Be("ux_order_events_order_seq");
    }

    [PostgresFact]
    public async Task TheStockHold_LastsAsLongAsTheOrderCanBePaid()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);
        var before = DateTime.UtcNow;

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        // If the hold expired earlier than the unpaid-order cutoff, a payment arriving in between would find no stock.
        await using var db = fixture.CreateContext();
        var hold = await db.StockReservations.AsNoTracking().SingleAsync(r => r.ReferenceId == placed.OrderId);
        hold.ExpiresAtUtc.Should().BeCloseTo(before + OrderService.UnpaidOrderLifetime, TimeSpan.FromSeconds(30));
        placed.ReservationExpiresAtUtc.Should().BeCloseTo(hold.ExpiresAtUtc, TimeSpan.FromSeconds(1));
    }

    [PostgresFact]
    public async Task ABuyerCancelsAPendingOrder_StockIsReleased_AndTheReasonIsRecorded()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);
        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 3))));
        (await StockAsync(v.VariantId)).Reserved.Should().Be(3);

        var cancelled = await With(s => s.CancelAsync(buyer, placed.OrderId, "  Ordered by mistake  "));

        cancelled.Status.Should().Be("cancelled");
        cancelled.SubOrders.Should().OnlyContain(s => s.Status == "cancelled");
        (await StockAsync(v.VariantId)).Should().Be((0, 5));
        await using var db = fixture.CreateContext();
        var events = await db.OrderEvents.AsNoTracking().Where(e => e.OrderId == placed.OrderId).OrderBy(e => e.Sequence).ToListAsync();
        events.Select(e => e.ToStatus).Should().Equal(OrderStatus.PendingPayment, OrderStatus.Cancelled);
        events.Last().Reason.Should().Be("Ordered by mistake");
        events.Last().ActorId.Should().Be(buyer);
    }

    [PostgresFact]
    public async Task CancelRefusals_AReasonIsNeeded_AStrangersOrderIsNotFound_AndCancellingTwiceConflicts()
    {
        var buyer = await NewBuyerAsync();
        var stranger = await NewBuyerAsync();
        var v = await SeedAsync(5);
        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        await Assert.ThrowsAsync<ValidationException>(() => With(s => s.CancelAsync(buyer, placed.OrderId, "  ")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => With(s => s.CancelAsync(stranger, placed.OrderId, "not mine")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => With(s => s.CancelAsync(buyer, Guid.NewGuid(), "no such order")));
        (await StockAsync(v.VariantId)).Reserved.Should().Be(1, "refused cancellations change nothing");

        await With(s => s.CancelAsync(buyer, placed.OrderId, "first"));
        (await Assert.ThrowsAsync<ConflictException>(() => With(s => s.CancelAsync(buyer, placed.OrderId, "second")))).Code.Should().Be("ORDER_NOT_CANCELLABLE");
    }

    [PostgresFact]
    public async Task AConfirmedCreditOrder_CannotBeCancelledYet_AndKeepsItsCreditAndStock()
    {
        var (company, buyer) = await CreditCompanyAsync(500_000);
        var v = await SeedAsync(10, 100_000);
        var placed = await With(s => s.PlaceAsync(OnCredit(buyer, company, v.VariantId)));

        (await Assert.ThrowsAsync<ConflictException>(() => With(s => s.CancelAsync(buyer, placed.OrderId, "changed my mind")))).Code.Should().Be("ORDER_NOT_CANCELLABLE");

        (await CreditUsedAsync(company)).Should().Be(105_000);
        (await StockAsync(v.VariantId)).Should().Be((0, 9));
    }

    [PostgresFact]
    public async Task TheSweep_CancelsOnlyOrdersUnpaidForThirtyMinutes_AndFreesTheirStock()
    {
        var buyer = await NewBuyerAsync();
        var stale = await SeedAsync(5);
        var fresh = await SeedAsync(5);
        var staleOrder = await With(s => s.PlaceAsync(Req(buyer, Key(), (stale.VariantId, 2))));

        // Age the first order by moving its placement time back; the second is placed "now".
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET placed_at = now() - interval '31 minutes' WHERE id = {staleOrder.OrderId}");
        var freshOrder = await With(s => s.PlaceAsync(Req(buyer, Key(), (fresh.VariantId, 2))));

        var cancelled = await With(s => s.ExpireUnpaidAsync(DateTime.UtcNow));

        cancelled.Should().BeGreaterThanOrEqualTo(1);
        await using var check = fixture.CreateContext();
        (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == staleOrder.OrderId)).Status.Should().Be(OrderStatus.Cancelled);
        (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == freshOrder.OrderId)).Status.Should().Be(OrderStatus.PendingPayment);
        (await StockAsync(stale.VariantId)).Reserved.Should().Be(0);
        (await StockAsync(fresh.VariantId)).Reserved.Should().Be(2);
        var reason = (await check.OrderEvents.AsNoTracking().Where(e => e.OrderId == staleOrder.OrderId && e.ToStatus == OrderStatus.Cancelled).SingleAsync());
        reason.ActorId.Should().BeNull("the system cancelled it");
        reason.Reason.Should().Contain("30 minutes");
    }

    [PostgresFact]
    public async Task TheSweep_SkipsPaidAndCreditOrders_AndParallelSweepsCancelEachOrderOnce()
    {
        var buyer = await NewBuyerAsync();
        var (company, creditBuyer) = await CreditCompanyAsync(500_000);
        var v = await SeedAsync(20, 100_000);
        var unpaid = new List<Guid>();
        for (var i = 0; i < 4; i++) unpaid.Add((await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))))).OrderId);
        var creditOrder = await With(s => s.PlaceAsync(OnCredit(creditBuyer, company, v.VariantId)));

        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET placed_at = now() - interval '2 hours' WHERE user_id IN ({buyer}, {creditBuyer})");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => With(s => s.ExpireUnpaidAsync(DateTime.UtcNow)))));

        await using var check = fixture.CreateContext();
        foreach (var id in unpaid)
        {
            (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == id)).Status.Should().Be(OrderStatus.Cancelled);
            (await check.OrderEvents.CountAsync(e => e.OrderId == id && e.ToStatus == OrderStatus.Cancelled)).Should().Be(1, "cancelled once, never twice");
        }

        (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == creditOrder.OrderId)).Status.Should().Be(OrderStatus.Confirmed, "a confirmed order is not 'unpaid'");
        (await StockAsync(v.VariantId)).Should().Be((0, 19));
    }

    [PostgresFact]
    public async Task BadRequests_AreRefusedBeforeAnythingHappens()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);

        foreach (var (key, lines) in new (string, (Guid, int)[])[]
        {
            ("short", [(v.VariantId, 1)]),                       // key too short
            ("bad key with spaces!", [(v.VariantId, 1)]),
            (Key(), []),                                         // no lines
            (Key(), [(v.VariantId, 0)]),
            (Key(), [(v.VariantId, -2)]),
            (Key(), [(v.VariantId, 10_001)])
        })
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, key, lines)));
            await act.Should().ThrowAsync<ValidationException>();
        }

        (await StockAsync(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task ADuplicatedVariantIsOneLine_AndCountsTowardsTheStockLimitOnce()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(onHand: 4);

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 2), (v.VariantId, 2))));

        placed.GrandTotalMinor.Should().Be(4 * 150_000);
        (await StockAsync(v.VariantId)).Reserved.Should().Be(4);
        await using var db = fixture.CreateContext();
        (await db.OrderItems.CountAsync(i => i.VariantId == v.VariantId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TheDatabaseRefusesAnOrderWhoseMoneyDoesNotAddUp()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);
        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        await using var db = fixture.CreateContext();
        var act = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE order_items SET total = total + 1 WHERE sub_order_id IN (SELECT id FROM sub_orders WHERE order_id = {placed.OrderId})");
        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.ConstraintName.Should().Be("ck_order_items_money");
    }
}
