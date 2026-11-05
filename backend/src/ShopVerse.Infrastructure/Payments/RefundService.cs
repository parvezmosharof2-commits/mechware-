using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShopVerse.Application.Payments;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Payments;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Payments;

public sealed class RefundService(
    ShopVerseDbContext db, IEnumerable<IPaymentProvider> providers, TimeProvider clock, ILogger<RefundService>? log = null) : IRefundService
{
    private readonly ILogger<RefundService> logger = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RefundService>.Instance;

    public async Task<IReadOnlyList<RefundQueueItem>> QueueAsync(CancellationToken ct = default)
    {
        var open = new[] { RefundStatus.Requested, RefundStatus.Processing, RefundStatus.Failed };
        var paymentIds = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.NeedsRefund).Select(p => p.Id)
            .Union(db.Refunds.AsNoTracking().Where(r => open.Contains(r.Status)).Select(r => r.PaymentId))
            .Take(500).ToListAsync(ct);

        var payments = await db.Payments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).ToListAsync(ct);
        var orderIds = payments.Select(p => p.OrderId).Distinct().ToList();
        var numbers = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        var refunds = (await db.Refunds.AsNoTracking().Where(r => paymentIds.Contains(r.PaymentId)).ToListAsync(ct))
            .GroupBy(r => r.PaymentId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedAtUtc).ToList());

        return payments.OrderBy(p => p.UpdatedAtUtc ?? p.CreatedAtUtc).Select(p =>
        {
            var mine = refunds.GetValueOrDefault(p.Id) ?? [];
            return new RefundQueueItem(p.Id, p.OrderId, numbers.GetValueOrDefault(p.OrderId) ?? string.Empty, p.Provider, p.AmountMinor,
                p.AmountMinor - mine.Where(r => r.HoldsAmount).Sum(r => r.AmountMinor), p.Currency, p.Note, mine.Select(ToDto).ToList());
        }).ToList();
    }

    public async Task<RefundDto> RequestAsync(Guid actorId, Guid paymentId, RequestRefund request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The payment row is the lock: two people asking at once cannot both fit under what the payment still holds.
        var payment = (await db.Payments
            .FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE p.id = {paymentId} FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault() ?? throw new KeyNotFoundException("Payment not found.");
        if (!payment.IsRefundable)
            throw new ConflictException("PAYMENT_NOT_REFUNDABLE", $"A {payment.Status} payment holds no money to send back.");

        var held = await db.Refunds.Where(r => r.PaymentId == paymentId && r.Status != RefundStatus.Rejected).SumAsync(r => (long?)r.AmountMinor, ct) ?? 0;
        if (request.AmountMinor > payment.AmountMinor - held)
            throw new ConflictException("REFUND_EXCEEDS_PAYMENT", $"At most {payment.AmountMinor - held} can still be refunded on this payment.");

        var refund = new Refund(Guid.NewGuid(), paymentId, request.AmountMinor, payment.Currency, request.Reason, actorId, Now);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(refund);
    }

    public async Task<RefundDto> ApproveAsync(Guid actorId, Guid refundId, CancellationToken ct = default)
    {
        // Step 1, in its own transaction: claim the refund. Only one approver can move it to Processing.
        Refund refund;
        Payment payment;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            refund = await LockRefundAsync(refundId, ct);
            refund.Approve(actorId, Now);   // the refund's own state and the four-eyes rule decide first
            payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == refund.PaymentId, ct);
            if (!payment.IsRefundable)
                throw new ConflictException("PAYMENT_NOT_REFUNDABLE", $"A {payment.Status} payment holds no money to send back.");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Step 2: the provider moves the money. The refund id is the idempotency key, so approving again after a failure or a
        // crash can never pay out twice.
        ProviderRefund result;
        try
        {
            result = await Provider(payment.Provider).RefundAsync(payment.ProviderRef, refund.AmountMinor, refund.Id.ToString("N"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Refund {RefundId} could not reach provider {Provider}", refund.Id, payment.Provider);
            result = new ProviderRefund(false, null, "The payment provider could not be reached. Approve again to retry.");
        }

        // Step 3: record the outcome, with the payment locked so the "fully refunded" check sees every completed refund.
        db.ChangeTracker.Clear();
        await using (var tx = await db.Database.BeginTransactionAsync(CancellationToken.None))
        {
            refund = await LockRefundAsync(refundId, CancellationToken.None);
            payment = (await db.Payments
                .FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE p.id = {refund.PaymentId} FOR UPDATE")
                .ToListAsync(CancellationToken.None)).Single();

            if (result.Succeeded)
            {
                refund.Complete(result.RefundRef ?? string.Empty, Now);
                var completed = await db.Refunds.Where(r => r.PaymentId == payment.Id && r.Status == RefundStatus.Completed && r.Id != refund.Id)
                    .SumAsync(r => (long?)r.AmountMinor, CancellationToken.None) ?? 0;
                if (completed + refund.AmountMinor >= payment.AmountMinor && payment.IsRefundable) payment.MarkRefunded(Now);
            }
            else
            {
                refund.Fail(result.Error ?? "The provider refused the refund.", Now);
            }

            await db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
        }

        return ToDto(refund);
    }

    public async Task<RefundDto> RejectAsync(Guid actorId, Guid refundId, RejectRefund request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var refund = await LockRefundAsync(refundId, ct);
        refund.Reject(actorId, request.Reason, Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(refund);
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<Refund> LockRefundAsync(Guid refundId, CancellationToken ct) =>
        (await db.Refunds.FromSqlInterpolated($"SELECT r.*, r.xmin FROM refunds r WHERE r.id = {refundId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
        ?? throw new KeyNotFoundException("Refund not found.");

    private IPaymentProvider Provider(string name) =>
        providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ConflictException("PROVIDER_UNAVAILABLE", $"The provider '{name}' is not configured here, so it cannot refund.");

    private static RefundDto ToDto(Refund r) => new(
        r.Id, r.PaymentId, r.AmountMinor, r.Currency, r.Reason, r.Status.ToString(), r.RequestedBy, r.DecidedBy, r.CreatedAtUtc, r.DecidedAtUtc,
        r.ProviderRefundRef, r.Note);
}
