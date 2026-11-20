using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Domain.Catalog;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Catalog;

public sealed class AdminVariantLookup(MechwareDbContext db) : IAdminVariantLookup
{
    public async Task<PagedResult<AdminVariantDto>> SearchAsync(string? sku, string? q, int page, int pageSize, bool includeCost, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, IAdminVariantLookup.MaxPageSize);

        var query = from v in db.ProductVariants.AsNoTracking()
                    join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                    where p.DeletedAtUtc == null
                    join b in db.Brands.AsNoTracking() on p.BrandId equals b.Id into brands
                    from b in brands.DefaultIfEmpty()
                    select new { v, p, Brand = b == null ? null : b.Name };

        if (sku?.Trim() is { Length: > 0 } exact)
        {
            var lower = exact.ToLowerInvariant();
            query = query.Where(x => x.v.Sku.ToLower() == lower);
        }

        if (q?.Trim() is { Length: > 0 } term)
        {
            var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var key = PartNumbers.Normalise(term);
            var useKey = key.Length >= 2;
            query = query.Where(x => EF.Functions.ILike(x.v.Sku, like, "\\") || EF.Functions.ILike(x.p.TitleEn, like, "\\")
                || (x.v.ManufacturerPartNumber != null && EF.Functions.ILike(x.v.ManufacturerPartNumber, like, "\\"))
                || (useKey && x.v.PartNumberKey == key));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.v.Sku).ThenBy(x => x.v.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.v.Id,
                ProductId = x.p.Id,
                x.v.Sku,
                x.v.ManufacturerPartNumber,
                Title = x.p.TitleEn,
                x.Brand,
                ProductStatus = x.p.Status,
                VariantStatus = x.v.Status,
                x.v.PriceMinor,
                x.p.SellerId,
                x.v.MinOrderQty,
                x.v.PackSize,
                x.v.CostPriceMinor
            }).ToListAsync(ct);

        var items = rows.Select(r => new AdminVariantDto(
            r.Id, r.ProductId, r.Sku, r.ManufacturerPartNumber, r.Title, r.Brand, r.ProductStatus.ToString(),
            r.VariantStatus == VariantStatus.Active, r.PriceMinor, r.SellerId, r.MinOrderQty, r.PackSize, includeCost ? r.CostPriceMinor : null)).ToList();
        return new PagedResult<AdminVariantDto>(items, page, pageSize, total);
    }
}
