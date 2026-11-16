using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Pricing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Pricing;

/// <summary>
/// Staff margin floors (D2.9, ADR-0008 §4, threat model PL11). The rules (scope shape, 0–1,000 % with at most two decimals,
/// precedence) live in <see cref="MarginFloor"/> and <see cref="MarginFloorPolicy"/>; this class checks that the brand and
/// category exist, serialises every floor change, and writes an explicit audit row with the old and the new markup.
/// A floor change never touches a price list: a pending list is evaluated again, with the floors of that moment, when it is
/// approved (D2.8), so lowering a floor approves nothing by itself.
/// </summary>
public sealed class MarginFloorService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : IMarginFloorService
{
    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Sign in to manage margin floors.");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<MarginFloorDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.MarginFloors.AsNoTracking().ToListAsync(ct);
        var dtos = await ToDtosAsync(rows, ct);
        return dtos.OrderBy(d => Enum.Parse<MarginFloorScope>(d.Scope)).ThenBy(d => d.BrandName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => string.Join("/", d.CategoryPath), StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id).ToList();
    }

    public async Task<EffectiveMarginFloorDto> EffectiveAsync(Guid? variantId, Guid? brandId, Guid? categoryId, CancellationToken ct = default)
    {
        if (variantId is not null && (brandId is not null || categoryId is not null))
            throw new ValidationException("variantId", "Send either a variant, or a brand and/or a category.");
        if (variantId is null && brandId is null && categoryId is null)
            throw new ValidationException("variantId", "Send a variant, or a brand and/or a category.");
        if (variantId is { } vid)
        {
            var facts = await (from v in db.ProductVariants.AsNoTracking()
                               where v.Id == vid
                               join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                               select new { p.BrandId, p.CategoryId }).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Variant not found.");
            (brandId, categoryId) = (facts.BrandId, facts.CategoryId);
        }
        else
        {
            if (brandId is { } b && !await db.Brands.AnyAsync(x => x.Id == b, ct)) throw new KeyNotFoundException("Brand not found.");
            if (categoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c, ct)) throw new KeyNotFoundException("Category not found.");
        }

        var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        var ancestry = new List<Guid>();
        for (var c = categoryId; c is { } cur && ancestry.Count < 32 && !ancestry.Contains(cur); c = parents.GetValueOrDefault(cur)) ancestry.Add(cur);

        var rows = await db.MarginFloors.AsNoTracking().ToListAsync(ct);
        // The cost does not matter for which row applies; the policy needs a fact record, never an amount from it here.
        var row = MarginFloorPolicy.FindFloor(new VariantCostFacts(variantId ?? Guid.Empty, null, brandId, ancestry, 0m), rows.Select(r => r.ToRow()).ToList());
        var applied = row is null ? null : rows.Single(r => r.Scope == row.Scope && r.BrandId == row.BrandId && r.CategoryId == row.CategoryId);
        return new EffectiveMarginFloorDto
        {
            VariantId = variantId,
            BrandId = brandId,
            CategoryId = categoryId,
            CategoryAncestry = ancestry,
            Floor = applied is null ? null : (await ToDtosAsync([applied], ct)).Single(),
            MinMarkupPercent = applied?.MinMarkupPercent,
            FailClosed = applied is null,
        };
    }

    public async Task<MarginFloorDto> UpsertAsync(UpsertMarginFloorRequest request, CancellationToken ct = default)
    {
        var scope = ParseScope(request.Scope);
        var markup = request.MinMarkupPercent ?? throw new ValidationException("minMarkupPercent", "Send the minimum markup in percent.");
        var (actor, now) = (Actor, Now);
        // The domain checks shape and range first (a third decimal or 1,000.01 % is refused, not rounded).
        var candidate = MarginFloor.Create(Guid.NewGuid(), scope, request.BrandId, request.CategoryId, markup, actor, now);
        if (request.BrandId is { } brand && !await db.Brands.AnyAsync(b => b.Id == brand, ct))
            throw new ValidationException("brandId", "This brand does not exist.");
        if (request.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, ct))
            throw new ValidationException("categoryId", "This category does not exist.");

        return await SerialisedAsync(async () =>
        {
            var existing = await db.MarginFloors.SingleOrDefaultAsync(f => f.Scope == scope && f.BrandId == request.BrandId && f.CategoryId == request.CategoryId, ct);
            var old = existing is null ? null : Snapshot(existing);
            var floor = existing ?? candidate;
            if (existing is null) db.MarginFloors.Add(candidate);
            else existing.ChangeMarkup(markup, actor, now);
            db.AuditLogs.Add(Audit("margin-floor.changed", floor.Id, old, Snapshot(floor), actor, now));
            await db.SaveChangesAsync(ct);
            return (await ToDtosAsync([floor], ct)).Single();
        }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await SerialisedAsync(async () =>
        {
            var floor = await db.MarginFloors.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new KeyNotFoundException("Margin floor not found.");
            // Without a global row the policy puts every entry below the floor (fail closed); the row may change, never go.
            if (floor.Scope == MarginFloorScope.Global)
                throw new ConflictException("GLOBAL_FLOOR_REQUIRED", "The global margin floor cannot be deleted; change its markup instead.");
            db.MarginFloors.Remove(floor);
            db.AuditLogs.Add(Audit("margin-floor.deleted", floor.Id, Snapshot(floor), null, actor, now));
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    /// <summary>
    /// One floor change at a time: <c>SHARE ROW EXCLUSIVE</c> conflicts with itself and with every write to the table, not with
    /// plain reads, so approvals keep reading floors while two upserts of one key queue up (no duplicate, no lost old value).
    /// </summary>
    private async Task<T> SerialisedAsync<T>(Func<Task<T>> change, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE margin_floors IN SHARE ROW EXCLUSIVE MODE", ct);
        var result = await change();
        await tx.CommitAsync(ct);
        return result;
    }

    private static MarginFloorScope ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || int.TryParse(raw, out _) || !Enum.TryParse<MarginFloorScope>(raw.Trim(), ignoreCase: true, out var scope) || !Enum.IsDefined(scope))
            throw new ValidationException("scope", "Choose Global, Brand, Category or BrandAndCategory.");
        return scope;
    }

    private static string Snapshot(MarginFloor f) =>
        JsonSerializer.Serialize(new { Scope = f.Scope.ToString(), f.BrandId, f.CategoryId, f.MinMarkupPercent });

    private AuditLog Audit(string action, Guid floorId, string? oldValues, string? newValues, Guid actor, DateTime now) =>
        new(Guid.NewGuid(), actor, user.IpAddress, action, "margin_floors", floorId.ToString(), oldValues, newValues, user.CorrelationId, now);

    private async Task<List<MarginFloorDto>> ToDtosAsync(IReadOnlyCollection<MarginFloor> rows, CancellationToken ct)
    {
        var brandIds = rows.Where(r => r.BrandId != null).Select(r => r.BrandId!.Value).Distinct().ToList();
        var brands = await db.Brands.AsNoTracking().Where(b => brandIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var categories = rows.Any(r => r.CategoryId != null)
            ? await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId, c.NameEn }).ToDictionaryAsync(c => c.Id, ct)
            : [];
        var userIds = rows.Where(r => r.UpdatedBy != null).Select(r => r.UpdatedBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        List<string> Path(Guid? id)
        {
            var path = new List<string>();
            var seen = new HashSet<Guid>();
            while (id is { } cur && seen.Add(cur) && categories.TryGetValue(cur, out var node))
            {
                path.Insert(0, node.NameEn);
                id = node.ParentId;
            }

            return path;
        }

        return rows.Select(r => new MarginFloorDto
        {
            Id = r.Id,
            Scope = r.Scope.ToString(),
            BrandId = r.BrandId,
            BrandName = r.BrandId is { } b ? brands.GetValueOrDefault(b) : null,
            CategoryId = r.CategoryId,
            CategoryName = r.CategoryId is { } c && categories.TryGetValue(c, out var node) ? node.NameEn : null,
            CategoryPath = Path(r.CategoryId),
            MinMarkupPercent = r.MinMarkupPercent,
            UpdatedBy = r.UpdatedBy,
            UpdatedByName = r.UpdatedBy is { } u ? names.GetValueOrDefault(u) : null,
            CreatedAtUtc = r.CreatedAtUtc,
            UpdatedAtUtc = r.UpdatedAtUtc,
        }).ToList();
    }
}
