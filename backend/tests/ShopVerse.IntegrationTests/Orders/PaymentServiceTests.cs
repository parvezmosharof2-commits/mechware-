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

    // ---- Threat model v2, P1 / P2 / P5 (docs/security/THREAT_MODEL_PRICING_QUOTES_PAYMENTS_INVOICING.md) ----

    /// <summary>The mock gateway, except that verifying can be made to fail the way a real gateway times out.</summary>
    private sealed class FlakyProvider(MockPaymentProvider inner) : IPaymentProvider
    {
        public int FailuresLeft;
        public string Name => inner.Name;
        public string WebhookSecret => inner.WebhookSecret;
        public Task<ProviderSession> CreateSessionAsync(Guid orderId, string orderNumber, long amountMinor, string currency, CancellationToken ct = default) =>
            inner.CreateSessionAsync(orderId, orderNumber, amountMinor, currency, ct);
        public Task<ProviderPayment> VerifyAsync(string providerRef, CancellationToken ct = default)
        {
            if (FailuresLeft > 0) { FailuresLeft--; throw new HttpRequestException("The gateway timed out."); }
            return inner.VerifyAsync(providerRef, ct);
        }
        public ParsedWebhook? Parse(string rawBody) => inner.Parse(rawBody);
        public Task<ProviderRefund> RefundAsync(string providerRef, long amountMinor, string idempotencyKey, CancellationToken ct = default) =>
            inner.RefundAsync(providerRef, amountMinor, idempotencyKey, ct);
    }

    private async Task<WebhookOutcome> DeliverThroughAsync(IPaymentProvider provider, (string Body, string Timestamp, string Signature) w, DateTime? clockNow = null)
    {
        await using var db = fixture.CreateContext();
        var clock = clockNow is null ? TimeProvider.System : new FixedClock(clockNow.Value);
        var payments = new PaymentService(db, [provider], new InventoryService(db, new NoopLockProvider(), TimeProvider.System), clock);
        return await payments.HandleWebhookAsync("mock", w.Signature, w.Timestamp, w.Body);
    }

    private async Task<PaymentStatus> PaymentStatusAsync(string providerRef)
    {
        await using var db = fixture.CreateContext();
        return (await db.Payments.AsNoTracking().SingleAsync(x => x.ProviderRef == providerRef)).Status;
    }

    [PostgresFact]
    public async Task ATransientProviderFailure_IsRetried_NotSwallowedAsDuplicate()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);
        var flaky = new FlakyProvider(_mock) { FailuresLeft = 1 };
        var w = Webhook("evt_timeout", p.ProviderRef);

        // The gateway is down while we verify: the delivery fails, so the provider will send the same event again.
        await Assert.ThrowsAsync<HttpRequestException>(() => DeliverThroughAsync(flaky, w));
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);

        // That retry must be processed, not answered "duplicate" while the buyer's money sits unconfirmed.
        (await DeliverThroughAsync(flaky, w)).Should().Be(WebhookOutcome.Confirmed);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Confirmed);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));
        (await DeliverThroughAsync(flaky, w)).Should().Be(WebhookOutcome.Duplicate, "once handled, the event is a real duplicate");
    }

    [PostgresFact]
    public async Task AnAttemptThatDiedMidway_IsTakenOverOnceStale_ButNotWhileItMayStillBeRunning()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);
        var w = Webhook("evt_crashed", p.ProviderRef);

        // A process that was killed after claiming the event leaves its inbox row behind, unfinished.
        await using (var db = fixture.CreateContext())
        {
            db.PaymentEvents.Add(new PaymentEvent("mock", "evt_crashed", p.ProviderRef, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        // Moments later the claim may belong to an attempt that is still running: leave it alone.
        (await DeliverAsync(w)).Should().Be(WebhookOutcome.Duplicate);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.PendingPayment);

        // Once it is stale, the next delivery takes it over and finishes the job.
        var later = DateTime.UtcNow + PaymentService.StaleClaimAfter + TimeSpan.FromMinutes(1);
        (await DeliverAsync(Webhook("evt_crashed", p.ProviderRef, later), later)).Should().Be(WebhookOutcome.Confirmed);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Confirmed);
    }

    [PostgresFact]
    public async Task ASecondPaidSessionForAConfirmedOrder_IsFlaggedForRefund()
    {
        var p = await PendingOrderAsync();
        var (_, payments, db) = await OpenAsync();
        PaymentSessionResult second;
        await using (db) second = await payments.StartAsync(p.Buyer, p.Order, "mock");
        var secondRef = second.RedirectUrl[(second.RedirectUrl.LastIndexOf('/') + 1)..];

        // Two tabs, both paid.
        _mock.SimulatePayment(p.ProviderRef);
        _mock.SimulatePayment(secondRef);
        (await DeliverAsync(Webhook("evt_first", p.ProviderRef))).Should().Be(WebhookOutcome.Confirmed);
        (await DeliverAsync(Webhook("evt_second", secondRef))).Should().Be(WebhookOutcome.NeedsRefund);

        (await PaymentStatusAsync(p.ProviderRef)).Should().Be(PaymentStatus.Captured);
        (await PaymentStatusAsync(secondRef)).Should().Be(PaymentStatus.NeedsRefund, "money the order cannot use must go back, never sit as 'pending'");
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8), "the stock is taken once");
    }

    [PostgresFact]
    public async Task OneOrder_CannotCollectPaymentAttemptsWithoutBound()
    {
        var p = await PendingOrderAsync();   // the first attempt
        var (_, payments, db) = await OpenAsync();
        await using (db)
        {
            for (var i = 1; i < PaymentService.MaxOpenAttemptsPerOrder; i++) await payments.StartAsync(p.Buyer, p.Order, "mock");
            (await Assert.ThrowsAsync<ConflictException>(() => payments.StartAsync(p.Buyer, p.Order, "mock"))).Code.Should().Be("TOO_MANY_PAYMENT_ATTEMPTS");
        }
    }

    // ---- Threat model v2, P4: a lost webhook is caught by asking the provider ----

    private async Task<int> ReconcileAsync(DateTime now, IPaymentProvider? provider = null)
    {
        await using var db = fixture.CreateContext();
        var payments = new PaymentService(db, [provider ?? _mock], new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System);
        return await payments.ReconcileAsync(now, 1000);
    }

    [PostgresFact]
    public async Task APaidAttemptWhoseWebhookNeverCame_IsConfirmedByReconciliation()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);   // the buyer paid; the webhook is lost

        (await ReconcileAsync(DateTime.UtcNow + PaymentService.ReconcileAfter + TimeSpan.FromMinutes(1))).Should().BeGreaterThanOrEqualTo(1);
        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Confirmed);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));

        // The webhook turning up late changes nothing.
        (await DeliverAsync(Webhook("evt_late_after_reconcile", p.ProviderRef))).Should().Be(WebhookOutcome.AlreadyConfirmed);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));
    }

    [PostgresFact]
    public async Task Reconciliation_LeavesYoungAndUnpaidAttemptsAlone_AndSurvivesAProviderThatIsDown()
    {
        var young = await PendingOrderAsync();
        _mock.SimulatePayment(young.ProviderRef);
        (await ReconcileAsync(DateTime.UtcNow)).Should().Be(0);
        (await StatusAsync(young.Order)).Should().Be(OrderStatus.PendingPayment, "its webhook may still be on the way");

        var unpaid = await PendingOrderAsync();
        var later = DateTime.UtcNow + PaymentService.ReconcileAfter + TimeSpan.FromMinutes(1);

        var down = new FlakyProvider(_mock) { FailuresLeft = int.MaxValue };
        (await ReconcileAsync(later, down)).Should().Be(0);
        (await StatusAsync(young.Order)).Should().Be(OrderStatus.PendingPayment, "an unreachable provider confirms nothing and breaks nothing");

        // Too old: past the window, the webhook and the refund queue handle it, not polling.
        (await ReconcileAsync(DateTime.UtcNow + PaymentService.ReconcileWindow + TimeSpan.FromMinutes(1))).Should().Be(0);
        (await StatusAsync(young.Order)).Should().Be(OrderStatus.PendingPayment);

        // Old enough and inside the window: the paid one is confirmed, the unpaid one is left alone.
        await ReconcileAsync(later);
        (await StatusAsync(young.Order)).Should().Be(OrderStatus.Confirmed);
        (await StatusAsync(unpaid.Order)).Should().Be(OrderStatus.PendingPayment, "the provider says it was not paid");
    }

    [PostgresFact]
    public async Task ReconciliationAndTheWebhookAtTheSameMoment_ConfirmOnce()
    {
        var p = await PendingOrderAsync();
        _mock.SimulatePayment(p.ProviderRef);
        var later = DateTime.UtcNow + PaymentService.ReconcileAfter + TimeSpan.FromMinutes(1);

        await Task.WhenAll(
            Task.Run(() => ReconcileAsync(later)),
            Task.Run(() => DeliverAsync(Webhook("evt_racing_reconcile", p.ProviderRef))),
            Task.Run(() => ReconcileAsync(later)));

        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Confirmed);
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8), "the stock is taken exactly once");
        await using var db = fixture.CreateContext();
        (await db.Payments.AsNoTracking().SingleAsync(x => x.ProviderRef == p.ProviderRef)).Status.Should().Be(PaymentStatus.Captured);
    }

    [PostgresFact]
    public async Task ASecondPaidTabWhoseWebhookWasLost_IsFlaggedForRefund_EvenThoughTheOrderIsAlreadyConfirmed()
    {
        var p = await PendingOrderAsync();
        var (_, payments, db) = await OpenAsync();
        PaymentSessionResult second;
        await using (db) second = await payments.StartAsync(p.Buyer, p.Order, "mock");
        var secondRef = second.RedirectUrl[(second.RedirectUrl.LastIndexOf('/') + 1)..];
        _mock.SimulatePayment(p.ProviderRef);
        _mock.SimulatePayment(secondRef);

        // The first tab's webhook arrives and confirms the order; the second tab's webhook is lost.
        (await DeliverAsync(Webhook("evt_first_tab", p.ProviderRef))).Should().Be(WebhookOutcome.Confirmed);
        await ReconcileAsync(DateTime.UtcNow + PaymentService.ReconcileAfter + TimeSpan.FromMinutes(1));

        (await StatusAsync(p.Order)).Should().Be(OrderStatus.Confirmed);
        var statuses = new[] { await PaymentStatusAsync(p.ProviderRef), await PaymentStatusAsync(secondRef) };
        statuses.Should().BeEquivalentTo([PaymentStatus.Captured, PaymentStatus.NeedsRefund], "the extra money must reach the refund queue");
        (await StockAsync(p.Variant.VariantId)).Should().Be((0, 8));
    }
}
