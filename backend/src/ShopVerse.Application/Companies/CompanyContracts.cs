using ShopVerse.Domain.Companies;

namespace ShopVerse.Application.Companies;

public sealed record MoneyDto(long MinorUnits, string Currency);

public sealed record CompanyDto(
    Guid Id, string LegalName, string TradeLicenceNo, string Bin, string? Tin, string? Industry,
    string Status, string CreditTerms, MoneyDto CreditLimit, MoneyDto CreditUsed, MoneyDto CreditAvailable,
    DateTime CreatedAtUtc);

/// <summary>A company as seen by one of its members, with that member's own roles.</summary>
public sealed record MyCompanyDto(CompanyDto Company, CompanyRoles Roles);

public sealed record RegisterCompanyRequest(string LegalName, string TradeLicenceNo, string Bin, string? Tin, string? Industry);

public sealed record SiteRequest(
    string Name, string ContactName, string ContactPhone, string Line1, string? Line2,
    string Upazila, string District, string Division, string? PostalCode, bool IsDefaultDelivery);

public sealed record SiteDto(
    Guid Id, string Name, string ContactName, string ContactPhone, string Line1, string? Line2,
    string Upazila, string District, string Division, string? PostalCode, bool IsDefaultDelivery);

public sealed record CostCentreRequest(string Code, string Name, long? MonthlyBudgetMinor);

public sealed record CostCentreDto(Guid Id, string Code, string Name, long? MonthlyBudgetMinor);

public sealed record AddMemberRequest(Guid UserId, CompanyRoles Roles, long? SpendLimitMinor, IReadOnlyList<Guid>? CostCentreIds);

public sealed record UpdateMemberRequest(CompanyRoles Roles, long? SpendLimitMinor, IReadOnlyList<Guid>? CostCentreIds, bool IsActive);

public sealed record MemberDto(
    Guid Id, Guid UserId, CompanyRoles Roles, long? SpendLimitMinor, bool IsActive, IReadOnlyList<Guid> CostCentreIds);

public sealed record SetCreditRequest(CreditTerms Terms, long LimitMinorUnits);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// B2B company accounts (brief §3.1.3). Every member-facing method takes the acting user id and resolves
/// access from <c>company_members</c> in the database; a company the actor does not belong to is reported as
/// "not found" so ids cannot be probed (IDOR). The caller never supplies a company scope as a claim.
/// </summary>
public interface ICompanyService
{
    Task<CompanyDto> RegisterAsync(Guid actorUserId, RegisterCompanyRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MyCompanyDto>> ListMineAsync(Guid actorUserId, CancellationToken ct = default);
    Task<CompanyDto> GetAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);

    Task<IReadOnlyList<SiteDto>> ListSitesAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);
    Task<SiteDto> AddSiteAsync(Guid actorUserId, Guid companyId, SiteRequest request, CancellationToken ct = default);
    Task<SiteDto> UpdateSiteAsync(Guid actorUserId, Guid companyId, Guid siteId, SiteRequest request, CancellationToken ct = default);
    Task RemoveSiteAsync(Guid actorUserId, Guid companyId, Guid siteId, CancellationToken ct = default);

    Task<IReadOnlyList<CostCentreDto>> ListCostCentresAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);
    Task<CostCentreDto> AddCostCentreAsync(Guid actorUserId, Guid companyId, CostCentreRequest request, CancellationToken ct = default);
    Task<CostCentreDto> UpdateCostCentreAsync(Guid actorUserId, Guid companyId, Guid costCentreId, CostCentreRequest request, CancellationToken ct = default);
    Task RemoveCostCentreAsync(Guid actorUserId, Guid companyId, Guid costCentreId, CancellationToken ct = default);

    Task<IReadOnlyList<MemberDto>> ListMembersAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);
    Task<MemberDto> AddMemberAsync(Guid actorUserId, Guid companyId, AddMemberRequest request, CancellationToken ct = default);
    Task<MemberDto> UpdateMemberAsync(Guid actorUserId, Guid companyId, Guid memberId, UpdateMemberRequest request, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid actorUserId, Guid companyId, Guid memberId, CancellationToken ct = default);

    // MECHWARE staff. Authorised by permission at the API; not scoped to membership.
    Task<PagedResult<CompanyDto>> AdminListAsync(string? status, int page, int pageSize, CancellationToken ct = default);
    Task<CompanyDto> AdminGetAsync(Guid companyId, CancellationToken ct = default);
    Task<CompanyDto> AdminActivateAsync(Guid companyId, CancellationToken ct = default);
    Task<CompanyDto> AdminSuspendAsync(Guid companyId, CancellationToken ct = default);
    Task<CompanyDto> AdminSetCreditAsync(Guid companyId, SetCreditRequest request, CancellationToken ct = default);
}
