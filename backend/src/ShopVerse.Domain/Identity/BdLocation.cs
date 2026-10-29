using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

/// <summary>
/// Pre-seeded Bangladesh administrative location hierarchy (Division -> District -> Upazila).
/// </summary>
public sealed class BdLocation : Entity<Guid>
{
    public Guid? ParentId { get; private set; }
    public string Level { get; private set; } = string.Empty; // division, district, upazila
    public string NameEn { get; private set; } = string.Empty;
    public string NameBn { get; private set; } = string.Empty;
    public string ShippingZone { get; private set; } = "outside_dhaka"; // inside_dhaka, dhaka_suburb, outside_dhaka
    public string? PostalCodePrefix { get; private set; }

    private BdLocation() { }

    public BdLocation(
        Guid id,
        Guid? parentId,
        string level,
        string nameEn,
        string nameBn,
        string shippingZone,
        string? postalCodePrefix = null) : base(id)
    {
        ParentId = parentId;
        Level = level;
        NameEn = nameEn;
        NameBn = nameBn;
        ShippingZone = shippingZone;
        PostalCodePrefix = postalCodePrefix;
    }
}
