using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>
/// One price of a <see cref="PriceList"/> version (ADR-0008 §1): an absolute unit price in whole poisha for a variant from
/// <see cref="MinQty"/> units on. Several entries of one variant are its quantity breaks. Changed only through the list.
/// </summary>
public sealed class PriceListEntry : Entity<Guid>
{
    public Guid PriceListId { get; private set; }
    public Guid VariantId { get; private set; }
    public int MinQty { get; private set; }

    /// <summary>Net of VAT for Group/Company lists, on the catalogue basis (VAT-inclusive) for General lists (§2).</summary>
    public long UnitPriceMinor { get; private set; }

    /// <summary>Written by the author; required when the entry is below the margin floor (§4).</summary>
    public string? BelowFloorReason { get; private set; }

    /// <summary>Audit values written by the approval (§4); shown only with <c>pricing.view_cost</c>.</summary>
    public long? CostMinorAtApproval { get; private set; }

    public long? FloorMinorAtApproval { get; private set; }
    public bool? BelowFloorAtApproval { get; private set; }

    private PriceListEntry() { }

    internal PriceListEntry(Guid id, Guid priceListId, Guid variantId, int minQty, long unitPriceMinor, string? belowFloorReason) : base(id)
    {
        PriceListId = priceListId;
        VariantId = variantId;
        MinQty = minQty;
        UnitPriceMinor = unitPriceMinor;
        BelowFloorReason = belowFloorReason;
    }

    internal void Change(long unitPriceMinor, string? belowFloorReason)
    {
        UnitPriceMinor = unitPriceMinor;
        BelowFloorReason = belowFloorReason;
    }

    internal void RecordApproval(EntryFloorEvaluation evaluation)
    {
        CostMinorAtApproval = evaluation.CostMinor;
        FloorMinorAtApproval = evaluation.FloorMinor;
        BelowFloorAtApproval = evaluation.BelowFloor;
    }
}

/// <summary>A user who created or changed a price list version (§3); none of them may approve it.</summary>
public sealed class PriceListContributor
{
    public Guid PriceListId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime FirstContributedAtUtc { get; private set; }

    private PriceListContributor() { }

    internal PriceListContributor(Guid priceListId, Guid userId, DateTime atUtc)
    {
        PriceListId = priceListId;
        UserId = userId;
        FirstContributedAtUtc = atUtc;
    }
}
