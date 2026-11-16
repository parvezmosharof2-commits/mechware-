using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Credit;

/// <summary>Who did a reversal and when: shared by every line of one event.</summary>
public sealed record CreditEventInfo(DateOnly OccurredOn, Guid EventId, Guid? ActorUserId, string? CorrelationId, string Reason);

/// <summary>The lines of one reduction (§8.2), or the lines it had already posted (<paramref name="AlreadyApplied"/>).</summary>
public sealed record CreditReductionPlan(IReadOnlyList<CreditPosting> Postings, long ReceivableReleasedMinor, long MovedOnAccountMinor, bool AlreadyApplied);

/// <summary>
/// ADR-0010 §8.1 and §8.2, pure: the lines that undo money. Both read the same attribution: on an order, a source (a payment, or
/// a credit note's on-account money) has paid what its <c>payment_applied</c> / <c>on_account_applied</c> lines took off the
/// receivable, less what a <c>payment_reversed</c> put back and what a <c>charge_reduced</c> moved on account in its name; a
/// source's on-account money is the sum of the on-account deltas that name it. New lines only: nothing old is ever changed.
/// </summary>
public static class CreditReversals
{
    public const int MaxSourceKeyLength = 60;

    /// <summary>
    /// A bounce (§8.1): one <c>payment_reversed</c> line puts back on each order what this payment still pays there (directly or
    /// through applied on-account money), one more takes its remaining on-account money away. The original due dates stay. A
    /// payment whose money is all reversed already yields no line, so a second bounce changes nothing.
    /// </summary>
    public static IReadOnlyList<CreditPosting> Bounce(Guid paymentId, IEnumerable<CreditLedgerEntry> lines, CreditEventInfo e)
    {
        var mine = lines.Where(l => l.PaymentId == paymentId).ToList();
        var postings = mine.Where(l => l.OrderId is not null).GroupBy(l => l.OrderId!.Value)
            .Select(g => (Order: g.Key, Paid: g.Sum(Paid), First: g.Min(l => l.Seq))).Where(o => o.Paid != 0).OrderBy(o => o.First)
            .Select(o => o.Paid > 0
                ? Line(CreditLedgerKind.PaymentReversed, o.Paid, 0, CreditLedgerKeys.Bounce(paymentId, o.Order), o.Order, paymentId, null, e)
                : throw new InvalidOperationException($"Payment {paymentId} has more taken back than paid on order {o.Order}."))
            .ToList();
        var onAccount = mine.Sum(l => l.OnAccountDeltaMinor);
        if (onAccount < 0) throw new InvalidOperationException($"Payment {paymentId} has spent more on-account money than it brought.");
        if (onAccount > 0)
            postings.Add(Line(CreditLedgerKind.PaymentReversed, 0, -onAccount, CreditLedgerKeys.BounceOnAccount(paymentId), null, paymentId, null, e));
        return postings;
    }

    /// <summary>
    /// A cancellation or credit note (§8.2): reduces the order by <paramref name="amountMinor"/> (null = all not yet reduced),
    /// at most its charge less earlier reductions and write-offs (409 <c>CREDIT_REDUCTION_EXCEEDS_CHARGE</c>). What the order
    /// still owes falls first; the rest is money already paid and goes on account in the name of the sources that paid it,
    /// newest first, one line per source. The same <paramref name="sourceKey"/> again finds its lines: the same amount (or none)
    /// is a no-op, another amount 409 <c>CREDIT_REDUCTION_CONFLICT</c>.
    /// </summary>
    public static CreditReductionPlan Reduce(Guid orderId, CreditOrderBalance balance, IEnumerable<CreditLedgerEntry> orderLines, long? amountMinor,
        string sourceKey, Guid? creditNoteId, CreditEventInfo e)
    {
        if (sourceKey is not { Length: > 0 and <= MaxSourceKeyLength } || sourceKey.Any(c => c is <= ' ' or > '~'))
            throw new ValidationException("sourceKey", $"A source key is 1 to {MaxSourceKeyLength} printable ASCII characters.");
        if (amountMinor is < 1) throw new ValidationException("amountMinor", "A reduction is at least 1 poisha.");
        if (string.IsNullOrWhiteSpace(e.Reason)) throw new ValidationException("reason", "Give the reason for the reduction.");
        var prefix = CreditLedgerKeys.Reduce(sourceKey);
        var lines = orderLines.Where(l => l.OrderId == orderId).ToList();

        var done = lines.Where(l => l.IdempotencyKey.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (done.Count > 0)
        {
            long released = -done.Sum(l => l.ReceivableDeltaMinor), moved = done.Sum(l => l.OnAccountDeltaMinor);
            if (amountMinor is { } asked && asked != released + moved)
                throw new ConflictException("CREDIT_REDUCTION_CONFLICT", $"This reduction was already applied with {released + moved} poisha.");
            return new CreditReductionPlan([], released, moved, true);
        }

        var reducible = balance.ChargeMinor - balance.ReducedMinor;
        var total = amountMinor ?? reducible;
        if (total > reducible)
            throw new ConflictException("CREDIT_REDUCTION_EXCEEDS_CHARGE", $"The order can be reduced by at most {reducible} poisha more.");
        var fromReceivable = Math.Min(total, balance.ReceivableMinor);
        var postings = new List<CreditPosting>();
        if (fromReceivable > 0)
            postings.Add(Line(CreditLedgerKind.ChargeReduced, -fromReceivable, 0, prefix + "receivable", orderId, null, creditNoteId, e));

        var rest = total - fromReceivable;
        var sources = lines.Where(l => l.PaymentId is not null || l.CreditNoteId is not null).GroupBy(l => (l.PaymentId, l.CreditNoteId))
            .Select(g => (g.Key.PaymentId, g.Key.CreditNoteId, Paid: g.Sum(Paid), Newest: g.Where(l => Paid(l) > 0).Select(l => l.Seq).DefaultIfEmpty().Max()))
            .Where(s => s.Paid > 0).OrderByDescending(s => s.Newest);
        foreach (var s in sources)
        {
            if (rest == 0) break;
            var x = Math.Min(rest, s.Paid);
            postings.Add(Line(CreditLedgerKind.ChargeReduced, 0, x, prefix + (s.PaymentId ?? s.CreditNoteId)!.Value.ToString("N"), orderId,
                s.PaymentId, s.CreditNoteId, e));
            rest -= x;
        }
        if (rest != 0) throw new InvalidOperationException($"The paid part of order {orderId} is not fully attributed to its payments.");
        return new CreditReductionPlan(postings, fromReceivable, total - fromReceivable, false);
    }

    /// <summary>What one line means for the money its source has on its order (see the class summary).</summary>
    private static long Paid(CreditLedgerEntry l) => l.Kind == CreditLedgerKind.ChargeReduced ? -l.OnAccountDeltaMinor : -l.ReceivableDeltaMinor;

    private static CreditPosting Line(CreditLedgerKind kind, long r, long a, string key, Guid? order, Guid? payment, Guid? note, CreditEventInfo e) =>
        new(kind, r, a, key, e.OccurredOn, order, payment, CreditNoteId: note, EventId: e.EventId, ActorUserId: e.ActorUserId,
            CorrelationId: e.CorrelationId, Reason: e.Reason);
}
