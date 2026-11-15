using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Application.Orders;
using Mechware.Application.Pricing;
using Mechware.Domain.Cart;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// <see cref="IQuickOrderValidator"/> over the database with a fixed number of queries however many lines there are (no N+1):
/// 1 the variants whose part-number key or SKU is asked for, 2 the look-alike keys of the unmatched lines, 3 stock, 4 the
/// successors of obsolete parts, 5 the price lists (inside <see cref="IPriceResolver"/>). Only published products with an active
/// variant are ever read, so a draft, archived or inactive item is indistinguishable from an unknown part number. Prices are
/// the guest's: no company id is passed to the resolver, the mode is VAT-inclusive.
/// </summary>
public sealed class QuickOrderValidator(MechwareDbContext db, IPriceResolver prices, TimeProvider clock) : IQuickOrderValidator
{
    public const int MaxCandidates = 10;
    public const int MaxSuggestions = 3;
    private const int MaxRows = 5000;
    private const string BdtCode = "BDT";

    private sealed record Row(ProductVariant Variant, string Slug, string TitleEn, string TitleBn, string? Brand,
        SupplyStatus Supply, Guid? SuccessorProductId, bool SuccessorIsDropIn, string? SuccessorNote, decimal VatRate);

    public async Task<QuickOrderValidation> ValidateAsync(string? text, CancellationToken ct = default)
    {
        var parsed = QuickOrderLines.Parse(text);
        if (parsed.InputError is { } refused)
        {
            throw refused.Code switch
            {
                "TOO_MANY_LINES" => new UnprocessableException(refused.Code, refused.Message),
                "INPUT_TOO_LARGE" => new PayloadTooLargeException(refused.Code, refused.Message),
                _ => new UnprocessableException(refused.Code, refused.Message)
            };
        }

        var wanted = parsed.Lines;
        var empty = new QuickOrderSummary(0, 0, 0, 0, BdtCode);
        if (wanted.Count == 0) return new QuickOrderValidation([], parsed.Errors, parsed.Notices, empty);

        // 1. Exact: the part-number key, or the SKU as typed (SKUs are stored upper case with their punctuation).
        var keys = wanted.Select(l => l.PartKey).Distinct().ToList();
        var skus = wanted.SelectMany(l => new[] { l.PartRaw.Trim().ToUpperInvariant(), l.PartKey }).Distinct().ToList();
        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                          join b in db.Brands.AsNoTracking() on p.BrandId equals b.Id into bs
                          from b in bs.DefaultIfEmpty()
                          where p.Status == ProductStatus.Active && p.DeletedAtUtc == null && v.Status == VariantStatus.Active
                                && ((v.PartNumberKey != null && keys.Contains(v.PartNumberKey)) || skus.Contains(v.Sku))
                          orderby v.Sku
                          select new Row(v, p.Slug, p.TitleEn, p.TitleBn, b == null ? null : b.Name, p.SupplyStatus,
                              p.SuccessorProductId, p.SuccessorIsDropIn, p.SuccessorNote, c.VatRate))
            .Take(MaxRows).ToListAsync(ct);

