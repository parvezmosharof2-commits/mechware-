using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Application.Payments;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Payments;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.Infrastructure.Payments;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

/// <summary>Threat model v2, P3: refunds need two people, never exceed the payment, and never pay out twice.</summary>
public class RefundServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private readonly MockPaymentProvider _mock = new();
    private const long Paid = 200_000;

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<T> Refunds<T>(Func<RefundService, Task<T>> action, DateTime? now = null)
    {
        await using var db = fixture.CreateContext();
        return await action(new RefundService(db, [_mock], now is null ? TimeProvider.System : new FixedClock(now.Value)));
    }

    /// <summary>An order paid through the mock gateway: a captured payment of <see cref="Paid"/>.</summary>
    private async Task<(Guid PaymentId, string ProviderRef)> CapturedPaymentAsync()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        SeededVariant variant;
        await using (var seed = fixture.CreateContext()) variant = await CatalogTestData.SeedVariantAsync(seed, onHand: 10, priceMinor: Paid);
        await using var db = fixture.CreateContext();
        var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        var placed = await new OrderService(db, inv, TimeProvider.System).PlaceAsync(
            new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variant.VariantId, 1)]));
        var payments = new PaymentService(db, [_mock], inv, TimeProvider.System);
        var session = await payments.StartAsync(buyer, placed.OrderId, "mock");
        var providerRef = session.RedirectUrl[(session.RedirectUrl.LastIndexOf('/') + 1)..];
        _mock.SimulatePayment(providerRef);
        var body = $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "providerRef": "{{providerRef}}" }""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (await payments.HandleWebhookAsync("mock", WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body), ts.ToString(), body))
            .Should().Be(WebhookOutcome.Confirmed);
        return (session.PaymentId, providerRef);
    }

    private Task<Guid> Staff() => TestUsers.CreateAsync(fixture);

    private static async Task<TException> Throws<TException>(Func<Task> act) where TException : Exception =>
        (await act.Should().ThrowAsync<TException>()).Which;

    private async Task<PaymentStatus> PaymentStatusAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == id)).Status;
    }

    [PostgresFact]
    public async Task TheRequesterCannotApproveTheirOwnRefund_ASecondPersonMust()
    {
        var (payment, providerRef) = await CapturedPaymentAsync();
        var (asker, approver) = (await Staff(), await Staff());
        var refund = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(Paid, "Order cancelled by the buyer")));

        (await Throws<ConflictException>(() => Refunds(s => s.ApproveAsync(asker, refund.Id)))).Code.Should().Be("REFUND_FOUR_EYES");
        _mock.RefundedOn(providerRef).Should().Be(0, "no money may move on one person's say");

        var done = await Refunds(s => s.ApproveAsync(approver, refund.Id));
        done.Status.Should().Be("Completed");
        done.DecidedBy.Should().Be(approver);
        _mock.RefundedOn(providerRef).Should().Be(Paid);
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.Refunded);
    }

    [PostgresFact]
    public async Task ARefundCanNeverExceedWhatThePaymentStillHolds()
    {
        var (payment, _) = await CapturedPaymentAsync();
        var asker = await Staff();

        (await Throws<ConflictException>(() => Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(Paid + 1, "Too much by one poisha"))))).Code.Should().Be("REFUND_EXCEEDS_PAYMENT");

        var first = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(150_000, "Two lines returned")));
        await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(50_000, "One more line returned")));
        (await Throws<ConflictException>(() => Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(1, "Anything at all"))))).Code.Should().Be("REFUND_EXCEEDS_PAYMENT", "pending refunds count, not only completed ones");

        // A rejected request gives its amount back.
        var reviewer = await Staff();
        await Refunds(s => s.RejectAsync(reviewer, first.Id, new RejectRefund("Goods came back damaged")));
        (await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(150_000, "Damage assessed, refund in full")))).Status.Should().Be("Requested");
    }

    [PostgresFact]
    public async Task ParallelRequests_CannotTogetherExceedThePayment()
    {
        var (payment, _) = await CapturedPaymentAsync();
        var askers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Staff()));
        var outcomes = await Task.WhenAll(askers.Select(a => Task.Run(async () =>
        {
            try { await Refunds(s => s.RequestAsync(a, payment, new RequestRefund(120_000, "Racing refund request"))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "ok").Should().Be(1);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "REFUND_EXCEEDS_PAYMENT");
    }

    [PostgresFact]
    public async Task TwoApproversAtOnce_MoveTheMoneyOnce()
    {
        var (payment, providerRef) = await CapturedPaymentAsync();
        var asker = await Staff();
        var refund = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(Paid, "Order cancelled")));
        var approvers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Staff()));

        var outcomes = await Task.WhenAll(approvers.Select(a => Task.Run(async () =>
        {
            try { return (await Refunds(s => s.ApproveAsync(a, refund.Id))).Status; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "Completed").Should().Be(1);
        outcomes.Where(o => o != "Completed").Should().OnlyContain(o => o == "REFUND_NOT_APPROVABLE");
        _mock.RefundedOn(providerRef).Should().Be(Paid);
    }

    [PostgresFact]
    public async Task AProviderFailure_LeavesTheRefundFailed_AndApprovingAgainPaysOutOnce()
    {
        var (payment, providerRef) = await CapturedPaymentAsync();
        var asker = await Staff();
        var refund = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(80_000, "Partial return")));
        var approver = await Staff();

        _mock.FailNextRefunds = 1;
        var failed = await Refunds(s => s.ApproveAsync(approver, refund.Id));
        failed.Status.Should().Be("Failed");
        failed.Note.Should().NotBeNullOrWhiteSpace();
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.Captured);

        (await Refunds(s => s.ApproveAsync(approver, refund.Id))).Status.Should().Be("Completed");
        _mock.RefundedOn(providerRef).Should().Be(80_000);
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.Captured, "a partial refund leaves the rest of the payment in place");
    }

    [PostgresFact]
    public async Task ARefundStuckMidCall_CanBeApprovedAgainOnlyOnceStale_AndStillPaysOnce()
    {
        var (payment, providerRef) = await CapturedPaymentAsync();
        var asker = await Staff();
        var refund = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(Paid, "Order cancelled")));
        var approver = await Staff();

        // The process died between claiming the refund and recording the outcome, after the provider had already paid it.
        await _mock.RefundAsync(providerRef, Paid, refund.Id.ToString("N"));
        await using (var db = fixture.CreateContext())
        {
            var row = await db.Refunds.SingleAsync(r => r.Id == refund.Id);
            row.Approve(approver, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        (await Throws<ConflictException>(() => Refunds(s => s.ApproveAsync(approver, refund.Id)))).Code.Should().Be("REFUND_NOT_APPROVABLE", "it may still be running");

        var later = DateTime.UtcNow + Refund.StuckAfter + TimeSpan.FromMinutes(1);
        (await Refunds(s => s.ApproveAsync(approver, refund.Id), later)).Status.Should().Be("Completed");
        _mock.RefundedOn(providerRef).Should().Be(Paid, "the same idempotency key cannot pay twice");
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.Refunded);
    }

    [PostgresFact]
    public async Task OnlyPaymentsThatReceivedMoney_CanBeRefunded_AndBadInputIsRefused()
    {
        var (payment, _) = await CapturedPaymentAsync();
        var asker = await Staff();

        // A payment the buyer never finished holds no money.
        var buyer = await TestUsers.CreateAsync(fixture);
        SeededVariant variant;
        await using (var seed = fixture.CreateContext()) variant = await CatalogTestData.SeedVariantAsync(seed, onHand: 5, priceMinor: Paid);
        Guid unpaid;
        await using (var db = fixture.CreateContext())
        {
            var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
            var placed = await new OrderService(db, inv, TimeProvider.System).PlaceAsync(
                new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variant.VariantId, 1)]));
            unpaid = (await new PaymentService(db, [_mock], inv, TimeProvider.System).StartAsync(buyer, placed.OrderId, "mock")).PaymentId;
        }
        (await Throws<ConflictException>(() => Refunds(s => s.RequestAsync(asker, unpaid, new RequestRefund(1, "Never paid at all"))))).Code
            .Should().Be("PAYMENT_NOT_REFUNDABLE");

        await Throws<KeyNotFoundException>(() => Refunds(s => s.RequestAsync(asker, Guid.NewGuid(), new RequestRefund(1, "Unknown payment"))));
        await Throws<ValidationException>(() => Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(0, "Nothing to refund"))));
        await Throws<ValidationException>(() => Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(1, "ok"))));

        var refund = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(Paid, "Order cancelled")));
        await Throws<ValidationException>(() => Refunds(s => s.RejectAsync(asker, refund.Id, new RejectRefund("no"))));
        await Refunds(s => s.RejectAsync(asker, refund.Id, new RejectRefund("Asked by mistake")));
        var approver = await Staff();
        (await Throws<ConflictException>(() => Refunds(s => s.ApproveAsync(approver, refund.Id)))).Code.Should().Be("REFUND_NOT_APPROVABLE");
    }

    [PostgresFact]
    public async Task TheQueue_ShowsPaymentsThatMustBeRefunded_UntilTheyAre()
    {
        // The realistic way money ends up needing a refund: two tabs, both paid (threat model v2, P2).
        var buyer = await TestUsers.CreateAsync(fixture);
        SeededVariant variant;
        await using (var seed = fixture.CreateContext()) variant = await CatalogTestData.SeedVariantAsync(seed, onHand: 5, priceMinor: Paid);
        Guid payment;
        await using (var db = fixture.CreateContext())
        {
            var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
            var placed = await new OrderService(db, inv, TimeProvider.System).PlaceAsync(
                new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variant.VariantId, 1)]));
            var payments = new PaymentService(db, [_mock], inv, TimeProvider.System);
            var tabs = new[] { await payments.StartAsync(buyer, placed.OrderId, "mock"), await payments.StartAsync(buyer, placed.OrderId, "mock") };
            foreach (var tab in tabs)
            {
                var providerRef = tab.RedirectUrl[(tab.RedirectUrl.LastIndexOf('/') + 1)..];
                _mock.SimulatePayment(providerRef);
                var body = $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "providerRef": "{{providerRef}}" }""";
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                db.ChangeTracker.Clear();
                await payments.HandleWebhookAsync("mock", WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body), ts.ToString(), body);
            }
            payment = tabs[1].PaymentId;
        }
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.NeedsRefund);

        var item = (await Refunds(s => s.QueueAsync())).Single(i => i.PaymentId == payment);
        item.RefundableMinor.Should().Be(Paid);
        item.OrderNumber.Should().StartWith("MW-");

        var (asker, approver) = (await Staff(), await Staff());
        var refund = await Refunds(s => s.RequestAsync(asker, payment, new RequestRefund(Paid, "Paid twice")));
        (await Refunds(s => s.QueueAsync())).Single(i => i.PaymentId == payment).RefundableMinor.Should().Be(0);
        await Refunds(s => s.ApproveAsync(approver, refund.Id));
        (await Refunds(s => s.QueueAsync())).Should().NotContain(i => i.PaymentId == payment);
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.Refunded);

        // The gateway sends one more (new) event about that payment: it must not land back in the queue.
        await using (var db = fixture.CreateContext())
        {
            var row = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment);
            var body = $$"""{ "id": "evt_late_{{Guid.NewGuid():N}}", "providerRef": "{{row.ProviderRef}}" }""";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var outcome = await new PaymentService(db, [_mock], new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System)
                .HandleWebhookAsync("mock", WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body), ts.ToString(), body);
            outcome.Should().Be(WebhookOutcome.AlreadyRefunded);
        }
        (await PaymentStatusAsync(payment)).Should().Be(PaymentStatus.Refunded);
        (await Refunds(s => s.QueueAsync())).Should().NotContain(i => i.PaymentId == payment);
    }
}
