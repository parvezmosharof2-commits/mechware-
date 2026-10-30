using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Companies;

/// <summary>A branch / delivery site of a company.</summary>
public sealed class CompanySite : Entity<Guid>
{
    public Guid CompanyId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ContactName { get; private set; } = string.Empty;
    public string ContactPhone { get; private set; } = string.Empty;
    public string Line1 { get; private set; } = string.Empty;
    public string? Line2 { get; private set; }
    public string Upazila { get; private set; } = string.Empty;
    public string District { get; private set; } = string.Empty;
    public string Division { get; private set; } = string.Empty;
    public string? PostalCode { get; private set; }
    public bool IsDefaultDelivery { get; private set; }

    private CompanySite() { }

    public CompanySite(Guid id, Guid companyId, string name, string contactName, string contactPhone,
        string line1, string? line2, string upazila, string district, string division, string? postalCode,
        bool isDefaultDelivery) : base(id)
    {
        CompanyId = companyId;
        Update(name, contactName, contactPhone, line1, line2, upazila, district, division, postalCode);
        IsDefaultDelivery = isDefaultDelivery;
    }

    public void Update(string name, string contactName, string contactPhone, string line1, string? line2,
        string upazila, string district, string division, string? postalCode)
    {
        var errors = new List<ValidationError>();
        Require(errors, "name", name, 100);
        Require(errors, "contactName", contactName, 150);
        Require(errors, "contactPhone", contactPhone, 20);
        Require(errors, "line1", line1, 255);
        Require(errors, "upazila", upazila, 100);
        Require(errors, "district", district, 100);
        Require(errors, "division", division, 100);
        if (line2 is { Length: > 255 }) errors.Add(new("line2", "Line 2 must be at most 255 characters."));
        if (postalCode is { Length: > 20 }) errors.Add(new("postalCode", "Postal code must be at most 20 characters."));
        if (errors.Count > 0) throw new ValidationException(errors);

        Name = name.Trim();
        ContactName = contactName.Trim();
        ContactPhone = contactPhone.Trim();
        Line1 = line1.Trim();
        Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim();
        Upazila = upazila.Trim();
        District = district.Trim();
        Division = division.Trim();
        PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SetDefaultDelivery(bool value)
    {
        IsDefaultDelivery = value;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void Require(List<ValidationError> errors, string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(new(field, $"{field} is required."));
        else if (value.Trim().Length > max) errors.Add(new(field, $"{field} must be at most {max} characters."));
    }
}

/// <summary>Cost centre with an optional monthly budget (minor units, company currency).</summary>
public sealed class CostCentre : Entity<Guid>
{
    public Guid CompanyId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public long? MonthlyBudgetMinor { get; private set; }

    private CostCentre() { }

    public CostCentre(Guid id, Guid companyId, string code, string name, long? monthlyBudgetMinor) : base(id)
    {
        CompanyId = companyId;
        Update(code, name, monthlyBudgetMinor);
    }

    public void Update(string code, string name, long? monthlyBudgetMinor)
    {
        var errors = new List<ValidationError>();
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        name = (name ?? string.Empty).Trim();
        if (code.Length is < 1 or > 30) errors.Add(new("code", "Code must be 1-30 characters."));
        if (name.Length is < 1 or > 100) errors.Add(new("name", "Name must be 1-100 characters."));
        if (monthlyBudgetMinor is < 0) errors.Add(new("monthlyBudget", "Budget cannot be negative."));
        if (errors.Count > 0) throw new ValidationException(errors);

        Code = code;
        Name = name;
        MonthlyBudgetMinor = monthlyBudgetMinor;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

/// <summary>Membership of a user in a company. Authorisation inside a company is derived from these rows only.</summary>
public sealed class CompanyMember : Entity<Guid>
{
    public Guid CompanyId { get; private set; }
    /// <summary>Identity user id. No database FK: identity is not yet DB-backed (Phase 3 uses an in-memory store).</summary>
    public Guid UserId { get; private set; }
    public CompanyRoles Roles { get; private set; }
    public long? SpendLimitMinor { get; private set; }
    public bool IsActive { get; private set; } = true;
    public List<Guid> CostCentreIds { get; private set; } = [];

    private CompanyMember() { }

    public CompanyMember(Guid id, Guid companyId, Guid userId, CompanyRoles roles, long? spendLimitMinor,
        IEnumerable<Guid>? costCentreIds) : base(id)
    {
        CompanyId = companyId;
        UserId = userId;
        Update(roles, spendLimitMinor, costCentreIds, true);
    }

    public bool Has(CompanyRoles role) => IsActive && (Roles & role) != 0;

    public void Update(CompanyRoles roles, long? spendLimitMinor, IEnumerable<Guid>? costCentreIds, bool isActive)
    {
        if (!roles.IsValid()) throw new ValidationException("roles", "At least one valid company role is required.");
        if (spendLimitMinor is < 0) throw new ValidationException("spendLimit", "Spend limit cannot be negative.");

        Roles = roles;
        SpendLimitMinor = spendLimitMinor;
        CostCentreIds = costCentreIds?.Distinct().ToList() ?? [];
        IsActive = isActive;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
