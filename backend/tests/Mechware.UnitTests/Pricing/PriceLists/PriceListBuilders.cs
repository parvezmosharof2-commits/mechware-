using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.UnitTests.Pricing.PriceLists;

/// <summary>Shared builders for the D2.3 tests. Cost 500.00 with the 10 % global floor = floor 550.00 unless a test says otherwise.</summary>
internal static class PriceListBuilders
{
    public static readonly DateTime Now = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
    public static readonly Guid Author = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid Editor = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid Submitter = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    public static readonly Guid Approver = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    public static readonly Guid CompanyA = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    public static readonly Guid V1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid V2 = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public const long Cost = 50_000;

    public static readonly MarginFloorRow Global10 = new(MarginFloorScope.Global, null, null, MarginFloorPolicy.DefaultGlobalMinMarkupPercent);

    /// <summary>A Draft contract list of <see cref="CompanyA"/>, valid from tomorrow for 90 days, created by <see cref="Author"/>.</summary>
    public static PriceList Draft(PriceListScope scope = PriceListScope.Company, Guid? id = null, Guid? series = null,
        DateTime? from = null, DateTime? to = null) =>
        PriceList.Create(id ?? Guid.NewGuid(), series ?? Guid.NewGuid(), "Acme 2027", scope,
            scope == PriceListScope.Company ? CompanyA : null, scope == PriceListScope.Company ? "CTR-2027-01" : null,
            from ?? Now.AddDays(1), to ?? Now.AddDays(91), Author, Now);

    public static MarginFloorEvaluation Floors(PriceList list, long? cost = Cost, IReadOnlyCollection<MarginFloorRow>? rows = null) =>
        MarginFloorPolicy.Evaluate(list,
            list.Entries.Select(e => e.VariantId).Distinct().Select(v => new VariantCostFacts(v, cost, null, [], 0.05m)).ToList(),
            rows ?? [Global10]);

    /// <summary>A contract list with one entry well above the floor, submitted by <see cref="Submitter"/>.</summary>
    public static PriceList Pending(long price = 100_000, string? reason = null, long? cost = Cost)
    {
        var list = Draft();
        list.AddEntry(Guid.NewGuid(), V1, 1, price, reason, Author, Now);
        list.Submit(Submitter, Floors(list, cost), allowBelowCost: false, Now);
        return list;
    }

    public static void Approve(PriceList list, Guid? approver = null, long? cost = Cost, DateTime? at = null,
        IReadOnlyCollection<Guid>? acknowledged = null, bool mayApproveBelowFloor = false, bool allowBelowCost = false) =>
        list.Approve(approver ?? Approver, mayApproveBelowFloor, list.ContentHash!, acknowledged ?? [], Floors(list, cost),
            allowBelowCost, at ?? Now.AddMinutes(5));

    public static PriceList Approved()
    {
        var list = Pending();
        Approve(list);
        return list;
    }
}
