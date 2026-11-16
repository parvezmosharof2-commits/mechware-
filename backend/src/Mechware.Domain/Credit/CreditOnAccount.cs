using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Credit;

/// <summary>On-account money held in the name of one source (a payment, or a credit note), and the seq of its first on-account line.</summary>
public sealed record CreditOnAccountSource(Guid? PaymentId, Guid? CreditNoteId, long OnAccountMinor, long FirstSeq);

/// <summary>One <c>on_account_applied</c> line: this much of this source's money now pays this order.</summary>
public sealed record CreditOnAccountApplication(Guid OrderId, Guid? PaymentId, Guid? CreditNoteId, long AmountMinor);

/// <summary>
/// ADR-0010 §6 "apply on-account" (B3.5b, threat model CS9), pure. A source holds the sum of the on-account deltas that name it
/// (the same attribution a bounce and a reduction read, <see cref="CreditReversals"/>). The orders are chosen by the same
/// <see cref="CreditAllocationPlanner"/> as a verification (named orders first, then the oldest due); the money comes from the
/// oldest source first (lowest seq of its first on-account line: the money that came on account first), one line per (order,
/// source) with R = A = −x. Nothing is clamped: more than is held or owed is refused.
/// </summary>
public static class CreditOnAccount
{
    public const string Reason = "On-account money applied to open orders";

    /// <summary>Every source still holding money, oldest first. A source below zero or naming no single source is a broken ledger.</summary>
    public static IReadOnlyList<CreditOnAccountSource> Sources(IEnumerable<CreditLedgerEntry> lines)
    {
        var sources = lines.Where(l => l.OnAccountDeltaMinor != 0).GroupBy(l => (l.PaymentId, l.CreditNoteId))
            .Select(g => new CreditOnAccountSource(g.Key.PaymentId, g.Key.CreditNoteId, g.Sum(l => l.OnAccountDeltaMinor), g.Min(l => l.Seq)))
            .Where(s => s.OnAccountMinor != 0).OrderBy(s => s.FirstSeq).ToList();
        if (sources.Any(s => s.OnAccountMinor < 0 || (s.PaymentId is null) == (s.CreditNoteId is null)))
            throw new InvalidOperationException("On-account money must name exactly one source and never fall below zero.");
        return sources;
    }

    /// <summary>
    /// The lines that apply <paramref name="amountMinor"/> of the held money (null: the named amounts, or with no names all that
    /// fits, i.e. the smaller of held and owed). 409 <c>CREDIT_NOTHING_TO_APPLY</c>, <c>CREDIT_ON_ACCOUNT_INSUFFICIENT</c>,
    /// <c>CREDIT_ON_ACCOUNT_EXCEEDS_OPEN</c>, and the planner's 400/409s. <paramref name="requestKey"/> makes the line keys
    /// (<see cref="CreditLedgerKeys.ApplyOnAccount"/>).
    /// </summary>
    public static IReadOnlyList<CreditPosting> Apply(IReadOnlyList<CreditOnAccountSource> sources, IEnumerable<CreditOpenCharge> openCharges,
        long? amountMinor, IReadOnlyList<CreditAllocationRequest>? requests, string requestKey, CreditEventInfo e)
    {
        var open = openCharges.ToList();
        var oldest = sources.OrderBy(s => s.FirstSeq).ToArray();
        long held = oldest.Sum(s => s.OnAccountMinor), owed = open.Sum(c => c.ReceivableMinor);
        var named = requests is { Count: > 0 };
        var amount = amountMinor ?? (named ? requests!.Sum(r => r.AmountMinor) : Math.Min(held, owed));
        if (amountMinor is null && !named && amount == 0)
            throw new ConflictException("CREDIT_NOTHING_TO_APPLY", held == 0 ? "The company has no money on account." : "No order of the company owes anything.");
        if (amount > held)
            throw new ConflictException("CREDIT_ON_ACCOUNT_INSUFFICIENT", $"The company has {held} poisha on account, less than {amount}.");
        var plan = CreditAllocationPlanner.Plan(amount, open, requests);
        if (plan.OnAccountMinor > 0)
            throw new ConflictException("CREDIT_ON_ACCOUNT_EXCEEDS_OPEN", $"The open orders owe {amount - plan.OnAccountMinor} poisha, less than {amount}.");

        var left = oldest.Select(s => s.OnAccountMinor).ToArray();
        var postings = new List<CreditPosting>();
        var i = 0;
        foreach (var a in plan.Allocations)
            for (var need = a.AmountMinor; need > 0;)
            {
                if (left[i] == 0) { i++; continue; }
                var (s, x) = (oldest[i], Math.Min(need, left[i]));
                postings.Add(new CreditPosting(CreditLedgerKind.OnAccountApplied, -x, -x,
                    CreditLedgerKeys.ApplyOnAccount(requestKey, a.OrderId, (s.PaymentId ?? s.CreditNoteId)!.Value), e.OccurredOn, a.OrderId, s.PaymentId,
                    CreditNoteId: s.CreditNoteId, EventId: e.EventId, ActorUserId: e.ActorUserId, CorrelationId: e.CorrelationId, Reason: e.Reason));
                (left[i], need) = (left[i] - x, need - x);
            }
        return postings;
    }
}
