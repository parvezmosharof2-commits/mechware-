using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>
/// A stored margin floor row (table <c>margin_floors</c>, ADR-0008 §4). The migration seeds the one global row
/// (<see cref="MarginFloorPolicy.DefaultGlobalMinMarkupPercent"/>); changing or adding rows is D2.9. The policy works on
/// <see cref="ToRow"/>, so the same shape and markup rules apply here and in the database CHECKs.
/// </summary>
public sealed class MarginFloor : Entity<Guid>
{
    public MarginFloorScope Scope { get; private set; }
    public Guid? BrandId { get; private set; }
    public Guid? CategoryId { get; private set; }
    public decimal MinMarkupPercent { get; private set; }

    /// <summary>Last staff user who set the value; null for the row the migration seeded.</summary>
    public Guid? UpdatedBy { get; private set; }

    private MarginFloor() { }

    public static MarginFloor Create(Guid id, MarginFloorScope scope, Guid? brandId, Guid? categoryId, decimal minMarkupPercent, Guid createdBy, DateTime nowUtc)
    {
        if (id == Guid.Empty) throw new ValidationException("id", "A margin floor needs an id.");
        if (createdBy == Guid.Empty) throw new ValidationException("updatedBy", "The author is required.");
        MarginFloorPolicy.Validate(new MarginFloorRow(scope, brandId, categoryId, minMarkupPercent));
        return new MarginFloor
        {
            Id = id,
            Scope = scope,
            BrandId = brandId,
            CategoryId = categoryId,
            MinMarkupPercent = minMarkupPercent,
            UpdatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    public MarginFloorRow ToRow() => new(Scope, BrandId, CategoryId, MinMarkupPercent);
}
