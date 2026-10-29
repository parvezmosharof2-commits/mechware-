using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Inventory;

public sealed class Warehouse : Entity<Guid>
{
    /// <summary>Null for the MECHWARE central hub.</summary>
    public Guid? SellerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string Division { get; private set; } = string.Empty;
    public string District { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    private Warehouse() { }

    public Warehouse(Guid id, Guid? sellerId, string name, string code, string division, string district, string address)
        : base(id)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("name", "Warehouse name is required.");
        if (string.IsNullOrWhiteSpace(code)) throw new ValidationException("code", "Warehouse code is required.");
        SellerId = sellerId;
        Name = name.Trim();
        Code = code.Trim().ToUpperInvariant();
        Division = division;
        District = district;
        Address = address;
    }
}
