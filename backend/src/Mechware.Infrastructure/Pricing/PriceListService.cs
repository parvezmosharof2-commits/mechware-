using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Pricing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Catalog;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Pricing;

/// <summary>
/// Staff price-list administration, part 1 (D2.7, ADR-0008 §3, §11). Every rule lives in the <see cref="PriceList"/> aggregate;
/// this class loads it, applies the call as the authenticated user (the only source of author, editor and submitter),
/// evaluates the margin floors from the database, and saves. Cost and floor values are read and returned only when the
/// caller holds <c>pricing.view_cost</c>; no error text carries an amount for anyone else.
/// </summary>
public sealed class PriceListService(MechwareDbContext db, ICurrentUserService user, PricingOptions options, TimeProvider clock) : IPriceListService
{
    private const int MaxPageSize = 50;
    public const int MaxEntriesPerRequest = 500;

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Sign in to manage price lists.");
    private bool CanSeeCost => user.HasPermission(Permissions.PricingViewCost);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<PriceListPageDto> ListAsync(PriceListQuery query, CancellationToken ct = default)
    {
        var q = db.PriceLists.AsNoTracking();
        if (Parse<PriceListStatus>(query.Status, "status") is { } status) q = q.Where(p => p.Status == status);
        if (Parse<PriceListScope>(query.Scope, "scope") is { } scope) q = q.Where(p => p.Scope == scope);
        if (query.CompanyId is { } companyId) q = q.Where(p => p.CompanyId == companyId);
        if (query.Q?.Trim() is { Length: > 0 } term)
        {
            var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(p => EF.Functions.ILike(p.Name, like) || (p.ContractRef != null && EF.Functions.ILike(p.ContractRef, like)));
        }

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.Id).Skip((page - 1) * size).Take(size)
            .Select(p => new
            {
                p.Id,
                p.SeriesId,
                p.Version,
                p.Name,
                p.Scope,
                p.CompanyId,
                p.ContractRef,
                p.Status,
                p.ValidFromUtc,
                p.ValidToUtc,
                p.CreatedBy,
                p.CreatedAtUtc,
                p.SubmittedBy,
                p.SubmittedAtUtc,
                p.ApprovedAtUtc,
                p.EffectiveFromUtc,
                Count = p.Entries.Count,
            }).ToListAsync(ct);
        var later = await StartedVersionsAsync(rows.Select(r => r.SeriesId).Distinct().ToList(), ct);
        var now = Now;
        var items = rows.Select(r => new PriceListSummaryDto
        {
            Id = r.Id,
            SeriesId = r.SeriesId,
            Version = r.Version,
            Name = r.Name,
            Scope = r.Scope.ToString(),
            CompanyId = r.CompanyId,
            ContractRef = r.ContractRef,
            Status = r.Status.ToString(),
            ValidFromUtc = r.ValidFromUtc,
            ValidToUtc = r.ValidToUtc,
            DisplayState = DisplayState(r.Status, r.ValidToUtc, r.EffectiveFromUtc, later.Any(l => l.SeriesId == r.SeriesId && l.Version > r.Version), now),
            EntryCount = r.Count,
            CreatedBy = r.CreatedBy,
            CreatedAtUtc = r.CreatedAtUtc,
            SubmittedBy = r.SubmittedBy,
            SubmittedAtUtc = r.SubmittedAtUtc,
        }).ToList();
        return new PriceListPageDto(items, page, size, total);
    }

    public async Task<PriceListDetailDto> GetAsync(Guid id, CancellationToken ct = default) =>
        await DetailAsync(await LoadAsync(id, tracked: false, ct), ct);

    public async Task<PriceListDetailDto> CreateAsync(CreatePriceListRequest request, CancellationToken ct = default)
    {
        var scope = Parse<PriceListScope>(request.Scope, "scope") ?? throw new ValidationException("scope", "Choose General or Company.");
        if (scope == PriceListScope.Group) throw new ValidationException("scope", "Group lists are not available yet.");
        if (scope == PriceListScope.Company
            && (request.CompanyId is not { } cid || !await db.Companies.AnyAsync(c => c.Id == cid && c.Status == CompanyStatus.Active, ct)))
            throw new ValidationException("companyId", "Choose an active company.");

        var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), request.Name ?? string.Empty, scope, request.CompanyId, request.ContractRef,
            request.ValidFromUtc, request.ValidToUtc, Actor, Now);
        db.PriceLists.Add(list);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(list, ct);
    }

    public async Task<PriceListDetailDto> UpdateHeaderAsync(Guid id, UpdatePriceListHeaderRequest request, CancellationToken ct = default)
    {
        return await LockedAsync(id, async list =>
        {
            list.ChangeHeader(request.Name ?? string.Empty, request.ContractRef, request.ValidFromUtc, request.ValidToUtc, Actor, Now);
            await db.SaveChangesAsync(ct);
            return await DetailAsync(list, ct);
        }, ct);
    }

    public async Task<PriceListDetailDto> UpsertEntriesAsync(Guid id, UpsertPriceListEntriesRequest request, CancellationToken ct = default)
    {
        var items = request.Entries ?? throw new ValidationException("entries", "Send the entries to save.");
        if (items.Count is 0 or > MaxEntriesPerRequest)
            throw new ValidationException("entries", $"Send 1 to {MaxEntriesPerRequest} entries per request.");
        if (items.GroupBy(i => (i.VariantId, i.MinQty)).Any(g => g.Count() > 1))
            throw new ValidationException("entries", "Each variant and minimum quantity may appear once per request.");

        return await LockedAsync(id, async list =>
        {
            var (editor, now) = (Actor, Now);
            foreach (var item in items)
            {
                var existing = list.Entries.FirstOrDefault(e => e.VariantId == item.VariantId && e.MinQty == item.MinQty);
                if (existing is not null) list.ChangeEntry(existing.Id, item.UnitPriceMinor, item.BelowFloorReason, editor, now);
                else db.Add(list.AddEntry(Guid.NewGuid(), item.VariantId, item.MinQty, item.UnitPriceMinor, item.BelowFloorReason, editor, now));
            }

            // After the domain accepted the edit (so a non-Draft list answers 409 first); nothing has been saved yet.
            var ids = items.Select(i => i.VariantId).Distinct().ToList();
            var usable = await db.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.Id) && v.Status == VariantStatus.Active)
                .Join(db.Products.AsNoTracking().Where(p => p.DeletedAtUtc == null), v => v.ProductId, p => p.Id, (v, _) => v.Id).ToListAsync(ct);
            var unknown = ids.Except(usable).ToList();
            if (unknown.Count > 0)
                throw new ValidationException(unknown.Select(v => new ValidationError($"entries.{v}.variantId", "This variant does not exist or is not active.")));

            await db.SaveChangesAsync(ct);
            return await DetailAsync(list, ct);
        }, ct);
    }

    public async Task<PriceListDetailDto> RemoveEntryAsync(Guid id, Guid entryId, CancellationToken ct = default)
    {
        return await LockedAsync(id, async list =>
        {
            list.RemoveEntry(entryId, Actor, Now);
            await db.SaveChangesAsync(ct);
            return await DetailAsync(list, ct);
        }, ct);
    }

    public async Task<PriceListDetailDto> SubmitAsync(Guid id, CancellationToken ct = default)
    {
        return await LockedAsync(id, async list =>
        {
            if (list.Status != PriceListStatus.Draft)
                throw new ConflictException("PRICE_LIST_NOT_EDITABLE", $"A {list.Status} price list cannot be submitted.");
            var floors = await EvaluateAsync(list, ct);
            // Name the entries (ids only; amounts only for a caller who may see cost) before the domain's own, single-message check.
            var problems = new List<ValidationError>();
            foreach (var e in floors.Entries)
            {
                var reason = list.Entries.Single(x => x.Id == e.EntryId).BelowFloorReason;
                if (e.BelowCost && !options.AllowBelowCost)
                    problems.Add(new($"entries.{e.EntryId}.unitPriceMinor",
                        CanSeeCost ? $"The price {e.NetUnitPriceMinor} is below cost {e.CostMinor}." : "This entry is priced below cost."));
                else if (e.BelowFloor && reason is null)
                    problems.Add(new($"entries.{e.EntryId}.belowFloorReason",
                        CanSeeCost ? $"The price {e.NetUnitPriceMinor} is below the margin floor {e.FloorMinor?.ToString() ?? "(cost unknown)"}; write a reason."
                            : "This entry is below the margin floor; write a reason."));
            }

            if (problems.Count > 0) throw new ValidationException(problems);

            var (actor, now) = (Actor, Now);
            list.Submit(actor, floors, options.AllowBelowCost, now);
            db.AuditLogs.Add(Audit("pricelist.submitted", list.Id, JsonSerializer.Serialize(new { list.Version, list.ContentHash, EntryCount = list.Entries.Count }), actor, now));
            await db.SaveChangesAsync(ct);
            return await DetailAsync(list, ct);
        }, ct);
    }

    public async Task<PriceListDetailDto> ReviseAsync(Guid id, CancellationToken ct = default)
    {
        var source = await LoadAsync(id, tracked: false, ct);
        var (actor, now) = (Actor, Now);
        var next = source.Revise(Guid.NewGuid(), actor, now); // refuses a list that is not Approved or Withdrawn
        var versions = await db.PriceLists.AsNoTracking().Where(p => p.SeriesId == source.SeriesId).Select(p => new { p.Version, p.Status }).ToListAsync(ct);
        if (versions.Any(v => v.Version > source.Version))
            throw new ConflictException("PRICE_LIST_NOT_LATEST", "Only the latest version of a price list can be revised.");
        if (versions.Any(v => v.Status is PriceListStatus.Draft or PriceListStatus.PendingApproval))
            throw new ConflictException("PRICE_LIST_OPEN_VERSION_EXISTS", "This price list already has a version in progress.");

        db.PriceLists.Add(next);
        db.AuditLogs.Add(Audit("pricelist.revised", next.Id, JsonSerializer.Serialize(new { RevisedFrom = source.Id, next.Version }), actor, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("PRICE_LIST_OPEN_VERSION_EXISTS", "This price list already has a version in progress.");
        }

        return await DetailAsync(next, ct);
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // ExecuteDelete: the database cascades to entries and contributors; EF's own cascade would delete contributors first and the trigger refuses that.
        var deleted = await db.PriceLists.Where(p => p.Id == id && p.Status == PriceListStatus.Draft).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            if (!await db.PriceLists.AnyAsync(p => p.Id == id, ct)) throw new KeyNotFoundException("Price list not found.");
            throw new ConflictException("PRICE_LIST_NOT_DELETABLE", "Only a draft price list can be deleted.");
        }

        db.AuditLogs.Add(Audit("pricelist.deleted", id, null, actor, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs an edit on the list while holding its row lock (<c>FOR UPDATE</c>) in one transaction. The entries trigger reads the
    /// parent <c>FOR SHARE</c>, so two unlocked edits of one draft can deadlock; locked edits queue up instead and each sees the
    /// previous one. The xmin concurrency token still refuses a write that bypasses this path.
    /// </summary>
    private async Task<T> LockedAsync<T>(Guid id, Func<PriceList, Task<T>> edit, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var found = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM price_lists WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        if (found.Count == 0) throw new KeyNotFoundException("Price list not found.");
        var result = await edit(await LoadAsync(id, tracked: true, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<PriceList> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var q = tracked ? db.PriceLists : db.PriceLists.AsNoTracking();
        return await q.Include(p => p.Entries).Include(p => p.Contributors).AsSplitQuery().SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException("Price list not found.");
    }

    private AuditLog Audit(string action, Guid listId, string? newValues, Guid actor, DateTime now) =>
        new(Guid.NewGuid(), actor, user.IpAddress, action, "price_lists", listId.ToString(), null, newValues, user.CorrelationId, now);

    private static T? Parse<T>(string? raw, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw, out _) || !Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var value) || !Enum.IsDefined(value))
            throw new ValidationException(field, $"Unknown {field}.");
        return value;
    }

    private async Task<List<(Guid SeriesId, int Version)>> StartedVersionsAsync(IReadOnlyCollection<Guid> seriesIds, CancellationToken ct)
    {
        var now = Now;
        var rows = await db.PriceLists.AsNoTracking()
            .Where(p => seriesIds.Contains(p.SeriesId) && (p.Status == PriceListStatus.Approved || p.Status == PriceListStatus.Withdrawn))
            .Select(p => new { p.SeriesId, p.Version, p.Status, p.EffectiveFromUtc, p.WithdrawnAtUtc }).ToListAsync(ct);
        // A later version supersedes once it started: approved and past its start, or withdrawn at/after its start (ADR-0008 §5).
        return rows.Where(r => r.EffectiveFromUtc is { } s && s <= now && (r.Status == PriceListStatus.Approved || r.WithdrawnAtUtc >= s))
            .Select(r => (r.SeriesId, r.Version)).ToList();
    }

    private static string DisplayState(PriceListStatus status, DateTime validTo, DateTime? effectiveFrom, bool laterStarted, DateTime now) => status switch
    {
        PriceListStatus.Draft => "Draft",
        PriceListStatus.PendingApproval => "PendingApproval",
        PriceListStatus.Withdrawn => "Withdrawn",
        _ when now >= validTo => "Expired",
        _ when laterStarted => "Superseded",
        _ when effectiveFrom is { } s && now < s => "Scheduled",
        _ => "Active",
    };

    /// <summary>Floors for this exact content, with the cost, brand and category ancestry of this instant.</summary>
    private async Task<MarginFloorEvaluation> EvaluateAsync(PriceList list, CancellationToken ct)
    {
        var ids = list.Entries.Select(e => e.VariantId).Distinct().ToList();
        var variants = await (from v in db.ProductVariants.AsNoTracking()
                              where ids.Contains(v.Id)
                              join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                              join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                              select new { v.Id, v.CostPriceMinor, p.BrandId, p.CategoryId, c.VatRate }).ToListAsync(ct);
        var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        var facts = variants.Select(v =>
        {
            var path = new List<Guid>();
            for (Guid? c = v.CategoryId; c is { } cur && path.Count < 32 && !path.Contains(cur); c = parents.GetValueOrDefault(cur)) path.Add(cur);
            return new VariantCostFacts(v.Id, v.CostPriceMinor, v.BrandId, path, v.VatRate);
        }).ToList();
        var floors = (await db.MarginFloors.AsNoTracking().ToListAsync(ct)).Select(f => f.ToRow()).ToList();
        return MarginFloorPolicy.Evaluate(list, facts, floors);
    }

    private async Task<PriceListDetailDto> DetailAsync(PriceList list, CancellationToken ct)
    {
        var variantIds = list.Entries.Select(e => e.VariantId).Distinct().ToList();
        var labels = await (from v in db.ProductVariants.AsNoTracking()
                            where variantIds.Contains(v.Id)
                            join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                            select new { v.Id, v.Sku, p.TitleEn }).ToDictionaryAsync(x => x.Id, ct);

        // Cost and floor: never read for a caller without view_cost. Open lists use the cost of this moment, decided ones what the approval stored.
        IReadOnlyDictionary<Guid, EntryFloorEvaluation>? live = null;
        if (CanSeeCost && list.Status is PriceListStatus.Draft or PriceListStatus.PendingApproval)
            live = (await EvaluateAsync(list, ct)).Entries.ToDictionary(e => e.EntryId);

        PriceListEntryDto Entry(PriceListEntry e)
        {
            labels.TryGetValue(e.VariantId, out var label);
            var dto = new PriceListEntryDto
            {
                Id = e.Id,
                VariantId = e.VariantId,
                Sku = label?.Sku ?? string.Empty,
                Title = label?.TitleEn ?? string.Empty,
                MinQty = e.MinQty,
                UnitPriceMinor = e.UnitPriceMinor,
                BelowFloorReason = e.BelowFloorReason,
            };
            if (!CanSeeCost) return dto;
            if (live is not null && live.TryGetValue(e.Id, out var f))
                return dto with { CostMinor = f.CostMinor, FloorMinor = f.FloorMinor, BelowFloor = f.BelowFloor, BelowCost = f.BelowCost, CostUnknown = f.CostUnknown };
            return dto with { CostMinor = e.CostMinorAtApproval, FloorMinor = e.FloorMinorAtApproval, BelowFloor = e.BelowFloorAtApproval };
        }

        var later = await StartedVersionsAsync([list.SeriesId], ct);
        return new PriceListDetailDto
        {
            Id = list.Id,
            SeriesId = list.SeriesId,
            Version = list.Version,
            Name = list.Name,
            Scope = list.Scope.ToString(),
            CompanyId = list.CompanyId,
            ContractRef = list.ContractRef,
            Status = list.Status.ToString(),
            ValidFromUtc = list.ValidFromUtc,
            ValidToUtc = list.ValidToUtc,
            DisplayState = DisplayState(list.Status, list.ValidToUtc, list.EffectiveFromUtc, later.Any(l => l.Version > list.Version), Now),
            EntryCount = list.Entries.Count,
            CreatedBy = list.CreatedBy,
            CreatedAtUtc = list.CreatedAtUtc,
            SubmittedBy = list.SubmittedBy,
            SubmittedAtUtc = list.SubmittedAtUtc,
            ContentHash = list.ContentHash,
            EffectiveFromUtc = list.EffectiveFromUtc,
            ApprovedBy = list.ApprovedBy,
            ApprovedAtUtc = list.ApprovedAtUtc,
            RejectionReason = list.RejectionReason,
            WithdrawReason = list.WithdrawReason,
            Contributors = list.Contributors.Select(c => c.UserId).Order().ToList(),
            Entries = list.Entries.OrderBy(e => labels.GetValueOrDefault(e.VariantId)?.Sku, StringComparer.Ordinal).ThenBy(e => e.MinQty).Select(Entry).ToList(),
        };
    }
}
