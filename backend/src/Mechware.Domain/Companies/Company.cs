using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Credit;

namespace Mechware.Domain.Companies;

/// <summary>
/// A B2B customer company (brief §3.1.3, §7). Credit terms and limits are set by MECHWARE finance only;
/// the company itself can edit nothing that affects credit.
/// </summary>
public sealed class Company : AggregateRoot<Guid>
{
    public string LegalName { get; private set; } = string.Empty;
    public string TradeLicenceNo { get; private set; } = string.Empty;
    /// <summary>Business Identification Number: 13 digits (NBR). Format to be confirmed by a VAT consultant.</summary>
    public string Bin { get; private set; } = string.Empty;
    /// <summary>Taxpayer Identification Number: 12 digits, optional.</summary>
    public string? Tin { get; private set; }
    public string? Industry { get; private set; }
    public CompanyStatus Status { get; private set; } = CompanyStatus.Pending;
    public CreditTerms CreditTerms { get; private set; } = CreditTerms.None;
    public Currency Currency { get; private set; } = Currency.BDT;
    public long CreditLimitMinor { get; private set; }
    /// <summary>The sum of open receivables: the running total of the credit ledger, cached here for the limit check (ADR-0010 §1).</summary>
    public long CreditUsedMinor { get; private set; }
    /// <summary>Money MECHWARE holds for the company that is not applied to an order; it does not raise available credit.</summary>
    public long CreditOnAccountMinor { get; private set; }
    /// <summary>The number of the company's last credit ledger line (0 = none).</summary>
    public long CreditLedgerSeq { get; private set; }
    /// <summary>ADR-0010 §9 (B3.7): an Admin lifted the overdue hold until this instant (null or past = not lifted).</summary>
    public DateTime? CreditHoldOverrideUntilUtc { get; private set; }
    /// <summary>When the hold sweep first found the company overdue beyond the hold days (null = not, at its last pass). For events
    /// and reads only: placement and approval ask the ledger itself, under the company lock.</summary>
    public DateTime? CreditHoldSinceUtc { get; private set; }
    public Guid? DefaultPriceListId { get; private set; }
    public Guid? AccountManagerId { get; private set; }

    /// <summary>Why MECHWARE refused the registration; shown to the registrant.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>ADR-0012 §11 (E6.6b): the company deducts VAT or tax at source. Only a hint (invoice PDF, payment text); it never changes an amount.</summary>
    public bool WithholdingEntity { get; private set; }

    public Money CreditLimit => Money.FromMinorUnits(CreditLimitMinor, Currency);
    public Money CreditUsed => Money.FromMinorUnits(CreditUsedMinor, Currency);
    public Money CreditAvailable => CreditLimit - CreditUsed;

    private Company() { }

    private Company(Guid id) : base(id) { }

    public static Company Register(Guid id, string legalName, string tradeLicenceNo, string bin, string? tin, string? industry)
    {
        var errors = new List<ValidationError>();
        legalName = (legalName ?? string.Empty).Trim();
        tradeLicenceNo = (tradeLicenceNo ?? string.Empty).Trim();
        bin = (bin ?? string.Empty).Trim();
        tin = string.IsNullOrWhiteSpace(tin) ? null : tin.Trim();

        if (legalName.Length is < 2 or > 200) errors.Add(new("legalName", "Legal name must be 2-200 characters."));
        if (tradeLicenceNo.Length is < 3 or > 50) errors.Add(new("tradeLicenceNo", "Trade licence number must be 3-50 characters."));
        // Written plain or as #########-####; stored and compared as the 13 digits.
        if (Mechware.Domain.Invoicing.TaxIdFormat.NormalizeBin(bin) is { } digits) bin = digits;
        else errors.Add(new("bin", "BIN must be exactly 13 digits (000000000-0000 is accepted)."));
        if (tin is not null && !IsDigits(tin, 12)) errors.Add(new("tin", "TIN must be exactly 12 digits."));
        if (industry is { Length: > 100 }) errors.Add(new("industry", "Industry must be at most 100 characters."));
        if (errors.Count > 0) throw new ValidationException(errors);

        return new Company(id)
        {
            LegalName = legalName,
            TradeLicenceNo = tradeLicenceNo,
            Bin = bin,
            Tin = tin,
            Industry = string.IsNullOrWhiteSpace(industry) ? null : industry.Trim()
        };
    }

