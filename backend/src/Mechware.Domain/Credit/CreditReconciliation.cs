namespace Mechware.Domain.Credit;

/// <summary>A company's cached credit totals and hold notes, as one snapshot read them.</summary>
public sealed record CreditBookCompany(Guid Id, long CreditUsedMinor, long OnAccountMinor, long LedgerSeq, DateTime? HoldSinceUtc, DateTime? HoldOverrideUntilUtc);

/// <summary>One ledger line, as read (no rule applied: the reconciliation must see a broken line as it is).</summary>
public sealed record CreditBookLine(long Seq, CreditLedgerKind Kind, Guid? OrderId, Guid? PaymentId, Guid? AdjustmentId, Guid? CreditNoteId,
    long ReceivableDeltaMinor, long OnAccountDeltaMinor, long ReceivableAfterMinor, long OnAccountAfterMinor, DateOnly? DueOn, DateTime CreatedAtUtc);

/// <summary><paramref name="Confirmed"/>: it has a confirmation time or a Confirmed event (as the B3.3 backfill reads it).</summary>
public sealed record CreditBookOrder(Guid Id, Guid? CompanyId, bool PaysOnCredit, bool Confirmed, bool Cancelled, long GrandTotalMinor);

public sealed record CreditBookPayment(Guid Id, Guid CompanyId, CreditPaymentStatus Status, long AmountMinor);

public sealed record CreditBookAdjustment(Guid Id, Guid CompanyId, CreditAdjustmentStatus Status, CreditAdjustmentKind Kind, long AmountMinor,
    Guid? OrderId, Guid? PaymentId, Guid? CreditNoteId);

/// <summary>
/// One company's books from ONE snapshot: every line of the company, and every order, payment and adjustment that is the company's
/// or that one of its lines names (a named record of another company, or none at all, is a finding).
/// </summary>
public sealed record CreditBook(CreditBookCompany Company, IReadOnlyList<CreditBookLine> Lines, IReadOnlyList<CreditBookOrder> Orders,
    IReadOnlyList<CreditBookPayment> Payments, IReadOnlyList<CreditBookAdjustment> Adjustments);

/// <summary>
/// What the hold notes (B3.7) should say. The sweep runs every 15 minutes, so a note may lag the ledger: the note is compared only when
/// the ledger gave the same answer now and at <paramref name="CutoffUtc"/> (lines created up to then, due dates against
/// <paramref name="DueBeforeAtCutoff"/>). A lift may not end after <paramref name="LatestOverrideUntilUtc"/> (now + the longest lift).
/// </summary>
public sealed record CreditHoldExpectation(DateOnly? DueBeforeNow, DateOnly? DueBeforeAtCutoff, DateTime CutoffUtc, DateTime LatestOverrideUntilUtc);

/// <summary>A mismatch: which check, about what (an order, payment or adjustment id; null = the company), and the two numbers compared
/// (amounts, counts or line numbers; 0/0 when the check is not a number).</summary>
public sealed record CreditReconciliationFinding(Guid CompanyId, string Check, Guid? SubjectId, long Expected, long Actual);

/// <summary>
/// ADR-0010 §11 (B3.10, threat model CS28), pure: checks one company's books and returns what does not add up. It repairs nothing and
/// assumes nothing it checks: I1/I2 (cached totals = the sums of the lines), I3 (lines 1, 2, 3 … with correct running totals, and the
/// company's line counter), I4/I6 after every line (no order owes below zero, nothing paid or reduced below zero), each order's paid
/// part attributed to its sources, one charge per confirmed credit order equal to its total, a cancelled order fully released, no
/// source's on-account money below zero after any line, I5 per payment (its verification lines = its amount; what it still pays and
/// holds = its amount ± its adjustments, 0 once bounced), no line for a payment or adjustment that is not verified / approved, an
/// approved adjustment's one line, no line naming a record of another company or none, and the hold notes.
/// </summary>
public static class CreditReconciliation
{
    public const string CreditUsed = "credit_used", OnAccount = "on_account", LedgerChain = "ledger_chain", LedgerSeq = "ledger_seq",
        OrderCharge = "order_charge", OrderChargeAmount = "order_charge_amount", OrderBalance = "order_balance",
        OrderPaidAttribution = "order_paid_attribution", CancelledOrderNotReleased = "cancelled_order_not_released",
        SourceOnAccountNegative = "source_on_account_negative", PaymentVerifiedAmount = "payment_verified_amount",
        PaymentBalance = "payment_balance", PaymentNotVerified = "payment_not_verified", AdjustmentLine = "adjustment_line",
        ForeignRecord = "foreign_record", UnknownRecord = "unknown_record", UnknownCompany = "unknown_company",
        HoldNote = "hold_note", HoldOverrideTooLong = "hold_override_too_long";