        var byKey = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            Add(byKey, r.Variant.PartNumberKey, r);
            string skuKey = PartNumbers.Normalise(r.Variant.Sku);
            if (skuKey != r.Variant.PartNumberKey) Add(byKey, skuKey, r);
        }

        var matches = wanted.ToDictionary(l => l.PartKey, l => byKey.TryGetValue(l.PartKey, out var m) ? m : []);

        // 2. Did you mean: the look-alike key (O/0, I/L/1) of the lines nothing matched, at most MaxSuggestions each.
        var unmatchedFuzzy = wanted.Where(l => matches[l.PartKey].Count == 0).Select(l => l.FuzzyKey).Distinct().ToList();
        var suggestions = new Dictionary<string, List<QuickOrderVariant>>(StringComparer.Ordinal);
        if (unmatchedFuzzy.Count > 0)
        {
            var near = await (from v in db.ProductVariants.AsNoTracking()
                              join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                              join b in db.Brands.AsNoTracking() on p.BrandId equals b.Id into bs
                              from b in bs.DefaultIfEmpty()
                              where p.Status == ProductStatus.Active && p.DeletedAtUtc == null && v.Status == VariantStatus.Active
                                    && v.PartNumberFuzzyKey != null && unmatchedFuzzy.Contains(v.PartNumberFuzzyKey)
                              orderby v.PartNumberFuzzyKey, v.Sku
                              select new { Key = v.PartNumberFuzzyKey!, Variant = ToVariant(v, p.Slug, p.TitleEn, p.TitleBn, b == null ? null : b.Name) })
                .Take(MaxRows).ToListAsync(ct);
            foreach (var g in near.GroupBy(n => n.Key))
                suggestions[g.Key] = [.. g.Take(MaxSuggestions).Select(n => n.Variant)];
        }

        // Single-match lines carry on to stock, successor and price.
        var singles = wanted.Where(l => matches[l.PartKey].Count == 1).ToDictionary(l => l.PartKey, l => matches[l.PartKey][0]);
        var variantIds = singles.Values.Select(r => r.Variant.Id).Distinct().ToList();

        // Quantities fitted to the order units (the cart's rules), then added up per variant: the resolver prices all units of
        // one variant together (quantity breaks), whichever lines they were typed on.
        var fits = wanted.Where(l => singles.ContainsKey(l.PartKey))
            .ToDictionary(l => l.PartKey, l => CartEngine.FitToOrderUnits(l.Quantity, singles[l.PartKey].Variant.MinOrderQty, singles[l.PartKey].Variant.PackSize));
        var totals = new Dictionary<Guid, long>();
        foreach (var l in wanted.Where(l => singles.ContainsKey(l.PartKey)))
            totals[singles[l.PartKey].Variant.Id] = totals.GetValueOrDefault(singles[l.PartKey].Variant.Id) + fits[l.PartKey].Quantity;
        var linesOfVariant = wanted.Where(l => singles.ContainsKey(l.PartKey)).GroupBy(l => singles[l.PartKey].Variant.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 3. Stock: on hand minus reserved over active warehouses (what the cart and the PDP show).
        var available = variantIds.Count == 0 ? [] : await db.InventoryItems.AsNoTracking()
            .Where(i => variantIds.Contains(i.VariantId))
            .Join(db.Warehouses.Where(w => w.IsActive), i => i.WarehouseId, w => w.Id, (i, _) => i)
            .GroupBy(i => i.VariantId)
            .Select(g => new { VariantId = g.Key, Available = g.Sum(i => i.OnHand - i.Reserved) })
            .ToDictionaryAsync(x => x.VariantId, x => x.Available, ct);

        // 4. Successors of the obsolete parts (only a product on sale that is not itself obsolete, like the product page).
        var successorIds = singles.Values.Where(r => r.Supply == SupplyStatus.Obsolete && r.SuccessorProductId is not null)
            .Select(r => r.SuccessorProductId!.Value).Distinct().ToList();
        var successors = new Dictionary<Guid, QuickOrderVariant>();
        if (successorIds.Count > 0)
        {
            var found = await (from v in db.ProductVariants.AsNoTracking()
                               join p in db.Products.AsNoTracking().Where(Product.IsLinkable) on v.ProductId equals p.Id
                               join b in db.Brands.AsNoTracking() on p.BrandId equals b.Id into bs
                               from b in bs.DefaultIfEmpty()
                               where successorIds.Contains(p.Id) && v.Status == VariantStatus.Active
                               orderby v.IsDefault descending, v.Sku
                               select new { ProductId = p.Id, Variant = ToVariant(v, p.Slug, p.TitleEn, p.TitleBn, b == null ? null : b.Name) })
                .ToListAsync(ct);
            foreach (var g in found.GroupBy(f => f.ProductId)) successors[g.Key] = g.First().Variant;
        }

        // 5. Price: only lines that can be sold as asked (found, on sale, within the line limit). One resolver call.
        var rowOf = singles.Values.GroupBy(r => r.Variant.Id).ToDictionary(g => g.Key, g => g.First());
        var priceable = totals.Where(t => t.Value <= OrderLimits.MaxQuantityPerLine && rowOf[t.Key].Supply != SupplyStatus.Obsolete)
            .Select(t => t.Key).ToList();
        var now = clock.GetUtcNow().UtcDateTime;
        var resolved = priceable.Count == 0 ? [] : (await prices.ResolveAsync(null,
                [.. priceable.Select(id => new PriceRequestLine(id, (int)totals[id], PricingInput.From(rowOf[id].Variant, rowOf[id].VatRate)))],
                TaxMode.Inclusive, now, ct))
            .ToDictionary(p => p.Price.VariantId);

        var results = new List<QuickOrderLineResult>(wanted.Count);
        foreach (var line in wanted)
        {
            var found = matches[line.PartKey];
            if (found.Count == 0)
            {
                var near = suggestions.GetValueOrDefault(line.FuzzyKey) ?? [];
                results.Add(Empty(line, "PART_NOT_FOUND", near.Count > 0
                    ? "No product has this part number as typed. Did you mean one of the suggestions?"
                    : "No product has this part number.", suggestions: near));
                continue;
            }

            if (found.Count > 1)
            {
                results.Add(Empty(line, "PART_AMBIGUOUS", "Several products share this part number; choose one.",
                    candidates: [.. found.Take(MaxCandidates).Select(r => ToVariant(r))]));
                continue;
            }

            var row = found[0];
            var variant = ToVariant(row);
            var fit = fits[line.PartKey];
            var notices = new List<QuickOrderLineNotice>();
            if (fit.Notice is { } kind) notices.Add(new(Camel(kind), line.Quantity, fit.Quantity));
            if (row.Supply == SupplyStatus.Nrnd) notices.Add(new("nrnd", null, null));
            if (linesOfVariant[row.Variant.Id].Count > 1) notices.Add(new("sameVariantOnSeveralLines", null, null));

            if (row.Supply == SupplyStatus.Obsolete)
            {
                var successor = row.SuccessorProductId is { } sid && successors.TryGetValue(sid, out var sv)
                    ? new QuickOrderSuccessor(sv, row.SuccessorIsDropIn, row.SuccessorNote) : null;
                results.Add(Empty(line, "PART_OBSOLETE", successor is null
                    ? "This part is obsolete and has no listed successor."
                    : "This part is obsolete. Its successor is offered; nothing is swapped for you.", variant: variant, successor: successor, notices: notices));
                continue;
            }

            long total = totals[row.Variant.Id];
            int stock = Math.Max(0, available.GetValueOrDefault(row.Variant.Id));
            var stockDto = StockDto.Of(stock);
            if (total > OrderLimits.MaxQuantityPerLine)
            {
                results.Add(Empty(line, "QTY_EXCEEDS_LIMIT", $"At most {OrderLimits.MaxQuantityPerLine:N0} of one item per order.", variant: variant, notices: notices, stock: stockDto, quantity: fit.Quantity));
                continue;
            }

            var price = resolved[row.Variant.Id].Price;
            long unit = price.UnitPriceMinor, lineTotal = checked(unit * fit.Quantity);
            string status = "OK", message = "Ready to add.";
            if (stock <= 0) (status, message) = ("OUT_OF_STOCK", "Out of stock.");
            else if (total > stock) (status, message) = ("INSUFFICIENT_STOCK", $"Only {stock} available.");

            results.Add(new QuickOrderLineResult(line.LineNumbers, line.Raw, line.PartRaw, line.Quantity, status, message, status == "OK",
                variant, fit.Quantity, notices, stockDto, unit, lineTotal, Camel(price.Source), null, [], []));
        }

        long subtotal = results.Where(r => r.Valid).Sum(r => r.LineTotalMinor ?? 0);
        int valid = results.Count(r => r.Valid);
        return new QuickOrderValidation(results, parsed.Errors, parsed.Notices,
            new QuickOrderSummary(results.Count, valid, results.Count - valid, subtotal, BdtCode));
    }

    private static QuickOrderLineResult Empty(QuickOrderLine line, string status, string message, QuickOrderVariant? variant = null,
        QuickOrderSuccessor? successor = null, IReadOnlyList<QuickOrderLineNotice>? notices = null, StockDto? stock = null,
        int? quantity = null, IReadOnlyList<QuickOrderVariant>? candidates = null, IReadOnlyList<QuickOrderVariant>? suggestions = null) =>
        new(line.LineNumbers, line.Raw, line.PartRaw, line.Quantity, status, message, false, variant, quantity, notices ?? [], stock,
            null, null, null, successor, candidates ?? [], suggestions ?? []);

    private static void Add(Dictionary<string, List<Row>> map, string? key, Row row)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (list.All(r => r.Variant.Id != row.Variant.Id)) list.Add(row);
    }

    private static QuickOrderVariant ToVariant(Row r) => ToVariant(r.Variant, r.Slug, r.TitleEn, r.TitleBn, r.Brand);

    private static QuickOrderVariant ToVariant(ProductVariant v, string slug, string titleEn, string titleBn, string? brand) =>
        new(v.Id, v.Sku, v.ManufacturerPartNumber, titleEn, titleBn, brand, slug, v.OptionValues.ToDictionary(o => o.Key, o => o.Value));

    private static string Camel(Enum value) => char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
}
