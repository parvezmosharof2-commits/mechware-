using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Catalog;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Pricing;
using ShopVerse.Infrastructure.Caching;
using ShopVerse.Infrastructure.Persistence;
using StackExchange.Redis;

namespace ShopVerse.Infrastructure.Catalog;

/// <summary>
/// High-speed storefront reads. Static product content is cached in Redis (10 min, evicted by
/// <c>ProductUpdatedEvent</c> through the outbox relay); stock is cached separately for 10 s so
/// availability stays fresh without busting the heavy document. Prices are computed per request from
/// cached price inputs, so scheduled sale windows flip exactly on time even while cached.
/// </summary>
public sealed class StorefrontCatalogReader : IStorefrontCatalogReader
{
    public const int LowStockDisplayThreshold = 5;

    private static readonly Func<ShopVerseDbContext, string, Task<Product?>> ProductBySlug =
        EF.CompileAsyncQuery((ShopVerseDbContext db, string slug) =>
            db.Products.AsNoTracking().AsSplitQuery()
                .Include(p => p.Variants)
                .Include(p => p.Media)
                .FirstOrDefault(p => p.Slug == slug && p.DeletedAtUtc == null && p.Status == ProductStatus.Active));

    private readonly ShopVerseDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _clock;

    public StorefrontCatalogReader(ShopVerseDbContext db, IConnectionMultiplexer redis, TimeProvider clock)
    {
        _db = db;
        _redis = redis;
        _clock = clock;
    }

    #region Product by slug

    public async Task<SlugLookupResult<ProductDetailDto>> GetProductBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (!Slug.IsValid(slug)) return SlugLookupResult<ProductDetailDto>.NotFound();

        var redis = _redis.GetDatabase();
        var cached = await redis.StringGetAsync(StorefrontCacheKeys.Product(slug));
        CachedProduct? doc = cached.HasValue ? JsonSerializer.Deserialize<CachedProduct>((string)cached!, CacheJson.Options) : null;

        if (doc is null)
        {
            var moved = await redis.StringGetAsync(StorefrontCacheKeys.Redirect(slug));
            if (moved.HasValue) return SlugLookupResult<ProductDetailDto>.MovedTo(moved!);
            if (await redis.KeyExistsAsync(StorefrontCacheKeys.Miss(slug))) return SlugLookupResult<ProductDetailDto>.NotFound();

            doc = await LoadProductAsync(slug, ct);
            if (doc is null)
            {
                var redirect = await FindRedirectAsync(slug, ct);
                if (redirect is not null)
                {
                    await redis.StringSetAsync(StorefrontCacheKeys.Redirect(slug), redirect, StorefrontCacheKeys.ProductTtl);
                    return SlugLookupResult<ProductDetailDto>.MovedTo(redirect);
                }

                await redis.StringSetAsync(StorefrontCacheKeys.Miss(slug), "1", StorefrontCacheKeys.NegativeTtl);
                return SlugLookupResult<ProductDetailDto>.NotFound();
            }

            await redis.StringSetAsync(StorefrontCacheKeys.Product(slug),
                JsonSerializer.Serialize(doc, CacheJson.Options), Jitter(StorefrontCacheKeys.ProductTtl));
        }

