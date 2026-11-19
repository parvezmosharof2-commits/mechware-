using Mechware.Domain.Companies;

namespace Mechware.Domain.Quotes;

/// <summary>
/// Configuration section <c>Quotes</c> (ADR-0011 §2, §5, §7). The defaults are the owner decisions of §15, decided 2026-11-17
/// under the owner's delegation; they are data and change without a release. The host validates them at start-up.
/// </summary>
public sealed record QuoteOptions
{
    public const string Section = "Quotes";

    /// <summary>The database CHECK on <c>validity_days</c>; options decide within it.</summary>
    public const int HardMaxValidityDays = 365;

    public int DefaultValidityDays { get; init; } = 15;
    public int MaxValidityDays { get; init; } = 90;

    /// <summary>Above this, a second person approves (flag F4, D4.3).</summary>
    public int ValidityWithoutApprovalDays { get; init; } = 30;

    /// <summary>Discount below the buyer's own resolver price allowed without approval, in basis points (500 = 5 %, flag F3).</summary>
    public int DiscountAuthorityBasisPoints { get; init; } = 500;

    public int MaxRfqsPerCompanyPerHour { get; init; } = 10;
    public int MaxOpenRfqsPerCompany { get; init; } = 20;

    /// <summary>Company roles that see every RFQ and quote of the company (§13); any other member sees their own.</summary>
    public CompanyRoles VisibleTo { get; init; } = CompanyRoles.Admin | CompanyRoles.Buyer;

    /// <summary>The KPI "first quote within 4 working hours" (§15 decision 12), on the <see cref="QuoteSla"/> clock; shown in the inbox only.</summary>
    public int FirstQuoteSlaWorkingHours { get; init; } = 4;

    /// <summary>Dhaka public holidays the SLA clock skips (owner item c: empty until the owner gives the list).</summary>
    public IReadOnlyList<DateOnly> SlaHolidays { get; init; } = [];

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        void Check(bool ok, string name, string rule) { if (!ok) p.Add($"{Section}:{name} {rule}."); }
        Check(MaxValidityDays is >= 1 and <= HardMaxValidityDays, nameof(MaxValidityDays), $"must be 1 to {HardMaxValidityDays}");
        Check(DefaultValidityDays >= 1 && DefaultValidityDays <= MaxValidityDays, nameof(DefaultValidityDays), $"must be 1 to {nameof(MaxValidityDays)}");
        Check(ValidityWithoutApprovalDays >= 1 && ValidityWithoutApprovalDays <= MaxValidityDays, nameof(ValidityWithoutApprovalDays),
            $"must be 1 to {nameof(MaxValidityDays)}");
        Check(DiscountAuthorityBasisPoints is >= 0 and < 10_000, nameof(DiscountAuthorityBasisPoints), "must be 0 to 9999");
        Check(MaxRfqsPerCompanyPerHour >= 1, nameof(MaxRfqsPerCompanyPerHour), "must be at least 1");
        Check(MaxOpenRfqsPerCompany >= 1, nameof(MaxOpenRfqsPerCompany), "must be at least 1");
        Check(VisibleTo != CompanyRoles.None && (VisibleTo & ~(CompanyRoles.Requester | CompanyRoles.Buyer | CompanyRoles.Approver | CompanyRoles.Admin)) == 0,
            nameof(VisibleTo), "must name at least one company role");
        Check(FirstQuoteSlaWorkingHours is >= 1 and <= 45, nameof(FirstQuoteSlaWorkingHours), "must be 1 to 45 (one working week)");
        return p;
    }

    /// <summary>This instance, or <see cref="InvalidOperationException"/> naming every bad value (the host does not start).</summary>
    public QuoteOptions Validated() =>
        Problems() is { Count: > 0 } p ? throw new InvalidOperationException("Invalid quote options: " + string.Join(" ", p)) : this;
}