    public static IReadOnlyList<CreditReconciliationFinding> Check(CreditBook book, CreditHoldExpectation hold)
    {
        var c = book.Company;
        var found = new List<CreditReconciliationFinding>();
        void Add(string check, Guid? subject, long expected, long actual) => found.Add(new(c.Id, check, subject, expected, actual));
        var lines = book.Lines.OrderBy(l => l.Seq).ToList();

        // I3, then I1/I2 and the line counter against the lines themselves (not the last running total, which I3 may already doubt).
        long seq = 0, r = 0, a = 0;
        foreach (var l in lines)
        {
            if (l.Seq != seq + 1 || l.ReceivableAfterMinor != r + l.ReceivableDeltaMinor || l.OnAccountAfterMinor != a + l.OnAccountDeltaMinor)
            {
                Add(LedgerChain, null, seq + 1, l.Seq);
                break;
            }
            (seq, r, a) = (l.Seq, l.ReceivableAfterMinor, l.OnAccountAfterMinor);
        }
        if (c.LedgerSeq != (lines.Count == 0 ? 0 : lines[^1].Seq)) Add(LedgerSeq, null, lines.Count == 0 ? 0 : lines[^1].Seq, c.LedgerSeq);
        if (c.CreditUsedMinor != lines.Sum(l => l.ReceivableDeltaMinor)) Add(CreditUsed, null, lines.Sum(l => l.ReceivableDeltaMinor), c.CreditUsedMinor);
        if (c.OnAccountMinor != lines.Sum(l => l.OnAccountDeltaMinor)) Add(OnAccount, null, lines.Sum(l => l.OnAccountDeltaMinor), c.OnAccountMinor);

        // I4/I6 per order and on-account per source, after every line in order.
        var balances = new Dictionary<Guid, CreditOrderBalance>();
        var held = new Dictionary<(Guid?, Guid?), long>();
        var broken = new HashSet<Guid>();
        foreach (var l in lines)
        {
            if (l.OrderId is { } o)
            {
                var b = balances[o] = balances.GetValueOrDefault(o).With(l.Kind, l.ReceivableDeltaMinor, l.OnAccountDeltaMinor);
                if ((b.ReceivableMinor < 0 || b.ReducedMinor < 0 || b.PaidMinor < 0) && broken.Add(o))
                    Add(OrderBalance, o, 0, Math.Min(b.ReceivableMinor, Math.Min(b.ReducedMinor, b.PaidMinor)));
            }
            if (l.OnAccountDeltaMinor == 0) continue;
            var source = (l.PaymentId, l.CreditNoteId);
            var left = held[source] = held.GetValueOrDefault(source) + l.OnAccountDeltaMinor;
            if (left < 0 && broken.Add(l.PaymentId ?? l.CreditNoteId ?? Guid.Empty)) Add(SourceOnAccountNegative, l.PaymentId ?? l.CreditNoteId, 0, left);
        }

        var orders = book.Orders.ToDictionary(o => o.Id);
        var byOrder = lines.Where(l => l.OrderId is not null).ToLookup(l => l.OrderId!.Value);
        foreach (var id in byOrder.Select(g => g.Key).Union(book.Orders.Where(o => o.CompanyId == c.Id && o.PaysOnCredit && o.Confirmed).Select(o => o.Id)))
        {
            if (Owned(orders.GetValueOrDefault(id), id, x => x.CompanyId == c.Id && x.PaysOnCredit) is not { } order) continue;
            var mine = byOrder[id].ToList();
            var charges = mine.Where(l => l.Kind == CreditLedgerKind.Charge).ToList();
            if (charges.Count != 1)
            {
                Add(OrderCharge, id, 1, charges.Count);
                continue;
            }
            if (charges[0].ReceivableDeltaMinor != order.GrandTotalMinor) Add(OrderChargeAmount, id, order.GrandTotalMinor, charges[0].ReceivableDeltaMinor);
            var balance = balances[id];
            var bySource = mine.Where(l => l.PaymentId is not null || l.CreditNoteId is not null).GroupBy(l => (l.PaymentId, l.CreditNoteId))
                .Select(g => g.Sum(Paid)).ToList();
            if (bySource.Any(p => p < 0) || bySource.Sum() != balance.PaidMinor) Add(OrderPaidAttribution, id, balance.PaidMinor, bySource.Sum());
            if (order.Cancelled && balance.ReducedMinor != balance.ChargeMinor) Add(CancelledOrderNotReleased, id, balance.ChargeMinor, balance.ReducedMinor);
        }

        var payments = book.Payments.ToDictionary(p => p.Id);
        var byPayment = lines.Where(l => l.PaymentId is not null).ToLookup(l => l.PaymentId!.Value);
        foreach (var id in byPayment.Select(g => g.Key).Union(book.Payments.Where(p => p.CompanyId == c.Id).Select(p => p.Id)))
        {
            if (Owned(payments.GetValueOrDefault(id), id, x => x.CompanyId == c.Id) is not { } p) continue;
            var mine = byPayment[id].ToList();
            if (p.Status is not (CreditPaymentStatus.Verified or CreditPaymentStatus.Bounced))
            {
                if (mine.Count > 0) Add(PaymentNotVerified, id, 0, mine.Count);
                continue;
            }
            var verified = mine.Where(l => l.Kind is CreditLedgerKind.PaymentApplied or CreditLedgerKind.PaymentOnAccount)
                .Sum(l => l.OnAccountDeltaMinor - l.ReceivableDeltaMinor);
            if (verified != p.AmountMinor) Add(PaymentVerifiedAmount, id, p.AmountMinor, verified);
            // What it still pays on orders plus what it holds on account: its amount ± what corrections and payouts moved; 0 once bounced.
            var adjusted = mine.Where(l => l.Kind is CreditLedgerKind.OnAccountCorrection or CreditLedgerKind.OnAccountPayout).Sum(l => l.OnAccountDeltaMinor);
            var expected = p.Status == CreditPaymentStatus.Bounced ? 0 : p.AmountMinor + adjusted;
            var still = mine.Sum(l => (l.OrderId is null ? 0 : Paid(l)) + l.OnAccountDeltaMinor);
            if (still != expected) Add(PaymentBalance, id, expected, still);
        }

        var adjustments = book.Adjustments.ToDictionary(x => x.Id);
        var byAdjustment = lines.Where(l => l.AdjustmentId is not null).ToLookup(l => l.AdjustmentId!.Value);
        foreach (var id in byAdjustment.Select(g => g.Key).Union(book.Adjustments.Where(x => x.CompanyId == c.Id).Select(x => x.Id)))
        {
            if (Owned(adjustments.GetValueOrDefault(id), id, y => y.CompanyId == c.Id) is not { } x) continue;
            var mine = byAdjustment[id].ToList();
            if (x.Status != CreditAdjustmentStatus.Approved)
            {
                if (mine.Count > 0) Add(AdjustmentLine, id, 0, mine.Count);
                continue;
            }
            var (kind, dr, da) = x.Kind switch
            {
                CreditAdjustmentKind.WriteOff => (CreditLedgerKind.WriteOff, -x.AmountMinor, 0L),
                CreditAdjustmentKind.OnAccountCorrection => (CreditLedgerKind.OnAccountCorrection, 0L, x.AmountMinor),
                _ => (CreditLedgerKind.OnAccountPayout, 0L, -x.AmountMinor)
            };
            if (mine.Count != 1) Add(AdjustmentLine, id, 1, mine.Count);
            else if (mine[0] is var l && (l.Kind != kind || l.ReceivableDeltaMinor != dr || l.OnAccountDeltaMinor != da || l.OrderId != x.OrderId
                     || l.PaymentId != x.PaymentId || l.CreditNoteId != x.CreditNoteId))
                Add(AdjustmentLine, id, dr + da, l.ReceivableDeltaMinor + l.OnAccountDeltaMinor);
        }

        // The hold notes (B3.7): a lift never longer than allowed; the "hold since" note agrees with a ledger that has been stable.
        if (c.HoldOverrideUntilUtc > hold.LatestOverrideUntilUtc) Add(HoldOverrideTooLong, null, 0, 0);
        bool overdue = Overdue(lines, hold.DueBeforeNow), before = Overdue(lines.Where(l => l.CreatedAtUtc <= hold.CutoffUtc), hold.DueBeforeAtCutoff);
        if (overdue == before && overdue != (c.HoldSinceUtc is not null)) Add(HoldNote, null, overdue ? 1 : 0, c.HoldSinceUtc is null ? 0 : 1);
        return found;

        // A named record must exist and be the company's (or a credit order of it).
        T? Owned<T>(T? record, Guid id, Func<T, bool> ours) where T : class
        {
            if (record is null) Add(UnknownRecord, id, 0, 0);
            else if (!ours(record)) Add(ForeignRecord, id, 0, 0);
            else return record;
            return null;
        }
    }

    /// <summary>What a line means for the money its source has paid on its order (the attribution of <see cref="CreditReversals"/>).</summary>
    private static long Paid(CreditBookLine l) => l.Kind == CreditLedgerKind.ChargeReduced ? -l.OnAccountDeltaMinor : -l.ReceivableDeltaMinor;

    /// <summary>The hold rule of B3.7 over these lines: an order that still owes on a charge due before <paramref name="dueBefore"/>.</summary>
    private static bool Overdue(IEnumerable<CreditBookLine> lines, DateOnly? dueBefore) =>
        dueBefore is { } d && lines.Where(l => l.OrderId is not null).GroupBy(l => l.OrderId)
            .Any(g => g.Any(l => l.Kind == CreditLedgerKind.Charge && l.DueOn < d) && g.Sum(l => l.ReceivableDeltaMinor) > 0);
}
