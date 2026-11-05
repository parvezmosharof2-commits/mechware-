namespace ShopVerse.Domain.Companies;

/// <summary>
/// <see cref="Rejected"/>: MECHWARE refused the registration (wrong or someone else's BIN, failed documents). Final; its BIN is
/// free for the real owner to register (threat model v1, H2).
/// </summary>
public enum CompanyStatus { Pending = 1, Active = 2, Suspended = 3, Rejected = 4 }

public enum CreditTerms { None = 0, Net15 = 15, Net30 = 30 }

/// <summary>Company-scoped roles (brief §9). A member may hold several, e.g. Buyer | Approver.</summary>
[Flags]
public enum CompanyRoles
{
    None = 0,
    Requester = 1,
    Buyer = 2,
    Approver = 4,
    Admin = 8
}

public static class CompanyRolesExtensions
{
    private const CompanyRoles All = CompanyRoles.Requester | CompanyRoles.Buyer | CompanyRoles.Approver | CompanyRoles.Admin;

    public static bool IsValid(this CompanyRoles roles) => roles != CompanyRoles.None && (roles & ~All) == 0;
}
