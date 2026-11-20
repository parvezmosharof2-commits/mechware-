namespace Mechware.Application.Quotes;

/// <summary>A delivery site of a company as the quote editor picks it (no contact name or phone).</summary>
public sealed record StaffSiteDto(Guid Id, string Name, string Line1, string? Line2, string Upazila, string District, string Division, string? PostalCode,
    bool IsDefaultDelivery);

/// <summary>A person who may be a quote's contact: an effective member with the Buyer or Admin role.</summary>
public sealed record StaffContactDto(Guid UserId, string FullName, string Email, IReadOnlyList<string> Roles);

/// <summary>The seller of a variant: a fixed-delivery quote carries one fee per seller.</summary>
public sealed record StaffVariantSellerDto(Guid VariantId, Guid SellerId, string SellerName);

/// <summary>Small staff lookups for the quote editor (D4.18). An unknown company is a 404.</summary>
public interface IStaffQuoteLookups
{
    public const int MaxVariants = 50;

    Task<IReadOnlyList<StaffSiteDto>> SitesAsync(Guid companyId, CancellationToken ct = default);

    Task<IReadOnlyList<StaffContactDto>> ContactsAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>Distinct sellers per given variant (1 to <see cref="MaxVariants"/> ids); unknown variants are left out.</summary>
    Task<IReadOnlyList<StaffVariantSellerDto>> SellersOfAsync(IReadOnlyList<Guid> variantIds, CancellationToken ct = default);
}
