using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Credit;

/// <summary>An order the recorder wants a payment applied to, and how much (ADR-0010 §4).</summary>
public sealed record CreditAllocationRequest(Guid OrderId, long AmountMinor);

/// <summary>One open receivable, read under the company lock: what the order still owes, its due date and its charge's instant.</summary>
public sealed record CreditOpenCharge(Guid OrderId, long ReceivableMinor, DateOnly DueOn, DateTime ConfirmedAtUtc);

public sealed record CreditAllocation(Guid OrderId, long AmountMinor);

/// <summary>Where a payment's money goes. Σ allocations + on-account = the payment, to the poisha.</summary>
public sealed record CreditAllocationPlan(IReadOnlyList<CreditAllocation> Allocations, long OnAccountMinor)
{
    /// <summary>The verification's ledger lines (§7): one <c>payment_applied</c> per order, one <c>payment_on_account</c> for the rest.</summary>
    public IReadOnlyList<CreditPosting> Postings(Guid paymentId, DateOnly occurredOn, Guid eventId, Guid? actorUserId, string? correlationId)
    {
        var lines = Allocations.Select(a => new CreditPosting(CreditLedgerKind.PaymentApplied, -a.AmountMinor, 0,
            CreditLedgerKeys.Verify(paymentId, a.OrderId), occurredOn, a.OrderId, paymentId, EventId: eventId, ActorUserId: actorUserId,
            CorrelationId: correlationId)).ToList();
        if (OnAccountMinor > 0)
            lines.Add(new CreditPosting(CreditLedgerKind.PaymentOnAccount, 0, OnAccountMinor, CreditLedgerKeys.VerifyOnAccount(paymentId),
                occurredOn, PaymentId: paymentId, EventId: eventId, ActorUserId: actorUserId, CorrelationId: correlationId));
        return lines;
    }
}

/// <summary>
/// ADR-0010 §6 (threat model CS6, CS8, CS9), pure: the recorder's allocations first, each exactly as asked and never above
/// what that order still owes (refused with the order id, never clamped); then the other open receivables, oldest
/// <c>due_on</c> first (tie: older confirmation, then the smaller order id in PostgreSQL's uuid order, which
/// <see cref="Guid.CompareTo(Guid)"/> follows); the rest on account, which does not raise available credit. Whole poisha,
/// so nothing rounds; the input order of the charges does not matter.
/// </summary>
public static class CreditAllocationPlanner
{
    public const int MaxRequests = 50;

    /// <summary>At most 50 orders, each once, each 1 poisha to the payment, together no more than the payment.</summary>
    public static void CheckRequests(IReadOnlyCollection<CreditAllocationRequest> requests, long amountMinor)
    {
        if (requests.Count > MaxRequests) throw new ValidationException("allocations", $"Name at most {MaxRequests} orders.");
        if (requests.Any(r => r.AmountMinor < 1 || r.AmountMinor > amountMinor) || requests.Sum(r => r.AmountMinor) > amountMinor)
            throw new ValidationException("allocations", "Each allocation is at least 1 poisha, and together they are at most the payment.");
        if (requests.DistinctBy(r => r.OrderId).Count() != requests.Count)
            throw new ValidationException("allocations", "Name each order at most once.");
    }

    public static CreditAllocationPlan Plan(long amountMinor, IEnumerable<CreditOpenCharge> openCharges,
        IReadOnlyList<CreditAllocationRequest>? requests = null)
    {
        if (amountMinor < 1) throw new ValidationException("amountMinor", "A payment is at least 1 poisha.");
        requests ??= [];
        CheckRequests(requests, amountMinor);
        var open = new Dictionary<Guid, CreditOpenCharge>();
        foreach (var c in openCharges.Where(c => c.ReceivableMinor != 0))
            if (c.ReceivableMinor < 0 || !open.TryAdd(c.OrderId, c))
                throw new InvalidOperationException("Open charges must be loaded once per order, never below zero.");

        var plan = new List<CreditAllocation>();
        var left = amountMinor;
        foreach (var r in requests)
        {
            if (!open.Remove(r.OrderId, out var c))
                throw new ConflictException("ALLOCATION_ORDER_NOT_OPEN", $"Order {r.OrderId} owes nothing on this company's credit.");
            if (r.AmountMinor > c.ReceivableMinor)
                throw new ConflictException("ALLOCATION_EXCEEDS_OUTSTANDING", $"Order {r.OrderId} still owes {c.ReceivableMinor} poisha, less than {r.AmountMinor}.");
            plan.Add(new CreditAllocation(r.OrderId, r.AmountMinor));
            left -= r.AmountMinor;
        }
        foreach (var c in open.Values.OrderBy(c => c.DueOn).ThenBy(c => c.ConfirmedAtUtc).ThenBy(c => c.OrderId))
        {
            if (left == 0) break;
            var x = Math.Min(left, c.ReceivableMinor);
            plan.Add(new CreditAllocation(c.OrderId, x));
            left -= x;
        }
        return new CreditAllocationPlan(plan, left);
    }
}
