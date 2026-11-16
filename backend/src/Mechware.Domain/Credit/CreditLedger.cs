using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;

namespace Mechware.Domain.Credit;

/// <summary>
/// What one charged order stands at, from its ledger lines: charge = reduced + receivable + paid, each ≥ 0 (ADR-0010 I4, I6).
/// "Reduced" is what cancellation, credit notes and write-offs took off; "paid" is what payments and on-account money covered.
/// </summary>
public readonly record struct CreditOrderBalance(long ChargeMinor, long ReducedMinor, long ReceivableMinor)
{
    public long PaidMinor => ChargeMinor - ReducedMinor - ReceivableMinor;

    internal CreditOrderBalance With(CreditLedgerKind kind, long r, long a) => kind switch
    {
        CreditLedgerKind.Charge => new(ChargeMinor + r, ReducedMinor, ReceivableMinor + r),
        // A reduction lowers what is owed (R) or turns money already paid into on-account money (A).
        CreditLedgerKind.ChargeReduced => new(ChargeMinor, ReducedMinor - r + a, ReceivableMinor + r),
        CreditLedgerKind.WriteOff => new(ChargeMinor, ReducedMinor - r, ReceivableMinor + r),
        _ => new(ChargeMinor, ReducedMinor, ReceivableMinor + r)
    };
}

/// <summary>The line a command posted, or the one it had already posted (a replay changes nothing).</summary>
public sealed record CreditPostResult(CreditLedgerEntry Entry, bool AlreadyApplied);

/// <summary>
/// The rules of one company's credit ledger (ADR-0010 §1-§3), over the lines a command has loaded: every line in tests, the
/// lines of the orders it touches in a service (the database unique key on the idempotency key is the backstop). A line's
/// key, when already posted, makes the command a no-op; an order is charged once; no order ever owes less than nothing or more
/// than its charge, and nothing reduces or reverses more than the order had. Numbering and the cached totals stay in
/// <see cref="Company.PostCredit"/>. Pure: no I/O, no clock.
/// </summary>
public sealed class CreditLedger
{
    private readonly Company _company;
    private readonly Dictionary<string, CreditLedgerEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CreditOrderBalance> _orders = [];

    public CreditLedger(Company company, IEnumerable<CreditLedgerEntry> loaded)
    {
        _company = company;
        foreach (var e in loaded.OrderBy(e => e.Seq))
        {
            if (e.CompanyId != company.Id) throw new InvalidOperationException("A ledger line of another company was loaded.");
            Track(e);
        }
    }

    public CreditOrderBalance BalanceOf(Guid orderId) => _orders.GetValueOrDefault(orderId);

    /// <summary>The confirmation of a credit order (§3): one charge per order, key <c>charge:{orderId}</c>.</summary>
    public CreditPostResult Charge(Guid orderId, long amountMinor, DateTime confirmedAtUtc, DateTime termsStartUtc,
        Guid? actorUserId = null, string? correlationId = null)
    {
        if (_byKey.TryGetValue(CreditLedgerKeys.Charge(orderId), out var done))
        {
            if (done.ReceivableDeltaMinor != amountMinor)
                throw new ConflictException("CREDIT_LEDGER_KEY_REUSED", "This order was already charged with another amount.");
            return new CreditPostResult(done, true);
        }
        var entry = _company.ChargeCredit(orderId, amountMinor, confirmedAtUtc, termsStartUtc, actorUserId, correlationId);
        Track(entry);
        return new CreditPostResult(entry, false);
    }

    /// <summary>Any other line (payments, reversals, reductions, adjustments): checked against the order it is about.</summary>
    public CreditPostResult Post(CreditPosting posting, DateTime nowUtc)
    {
        if (posting.Kind == CreditLedgerKind.Charge)
            throw new InvalidOperationException("A charge goes through Charge, which checks the company's credit.");
        if (_byKey.TryGetValue(posting.IdempotencyKey, out var done))
        {
            if (!done.Matches(posting))
                throw new ConflictException("CREDIT_LEDGER_KEY_REUSED", "This ledger key was already used for another line.");
            return new CreditPostResult(done, true);
        }

        CreditLedgerEntry.Validate(posting);
        if (posting.OrderId is { } orderId)
        {
            var before = BalanceOf(orderId);
            if (before.ChargeMinor == 0)
                throw new ConflictException("CREDIT_ORDER_NOT_CHARGED", "This order has no charge on the company's credit.");
            var after = before.With(posting.Kind, posting.ReceivableDeltaMinor, posting.OnAccountDeltaMinor);
            if (after.ReceivableMinor < 0)
                throw new ConflictException("CREDIT_EXCEEDS_RECEIVABLE", "This is more than the order still owes.");
            if (after.PaidMinor < 0)
                throw new ConflictException("CREDIT_EXCEEDS_PAID", "This is more than was paid on the order.");
        }

        var entry = _company.PostCredit(posting, nowUtc);
        Track(entry);
        return new CreditPostResult(entry, false);
    }

    private void Track(CreditLedgerEntry e)
    {
        _byKey[e.IdempotencyKey] = e;
        if (e.OrderId is { } id) _orders[id] = BalanceOf(id).With(e.Kind, e.ReceivableDeltaMinor, e.OnAccountDeltaMinor);
    }
}
