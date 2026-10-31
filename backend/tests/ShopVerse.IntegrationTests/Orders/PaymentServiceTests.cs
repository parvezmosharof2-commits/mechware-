using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Application.Payments;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Payments;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.Infrastructure.Payments;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

public class PaymentServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private readonly MockPaymentProvider _mock = new();

    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<(OrderService Orders, PaymentService Payments, ShopVerse.Infrastructure.Persistence.ShopVerseDbContext Db)> OpenAsync(DateTime? now = null)
    {
        var db = fixture.CreateContext();
        var clock = now is null ? TimeProvider.System : new FixedClock(now.Value);
        var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        return (new OrderService(db, inv, TimeProvider.System), new PaymentService(db, [_mock], inv, clock), db);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<(Guid Buyer, Guid Order, SeededVariant Variant, PaymentSessionResult Session, string ProviderRef)> PendingOrderAsync(int qty = 2, DateTime? clockNow = null)
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        await using var seedDb = fixture.CreateContext();
        var v = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 10, priceMinor: 100_000);

        var (orders, payments, db) = await OpenAsync(clockNow);
        await using (db)
        {
            var placed = await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, qty)]));
            var session = await payments.StartAsync(buyer, placed.OrderId, "mock");
            var providerRef = session.RedirectUrl[(session.RedirectUrl.LastIndexOf('/') + 1)..];
            return (buyer, placed.OrderId, v, session, providerRef);
        }
    }

    private static (string Body, string Timestamp, string Signature) Webhook(string eventId, string providerRef, DateTime? at = null, string? secret = null)
    {
        var body = $$"""{ "id": "{{eventId}}", "providerRef": "{{providerRef}}" }""";
        var ts = new DateTimeOffset(at ?? DateTime.UtcNow).ToUnixTimeSeconds();
        return (body, ts.ToString(), WebhookVerifier.Sign(secret ?? MockPaymentProvider.DefaultSecret, ts, body));
    }

    private async Task<WebhookOutcome> DeliverAsync((string Body, string Timestamp, string Signature) w, DateTime? clockNow = null)
    {
        var (_, payments, db) = await OpenAsync(clockNow);
        await using (db) return await payments.HandleWebhookAsync("mock", w.Signature, w.Timestamp, w.Body);
    }

    private async Task<OrderStatus> StatusAsync(Guid orderId)
    {
        await using var db = fixture.CreateContext();
        return (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status;
    }

    private async Task<(int Reserved, int OnHand)> StockAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == variantId);
        return (item.Reserved, item.OnHand);
    }

    [PostgresFact]
    public async Task APaidAndVerifiedWebhook_ConfirmsTheOrder_AndTurnsTheHoldIntoARealDeduction()
    {
        var p = await PendingOrderAsync(qty: 2);
        p.Session.AmountMinor.Should().Be(200_000, "the payment is for the order's own total (2 x 1,000.00, VAT inside for a personal order)");
        _mock.SimulatePayment(p.ProviderRef);

        var outcome = await DeliverAsync(Webhook("evt_1", p.ProviderRef));

        outcome.Should().Be(WebhookOutcome.Confirmed);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Confirmed);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));
        await using var db = fixture.CreateContext();
        (await db.Payments.AsNoTracking().SingleAsync(x => x.ProviderRef == p.ProviderRef)).Status.Should().Be(PaymentStatus.Captured);
    }

    [PostgresFact]
    public async Task RedirectingBackWithoutAWebhook_NeverConfirmsAnything()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);   // the customer even paid, but no webhook has been processed

        // There is no endpoint or call that confirms from a browser return: the order simply stays pending.
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);
        (await StockAsync(p.Variant.VariantId)).Should().Be((2, 10));
    }

    [PostgresFact]
    public async Task AWebhookForAPaymentTheProviderDoesNotSayIsPaid_ConfirmsNothing()
    {
        var p = await PendingOrderAsync();   // never paid at the provider

        var outcome = await DeliverAsync(Webhook("evt_unpaid", p.ProviderRef));

        outcome.Should().Be(WebhookOutcome.NotPaid);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);
    }

    [PostgresFact]
    public async Task ADifferentAmountPaid_IsNeverConfirmed_AndIsFlaggedForRefund()
    {
        var p = await PendingOrderAsync(qty: 2);
        _mock.SimulatePayment(p.ProviderRef, amountMinor: 1);   // the provider says only 0.01 was paid

        var outcome = await DeliverAsync(Webhook("evt_short", p.ProviderRef));

        outcome.Should().Be(WebhookOutcome.NotPaid);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);
        await using var db = fixture.CreateContext();
        (await db.Payments.AsNoTracking().SingleAsync(x => x.ProviderRef == p.ProviderRef)).Status.Should().Be(PaymentStatus.NeedsRefund);
    }

    [PostgresFact]
    public async Task ForgedStaleAndGarbledWebhooks_AreRejected_AndRecordNothing()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);

        var forged = Webhook("evt_f", p.ProviderRef, secret: "not-the-secret");
        var stale = Webhook("evt_s", p.ProviderRef, at: DateTime.UtcNow.AddHours(-2));
        var tampered = Webhook("evt_t", p.ProviderRef);
        tampered.Body = tampered.Body.Replace(p.ProviderRef, "mock_someone_else");

        foreach (var w in new[] { forged, stale, tampered })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DeliverAsync(w));

        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);
        await using var db = fixture.CreateContext();
        (await db.PaymentEvents.CountAsync(e => e.ProviderRef == p.ProviderRef)).Should().Be(0, "rejected requests never reach the inbox");
    }

    [PostgresFact]
    public async Task TheSameEventDeliveredTwice_IsProcessedOnce()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);
        var hook = Webhook("evt_dup", p.ProviderRef);

        (await DeliverAsync(hook)).Should().Be(WebhookOutcome.Confirmed);
        (await DeliverAsync(hook)).Should().Be(WebhookOutcome.Duplicate);

        await using var db = fixture.CreateContext();
        (await db.OrderEvents.CountAsync(e => e.OrderId == p.Order && e.ToStatus == OrderStatus.Confirmed)).Should().Be(1);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8), "stock is deducted once");
    }

    [PostgresFact]
    public async Task EightParallelDeliveriesOfOneEvent_ConfirmExactlyOnce()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);
        var hook = Webhook("evt_race", p.ProviderRef);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => DeliverAsync(hook))));

        outcomes.Count(o => o == WebhookOutcome.Confirmed).Should().Be(1);
        outcomes.Count(o => o == WebhookOutcome.Duplicate).Should().Be(7);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));
    }

    [PostgresFact]
    public async Task TwoDifferentEventsForOnePayment_StillConfirmOnce()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);

        var outcomes = await Task.WhenAll(
            Task.Run(() => DeliverAsync(Webhook("evt_a", p.ProviderRef))),
            Task.Run(() => DeliverAsync(Webhook("evt_b", p.ProviderRef))));

        outcomes.Count(o => o == WebhookOutcome.Confirmed).Should().Be(1);
        outcomes.Single(o => o != WebhookOutcome.Confirmed).Should().Be(WebhookOutcome.AlreadyConfirmed);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));
    }

    [PostgresFact]
    public async Task TwoParallelEventsForAWrongAmountPayment_BothAnswerCleanly_NeitherCrashes()
    {
        var p = await PendingOrderAsync(qty: 2);
        _mock.SimulatePayment(p.ProviderRef, amountMinor: 1);

        // Two different events about the same short payment, at once: both must be answered (not an unhandled concurrency error).
        var outcomes = await Task.WhenAll(
            Task.Run(() => DeliverAsync(Webhook("evt_short_a", p.ProviderRef))),
            Task.Run(() => DeliverAsync(Webhook("evt_short_b", p.ProviderRef))),
            Task.Run(() => DeliverAsync(Webhook("evt_short_c", p.ProviderRef))),
            Task.Run(() => DeliverAsync(Webhook("evt_short_d", p.ProviderRef))));

        outcomes.Should().OnlyContain(o => o == WebhookOutcome.NotPaid);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);
    }

    [PostgresFact]
    public async Task PayingAfterTheOrderWasCancelled_IsNotConfirmed_AndIsFlaggedForRefund()
    {
        var p = await PendingOrderAsync();
        var (orders, _, db) = await OpenAsync();
        await using (db) await orders.CancelAsync(p.Buyer, p.Order, "changed my mind");
        _mock.SimulatePayment(p.ProviderRef);

        var outcome = await DeliverAsync(Webhook("evt_late", p.ProviderRef));

        outcome.Should().Be(WebhookOutcome.NeedsRefund);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Cancelled);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 10), "a cancelled order must not take stock");
        await using var check = fixture.CreateContext();
        (await check.Payments.AsNoTracking().SingleAsync(x => x.ProviderRef == p.ProviderRef)).Status.Should().Be(PaymentStatus.NeedsRefund);
    }

    [PostgresFact]
    public async Task AWebhookAboutAPaymentWeNeverCreated_IsAcknowledgedAndIgnored()
    {
        _mock.SimulatePayment((await PendingOrderAsync()).ProviderRef);   // some other, real session exists
        var outcome = await DeliverAsync(Webhook("evt_stranger", "mock_never_created"));
        outcome.Should().Be(WebhookOutcome.UnknownPayment);
    }

    [PostgresFact]
    public async Task OnlyThePendingOrdersOwnerCanStartAPayment_AndOnlyForAKnownProvider()
    {
        var p = await PendingOrderAsync();
        var stranger = await TestUsers.CreateAsync(fixture);
        var (_, payments, db) = await OpenAsync();
        await using (db)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => payments.StartAsync(stranger, p.Order, "mock"));
            await Assert.ThrowsAsync<ValidationException>(() => payments.StartAsync(p.Buyer, p.Order, "paypal"));
        }

        _mock.SimulatePayment(p.ProviderRef);
        await DeliverAsync(Webhook("evt_paid", p.ProviderRef));
        var (_, again, db2) = await OpenAsync();
        await using (db2) (await Assert.ThrowsAsync<ConflictException>(() => again.StartAsync(p.Buyer, p.Order, "mock"))).Code.Should().Be("ORDER_NOT_PAYABLE");
    }
}
