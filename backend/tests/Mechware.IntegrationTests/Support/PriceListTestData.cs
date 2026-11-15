using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;

namespace Mechware.IntegrationTests.Support;

/// <summary>
/// Price lists made through the real aggregate and saved through the real database (four eyes, CHECKs, immutability
/// triggers), for tests of the order seam (D2.5). Every author, submitter and approver is a different random user.
/// </summary>
public static class PriceListTestData
{
    public sealed record Entry(Guid VariantId, int MinQty, long UnitPriceMinor);

    public static DateTime WholeSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    public static async Task<PriceList> DraftAsync(MechwareDbContext db, PriceListScope scope, Guid? companyId,
        DateTime validFrom, DateTime validTo, DateTime at, params Entry[] entries)
    {
        var author = Guid.NewGuid();
        var contractRef = companyId is null ? null : "CN-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "Order test list", scope, companyId, contractRef, validFrom, validTo, author, at);
        foreach (var e in entries) list.AddEntry(Guid.NewGuid(), e.VariantId, e.MinQty, e.UnitPriceMinor, null, author, at);
        db.PriceLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    public static async Task<PriceList> PendingAsync(MechwareDbContext db, PriceListScope scope, Guid? companyId,
        DateTime validFrom, DateTime validTo, DateTime at, params Entry[] entries)
    {
        var list = await DraftAsync(db, scope, companyId, validFrom, validTo, at, entries);
        list.Submit(Guid.NewGuid(), Floors(list), false, at);
        await db.SaveChangesAsync();
        return list;
    }

    /// <summary>Approved at <paramref name="at"/>, so it starts at <c>max(validFrom, at + 60 s)</c>.</summary>
    public static async Task<PriceList> ApprovedAsync(MechwareDbContext db, PriceListScope scope, Guid? companyId,
        DateTime validFrom, DateTime validTo, DateTime at, params Entry[] entries)
    {
        var list = await PendingAsync(db, scope, companyId, validFrom, validTo, at, entries);
        list.Approve(Guid.NewGuid(), false, list.ContentHash!, [], Floors(list), false, at);
        await db.SaveChangesAsync();
        return list;
    }

    /// <summary>
    /// Version + 1 of an approved list with some entries re-priced (and, when given, a new end of its window), submitted and
    /// approved at <paramref name="at"/>.
    /// </summary>
    public static async Task<PriceList> ReviseAsync(MechwareDbContext db, Guid listId, DateTime at, Entry[] newPrices, DateTime? validTo = null)
    {
        var current = await LoadAsync(db, listId);
        var reviser = Guid.NewGuid();
        var next = current.Revise(Guid.NewGuid(), reviser, at);
        if (validTo is { } to) next.ChangeHeader(next.Name, next.ContractRef, next.ValidFromUtc, to, reviser, at);
        foreach (var p in newPrices)
            next.ChangeEntry(next.Entries.Single(e => e.VariantId == p.VariantId && e.MinQty == p.MinQty).Id, p.UnitPriceMinor, null, reviser, at);
        db.PriceLists.Add(next);
        await db.SaveChangesAsync();
        next.Submit(Guid.NewGuid(), Floors(next), false, at);
        await db.SaveChangesAsync();
        next.Approve(Guid.NewGuid(), false, next.ContentHash!, [], Floors(next), false, at);
        await db.SaveChangesAsync();
        return next;
    }

    public static async Task WithdrawAsync(MechwareDbContext db, Guid listId, DateTime at)
    {
        (await LoadAsync(db, listId)).Withdraw(Guid.NewGuid(), "Wrong prices approved", at);
        await db.SaveChangesAsync();
    }

    public static Task<PriceList> LoadAsync(MechwareDbContext db, Guid id) =>
        db.PriceLists.Include(p => p.Entries).Include(p => p.Contributors).SingleAsync(p => p.Id == id);

    /// <summary>Cost 1 poisha and the 10 % global floor: every test price is far above the floor.</summary>
    private static MarginFloorEvaluation Floors(PriceList list) => MarginFloorPolicy.Evaluate(list,
        list.Entries.Select(e => e.VariantId).Distinct().Select(v => new VariantCostFacts(v, 1, null, [], 0m)).ToList(),
        [new MarginFloorRow(MarginFloorScope.Global, null, null, 10m)]);
}