    public bool IsActive => Status == CompanyStatus.Active;

    public void Activate()
    {
        if (Status == CompanyStatus.Rejected)
            throw new ConflictException("COMPANY_REJECTED", "A rejected registration cannot be activated; the company must register again.");
        if (Status == CompanyStatus.Active) return;
        Status = CompanyStatus.Active;
        Touch();
    }

    /// <summary>Refuses a registration that was never verified, and frees its BIN for the real owner.</summary>
    public void Reject(string reason)
    {
        if (Status != CompanyStatus.Pending)
            throw new ConflictException("COMPANY_NOT_PENDING", "Only a registration waiting for verification can be rejected; suspend an active company instead.");
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 500) throw new ValidationException("reason", "Give a reason of 5 to 500 characters.");
        Status = CompanyStatus.Rejected;
        RejectionReason = trimmed;
        Touch();
    }

    public void Suspend()
    {
        if (Status == CompanyStatus.Rejected)
            throw new ConflictException("COMPANY_REJECTED", "A rejected registration cannot be suspended.");
        if (Status == CompanyStatus.Suspended) return;
        Status = CompanyStatus.Suspended;
        Touch();
    }

    /// <summary>Sets the withholding hint; false when it already had that value (nothing to audit).</summary>
    public bool MarkWithholdingEntity(bool value)
    {
        if (WithholdingEntity == value) return false;
        WithholdingEntity = value;
        Touch();
        return true;
    }

    public void AssignAccountManager(Guid? staffUserId)
    {
        AccountManagerId = staffUserId;
        Touch();
    }

    /// <summary>Finance sets terms and limit. A limit cannot drop below what is already used.</summary>
    public void SetCredit(CreditTerms terms, Money limit)
    {
        if (!Enum.IsDefined(terms)) throw new ValidationException("terms", "Unknown credit terms.");
        if (limit.Currency != Currency) throw new CurrencyMismatchException(limit.Currency, Currency);
        if (limit.IsNegative) throw new ValidationException("limit", "Credit limit cannot be negative.");
        if (terms == CreditTerms.None && !limit.IsZero)
            throw new ValidationException("limit", "Credit limit must be zero when no credit terms are granted.");
        if (terms != CreditTerms.None && limit.IsZero)
            throw new ValidationException("limit", "A positive credit limit is required for net terms.");
        if (limit.ToMinorUnits() < CreditUsedMinor)
            throw new ConflictException("CREDIT_LIMIT_BELOW_USED", "The limit cannot be lower than the credit already used.");

        CreditTerms = terms;
        CreditLimitMinor = limit.ToMinorUnits();
        Touch();
    }

    /// <summary>
    /// Brief §8.7, ADR-0010 §3: the charge of a credit order at its confirmation, allowed only if <c>used + total ≤ limit</c>.
    /// Returns the ledger line, which the caller stores in the same transaction. The due date is the Dhaka date the terms
    /// start (<paramref name="termsStartUtc"/>; the confirmation by default, owner decision 1) plus the company's terms days
    /// now; a later terms change never moves it. The caller must hold the company row lock (or rely on the xmin concurrency
    /// token) so two orders cannot both pass the check. Use <see cref="CreditLedger.Charge"/>, which also makes a replay a no-op.
    /// </summary>
    public CreditLedgerEntry ChargeCredit(Guid orderId, long amountMinor, DateTime confirmedAtUtc, DateTime termsStartUtc,
        Guid? actorUserId = null, string? correlationId = null)
    {
        if (Currency != Currency.BDT) throw new CurrencyMismatchException(Currency.BDT, Currency);
        if (amountMinor <= 0) throw new ValidationException("amount", "Amount must be positive.");
        if (!IsActive) throw new ConflictException("COMPANY_NOT_ACTIVE", "The company account is not active.");
        if (CreditTerms == CreditTerms.None) throw new ConflictException("NO_CREDIT_TERMS", "The company has no credit terms.");
        if (amountMinor > CreditLimitMinor - CreditUsedMinor)
            throw new ConflictException("CREDIT_LIMIT_EXCEEDED", "The order exceeds the available credit limit.");

        var dueOn = CreditCalendar.DhakaDate(termsStartUtc).AddDays((int)CreditTerms);
        return PostCredit(new CreditPosting(CreditLedgerKind.Charge, amountMinor, 0, CreditLedgerKeys.Charge(orderId),
            CreditCalendar.DhakaDate(confirmedAtUtc), OrderId: orderId, DueOn: dueOn, ActorUserId: actorUserId, CorrelationId: correlationId),
            confirmedAtUtc);
    }

    public const string CreditHoldCode = "CREDIT_HOLD_OVERDUE";

    public bool CreditHoldLifted(DateTime nowUtc) => CreditHoldOverrideUntilUtc > nowUtc;

    /// <summary>
    /// ADR-0010 §9 (owner decision 2): no new credit order while a charge that still owes is more than the hold days past due,
    /// unless an Admin lifted the hold. <paramref name="overdueSince"/> = the oldest such due date (null = none), read under the
    /// company row lock by the caller, right before the charge.
    /// </summary>
    public void EnsureNoCreditHold(DateOnly? overdueSince, DateTime nowUtc)
    {
        if (overdueSince is not null && !CreditHoldLifted(nowUtc))
            throw new ConflictException(CreditHoldCode,
                "New credit orders are on hold because an invoice is overdue. Pay the overdue invoices, or pay this order another way.");
    }

    /// <summary>
    /// An Admin lifts the hold for 1 to <paramref name="maxDays"/> days from now (owner decision 2: at most 7), or ends a lift with 0.
    /// A new lift replaces the old one. Returns the trimmed reason (5-500), which goes to the audit row.
    /// </summary>
    public string LiftCreditHold(int days, int maxDays, string? reason, DateTime nowUtc)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (days < 0 || days > maxDays) throw new ValidationException("days", $"Lift the hold for 1 to {maxDays} days, or 0 to end a lift.");
        if (trimmed.Length is < 5 or > 500) throw new ValidationException("reason", "Give a reason of 5 to 500 characters.");
        CreditHoldOverrideUntilUtc = days == 0 ? null : nowUtc.AddDays(days);
        Touch();
        return trimmed;
    }

    /// <summary>The hold sweep notes whether the company is overdue beyond the hold days; true only when that changed.</summary>
    public bool NoteCreditHold(bool overdue, DateTime nowUtc)
    {
        if (overdue == (CreditHoldSinceUtc is not null)) return false;
        CreditHoldSinceUtc = overdue ? nowUtc : null;
        Touch();
        return true;
    }

    /// <summary>
    /// The only place the cached credit totals change (ADR-0010 §1): numbers the line <c>seq + 1</c>, works out both running
    /// totals and refuses one below zero (no release beyond what is used, no on-account money spent twice). Called through
    /// <see cref="CreditLedger"/>, which checks the order and the key first.
    /// </summary>
    internal CreditLedgerEntry PostCredit(CreditPosting posting, DateTime nowUtc)
    {
        CreditLedgerEntry.Validate(posting);
        var receivable = checked(CreditUsedMinor + posting.ReceivableDeltaMinor);
        var onAccount = checked(CreditOnAccountMinor + posting.OnAccountDeltaMinor);
        if (receivable < 0) throw new ConflictException("CREDIT_RELEASE_EXCEEDS_USED", "Cannot release more credit than is used.");
        if (onAccount < 0) throw new ConflictException("CREDIT_ON_ACCOUNT_EXCEEDED", "Cannot use more on-account money than the company has.");

        var entry = new CreditLedgerEntry(Id, CreditLedgerSeq + 1, posting, receivable, onAccount, nowUtc);
        CreditUsedMinor = receivable;
        CreditOnAccountMinor = onAccount;
        CreditLedgerSeq = entry.Seq;
        Touch();
        return entry;
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;

    private static bool IsDigits(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9');
}