        var stock = await GetStockAsync(doc.Id, doc.Variants.Select(v => v.Id).ToArray(), ct);
        return SlugLookupResult<ProductDetailDto>.Found(ToDto(doc, stock, _clock.GetUtcNow().UtcDateTime));
    }

    private async Task<CachedProduct?> LoadProductAsync(string slug, CancellationToken ct)
    {
        var product = await ProductBySlug(_db, slug);
        if (product is null) return null;

        var brand = product.BrandId is null ? null
            : await _db.Brands.AsNoTracking().Where(b => b.Id == product.BrandId)
                .Select(b => new BrandDto(b.Name, b.Slug, b.LogoUrl, b.IsVerified)).FirstOrDefaultAsync(ct);

        var seller = await _db.Sellers.AsNoTracking().Where(s => s.Id == product.SellerId)
            .Select(s => new SellerSummaryDto(s.StoreName, s.Slug, s.LogoUrl, s.RatingAvg, s.RatingCount)).FirstAsync(ct);

        var category = await _db.Categories.AsNoTracking().FirstAsync(c => c.Id == product.CategoryId, ct);
        var slugs = category.MaterializedPath.Split('.').Skip(1).ToArray();
        var chain = await _db.Categories.AsNoTracking().Where(c => slugs.Contains(c.Slug)).OrderBy(c => c.Depth)
            .Select(c => new BreadcrumbDto(c.NameEn, c.NameBn, c.Slug)).ToListAsync(ct);

        return new CachedProduct
        {
            Id = product.Id,
            Slug = product.Slug,
            TitleEn = product.TitleEn,
            TitleBn = product.TitleBn,
            ShortDescriptionEn = product.ShortDescriptionEn,
            ShortDescriptionBn = product.ShortDescriptionBn,
            DescriptionHtml = product.DescriptionHtml,
            Highlights = JsonSerializer.Deserialize<List<string>>(product.HighlightsJson, CacheJson.Options) ?? [],
            Specs = ParseSpecs(product.SpecsJson),
            Brand = brand,
            Seller = seller,
            Breadcrumbs = chain,
            Condition = SnakeCaseEnumConverterName(product.Condition),
            WarrantyMonths = product.WarrantyMonths,
            ReturnPolicyDays = product.ReturnPolicyDays,
            RatingAvg = product.RatingAvg,
            RatingCount = product.RatingCount,
            SoldCount = product.SoldCount,
            DatasheetUrl = product.DatasheetUrl,
            CadModelUrl = product.CadModelUrl,
            VatRate = category.VatRate,
            Media = product.Media.OrderBy(m => m.Position)
                .Select(m => new MediaDto(m.Url, m.AltText, m.Type == MediaType.Video ? "video" : "image", m.Width, m.Height, m.BlurDataUrl, m.VariantId))
                .ToList(),
            Variants = product.Variants.Where(v => v.Status == VariantStatus.Active)
                .OrderByDescending(v => v.IsDefault).ThenBy(v => v.PriceMinor).ThenBy(v => v.Sku, StringComparer.Ordinal)
                .Select(v => new CachedVariant
                {
                    Id = v.Id,
                    Sku = v.Sku,
                    Options = v.OptionValues.ToDictionary(o => o.Key, o => o.Value),
                    IsDefault = v.IsDefault,
                    Currency = v.Currency.ToIsoCode(),
                    PriceMinor = v.PriceMinor,
                    CompareAtMinor = v.CompareAtPriceMinor,
                    SalePriceMinor = v.SalePriceMinor,
                    SaleStartsAtUtc = v.SaleStartsAtUtc,
                    SaleEndsAtUtc = v.SaleEndsAtUtc
                }).ToList()
        };
    }

    private async Task<string?> FindRedirectAsync(string slug, CancellationToken ct)
    {
        var entityId = await _db.SlugHistories.AsNoTracking()
            .Where(h => h.EntityType == SlugEntityType.Product && h.OldSlug == slug)
            .Select(h => (Guid?)h.EntityId).FirstOrDefaultAsync(ct);
        if (entityId is null) return null;

        return await _db.Products.AsNoTracking()
            .Where(p => p.Id == entityId && p.DeletedAtUtc == null && p.Status == ProductStatus.Active)
            .Select(p => p.Slug).FirstOrDefaultAsync(ct);
    }

    private async Task<Dictionary<Guid, int>> GetStockAsync(Guid productId, Guid[] variantIds, CancellationToken ct)
    {
        var redis = _redis.GetDatabase();
        var key = StorefrontCacheKeys.Stock(productId);
        var cached = await redis.StringGetAsync(key);
        if (cached.HasValue)
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, int>>((string)cached!, CacheJson.Options) ?? [];
        }

        var rows = await _db.InventoryItems.AsNoTracking()
            .Where(i => variantIds.Contains(i.VariantId))
            .Join(_db.Warehouses.Where(w => w.IsActive), i => i.WarehouseId, w => w.Id, (i, _) => i)
            .GroupBy(i => i.VariantId)
            .Select(g => new { VariantId = g.Key, Available = g.Sum(i => i.OnHand - i.Reserved) })
            .ToListAsync(ct);

        var map = variantIds.ToDictionary(id => id, id => rows.FirstOrDefault(r => r.VariantId == id)?.Available ?? 0);
        await redis.StringSetAsync(key, JsonSerializer.Serialize(map, CacheJson.Options), StorefrontCacheKeys.StockTtl);
        return map;
    }

    private static ProductDetailDto ToDto(CachedProduct d, IReadOnlyDictionary<Guid, int> stock, DateTime nowUtc) => new(
        d.Id, d.Slug, d.TitleEn, d.TitleBn, d.ShortDescriptionEn, d.ShortDescriptionBn, d.DescriptionHtml, d.Highlights,
        d.Specs, d.Brand, d.Seller, d.Breadcrumbs, d.Condition, d.WarrantyMonths, d.ReturnPolicyDays, d.RatingAvg,
        d.RatingCount, d.SoldCount, d.DatasheetUrl, d.CadModelUrl, d.Media,
        d.Variants.Select(v => new VariantDto(
            v.Id, v.Sku, v.Options, v.IsDefault, PriceFor(v, d.VatRate, nowUtc),
            ToStock(stock.TryGetValue(v.Id, out var a) ? a : 0))).ToList());

    internal static StockDto ToStock(int available) => new(Math.Max(available, 0), available switch
    {
        <= 0 => "out_of_stock",
        <= LowStockDisplayThreshold => "low_stock",
        _ => "in_stock"
    });

    internal static VariantPriceDto PriceFor(CachedVariant v, decimal vatRate, DateTime nowUtc)
    {
        var currency = CurrencyExtensions.Parse(v.Currency);
        var input = new PricingInput(
            Money.FromMinorUnits(v.PriceMinor, currency),
            v.CompareAtMinor is { } c ? Money.FromMinorUnits(c, currency) : null,
            v.SalePriceMinor is { } s ? Money.FromMinorUnits(s, currency) : null,
            v.SaleStartsAtUtc, v.SaleEndsAtUtc, vatRate);
        var quote = PricingEngine.Quote(input, nowUtc, TaxMode.Inclusive);
        return new VariantPriceDto(ToDto(quote.Display), quote.StrikeThrough is { } st ? ToDto(st) : null,
            quote.DiscountPercent, quote.SaleActive, true, ToDto(quote.Tax));
    }

    internal static MoneyDto ToDto(Money m) =>
        new(m.Amount.ToString("F2", CultureInfo.InvariantCulture), m.ToMinorUnits(), m.Currency.ToIsoCode(), m.ToString());

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseSpecs(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, CacheJson.Options) ?? [];
        return parsed.ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.Value);
    }

    private static string SnakeCaseEnumConverterName(ProductCondition c) => c switch
    {
        ProductCondition.BrandNew => "brand_new",
        ProductCondition.Refurbished => "refurbished",
        _ => "used"
    };

    private static TimeSpan Jitter(TimeSpan ttl) => ttl + TimeSpan.FromSeconds(Random.Shared.Next(0, 30));

    #endregion

    #region Categories & listing

    public async Task<IReadOnlyList<CategoryNodeDto>> GetCategoryTreeAsync(CancellationToken ct = default)
    {
        var redis = _redis.GetDatabase();
        var cached = await redis.StringGetAsync(StorefrontCacheKeys.Categories);
        if (cached.HasValue)
            return JsonSerializer.Deserialize<List<CategoryNodeDto>>((string)cached!, CacheJson.Options) ?? [];

        var all = await _db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync(ct);
        var byParent = all.ToLookup(c => c.ParentId);
        CategoryNodeDto Build(Category c) => new(c.Id, c.NameEn, c.NameBn, c.Slug, c.MaterializedPath, c.Depth, c.ImageUrl,
            byParent[c.Id].Select(Build).ToList());
        var tree = byParent[null].Select(Build).ToList();

        await redis.StringSetAsync(StorefrontCacheKeys.Categories, JsonSerializer.Serialize(tree, CacheJson.Options), Jitter(StorefrontCacheKeys.ProductTtl));
        return tree;
    }

    public async Task<SlugLookupResult<PagedResult<ProductCardDto>>> ListProductsByCategoryAsync(
        string categorySlug, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 60);
        if (!Slug.IsValid(categorySlug)) return SlugLookupResult<PagedResult<ProductCardDto>>.NotFound();

        var redis = _redis.GetDatabase();
        var key = StorefrontCacheKeys.Listing(categorySlug, page, pageSize);
        var cached = await redis.StringGetAsync(key);
        if (cached.HasValue)
        {
            var hit = JsonSerializer.Deserialize<PagedResult<ProductCardDto>>((string)cached!, CacheJson.Options);
            if (hit is not null) return SlugLookupResult<PagedResult<ProductCardDto>>.Found(hit);
        }

        var category = await _db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == categorySlug, ct);
        if (category is null)
        {
            var movedTo = await _db.SlugHistories.AsNoTracking()
                .Where(h => h.EntityType == SlugEntityType.Category && h.OldSlug == categorySlug)
                .Join(_db.Categories, h => h.EntityId, c => c.Id, (_, c) => c.Slug).FirstOrDefaultAsync(ct);
            return movedTo is null ? SlugLookupResult<PagedResult<ProductCardDto>>.NotFound()
                : SlugLookupResult<PagedResult<ProductCardDto>>.MovedTo(movedTo);
        }

        var prefix = category.MaterializedPath + ".";
        var basis = _db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Active && p.DeletedAtUtc == null)
            .Join(_db.Categories, p => p.CategoryId, c => c.Id, (p, c) => new { p, c })
            .Where(x => x.c.Id == category.Id || x.c.MaterializedPath.StartsWith(prefix))
            .Select(x => x.p);

        var total = await basis.CountAsync(ct);
        var rows = await basis
            .OrderByDescending(p => p.SoldCount).ThenBy(p => p.Slug)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Slug,
                p.TitleEn,
                p.TitleBn,
                p.RatingAvg,
                p.RatingCount,
                p.CategoryId,
                Brand = p.BrandId == null ? null : _db.Brands.Where(b => b.Id == p.BrandId).Select(b => b.Name).FirstOrDefault(),
                Image = p.Media.OrderBy(m => m.Position).Select(m => new { m.Url, m.AltText }).FirstOrDefault(),
                Variant = p.Variants.Where(v => v.Status == VariantStatus.Active).OrderByDescending(v => v.IsDefault).ThenBy(v => v.PriceMinor).FirstOrDefault(),
                Available = _db.InventoryItems.Where(i => p.Variants.Select(v => v.Id).Contains(i.VariantId)).Sum(i => (int?)(i.OnHand - i.Reserved)) ?? 0
            })
            .ToListAsync(ct);

        var vatByCategory = await _db.Categories.AsNoTracking()
            .Where(c => rows.Select(r => r.CategoryId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.VatRate, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var cards = rows.Where(r => r.Variant is not null).Select(r =>
        {
            var v = r.Variant!;
            var price = PriceFor(new CachedVariant
            {
                Id = v.Id,
                Sku = v.Sku,
                Currency = v.Currency.ToIsoCode(),
                PriceMinor = v.PriceMinor,
                CompareAtMinor = v.CompareAtPriceMinor,
                SalePriceMinor = v.SalePriceMinor,
                SaleStartsAtUtc = v.SaleStartsAtUtc,
                SaleEndsAtUtc = v.SaleEndsAtUtc
            }, vatByCategory.GetValueOrDefault(r.CategoryId, 0.05m), now);
            return new ProductCardDto(r.Id, r.Slug, r.TitleEn, r.TitleBn, r.Brand, r.Image?.Url, r.Image?.AltText,
                price.Price, price.CompareAt, price.DiscountPercent, r.RatingAvg, r.RatingCount, ToStock(r.Available).Status);
        }).ToList();

        var result = new PagedResult<ProductCardDto>(cards, page, pageSize, total);
        await redis.StringSetAsync(key, JsonSerializer.Serialize(result, CacheJson.Options), StorefrontCacheKeys.ListingTtl);
        return SlugLookupResult<PagedResult<ProductCardDto>>.Found(result);
    }

    #endregion

    #region Cached document

    internal sealed class CachedProduct
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string TitleEn { get; set; } = string.Empty;
        public string TitleBn { get; set; } = string.Empty;
        public string ShortDescriptionEn { get; set; } = string.Empty;
        public string ShortDescriptionBn { get; set; } = string.Empty;
        public string DescriptionHtml { get; set; } = string.Empty;
        public List<string> Highlights { get; set; } = [];
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Specs { get; set; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>();
        public BrandDto? Brand { get; set; }
        public SellerSummaryDto Seller { get; set; } = null!;
        public List<BreadcrumbDto> Breadcrumbs { get; set; } = [];
        public string Condition { get; set; } = "brand_new";
        public int WarrantyMonths { get; set; }
        public int ReturnPolicyDays { get; set; }
        public decimal RatingAvg { get; set; }
        public int RatingCount { get; set; }
        public int SoldCount { get; set; }
        public string? DatasheetUrl { get; set; }
        public string? CadModelUrl { get; set; }
        public decimal VatRate { get; set; }
        public List<MediaDto> Media { get; set; } = [];
        public List<CachedVariant> Variants { get; set; } = [];
    }

    internal sealed class CachedVariant
    {
        public Guid Id { get; set; }
        public string Sku { get; set; } = string.Empty;
        public Dictionary<string, string> Options { get; set; } = [];
        public bool IsDefault { get; set; }
        public string Currency { get; set; } = "BDT";
        public long PriceMinor { get; set; }
        public long? CompareAtMinor { get; set; }
        public long? SalePriceMinor { get; set; }
        public DateTime? SaleStartsAtUtc { get; set; }
        public DateTime? SaleEndsAtUtc { get; set; }
    }

    #endregion
}
