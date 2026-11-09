using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Common.ValueObjects;

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
    public long CreditUsedMinor { get; private set; }
    public Guid? DefaultPriceListId { get; private set; }
    public Guid? AccountManagerId { get; private set; }

    /// <summary>Why MECHWARE refused the registration; shown to the registrant.</summary>
    public string? RejectionReason { get; private set; }

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
        if (!IsDigits(bin, 13)) errors.Add(new("bin", "BIN must be exactly 13 digits."));
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
    /// Brief §8.7: allowed only if <c>used + total ≤ limit</c>. The caller must hold a row lock (or rely on the
    /// xmin concurrency token) so two orders cannot both pass the check.
    /// </summary>
    public void ReserveCredit(Money amount)
    {
        if (amount.Currency != Currency) throw new CurrencyMismatchException(amount.Currency, Currency);
        if (!amount.IsPositive) throw new ValidationException("amount", "Amount must be positive.");
        if (!IsActive) throw new ConflictException("COMPANY_NOT_ACTIVE", "The company account is not active.");
        if (CreditTerms == CreditTerms.None) throw new ConflictException("NO_CREDIT_TERMS", "The company has no credit terms.");
        if (amount.ToMinorUnits() > CreditLimitMinor - CreditUsedMinor)
            throw new ConflictException("CREDIT_LIMIT_EXCEEDED", "The order exceeds the available credit limit.");

        CreditUsedMinor += amount.ToMinorUnits();
        Touch();
    }

    public void ReleaseCredit(Money amount)
    {
        if (amount.Currency != Currency) throw new CurrencyMismatchException(amount.Currency, Currency);
        if (!amount.IsPositive) throw new ValidationException("amount", "Amount must be positive.");
        if (amount.ToMinorUnits() > CreditUsedMinor)
            throw new ConflictException("CREDIT_RELEASE_EXCEEDS_USED", "Cannot release more credit than is used.");

        CreditUsedMinor -= amount.ToMinorUnits();
        Touch();
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;

    private static bool IsDigits(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9');
}
