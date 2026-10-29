using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

/// <summary>
/// Represents a user's shipping or billing address in Bangladesh (Division/District/Upazila hierarchy).
/// </summary>
public sealed class Address : Entity<Guid>
{
    public Guid UserId { get; private set; }
    public string Label { get; private set; } = "Factory / Office";
    public string RecipientName { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string Line1 { get; private set; } = string.Empty;
    public string? Line2 { get; private set; }
    public string? Area { get; private set; }
    public string Upazila { get; private set; } = string.Empty;
    public string District { get; private set; } = string.Empty;
    public string Division { get; private set; } = string.Empty;
    public string? PostalCode { get; private set; }
    public string Country { get; private set; } = "BD";
    public bool IsDefaultShipping { get; private set; }
    public bool IsDefaultBilling { get; private set; }
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }

    private Address() { }

    public Address(
        Guid id,
        Guid userId,
        string label,
        string recipientName,
        string phone,
        string line1,
        string? line2,
        string? area,
        string upazila,
        string district,
        string division,
        string? postalCode,
        bool isDefaultShipping,
        bool isDefaultBilling,
        decimal? latitude = null,
        decimal? longitude = null) : base(id)
    {
        if (string.IsNullOrWhiteSpace(recipientName))
        {
            throw new ValidationException("recipientName", "Recipient name is required.");
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new ValidationException("phone", "Recipient phone number is required.");
        }

        if (string.IsNullOrWhiteSpace(line1))
        {
            throw new ValidationException("line1", "Street/Plot address line 1 is required.");
        }

        if (string.IsNullOrWhiteSpace(division) || string.IsNullOrWhiteSpace(district) || string.IsNullOrWhiteSpace(upazila))
        {
            throw new ValidationException("location", "Bangladesh Division, District, and Upazila are required.");
        }

        UserId = userId;
        Label = string.IsNullOrWhiteSpace(label) ? "Office" : label.Trim();
        RecipientName = recipientName.Trim();
        Phone = phone.Trim();
        Line1 = line1.Trim();
        Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim();
        Area = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        Upazila = upazila.Trim();
        District = district.Trim();
        Division = division.Trim();
        PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        Country = "BD";
        IsDefaultShipping = isDefaultShipping;
        IsDefaultBilling = isDefaultBilling;
        Latitude = latitude;
        Longitude = longitude;
    }

    public void Update(
        string label,
        string recipientName,
        string phone,
        string line1,
        string? line2,
        string? area,
        string upazila,
        string district,
        string division,
        string? postalCode,
        bool isDefaultShipping,
        bool isDefaultBilling,
        decimal? latitude,
        decimal? longitude,
        DateTime utcNow)
    {
        Label = string.IsNullOrWhiteSpace(label) ? Label : label.Trim();
        RecipientName = recipientName.Trim();
        Phone = phone.Trim();
        Line1 = line1.Trim();
        Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim();
        Area = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        Upazila = upazila.Trim();
        District = district.Trim();
        Division = division.Trim();
        PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        IsDefaultShipping = isDefaultShipping;
        IsDefaultBilling = isDefaultBilling;
        Latitude = latitude;
        Longitude = longitude;
        UpdatedAtUtc = utcNow;
    }

    public void SetDefaultFlags(bool isDefaultShipping, bool isDefaultBilling, DateTime utcNow)
    {
        IsDefaultShipping = isDefaultShipping;
        IsDefaultBilling = isDefaultBilling;
        UpdatedAtUtc = utcNow;
    }
}
