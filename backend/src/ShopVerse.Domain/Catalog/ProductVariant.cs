using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.Domain.Catalog;

public enum VariantStatus { Active = 1, Inactive = 2 }

/// <summary>Purchasable SKU. Price fields follow ADR 0002 (minor units in the DB, <see cref="Money"/> in code).</summary>
public sealed class ProductVariant : Entity<Guid>
{
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string? Barcode { get; private set; }

    /// <summary>Option axis → value (JSONB), e.g. <c>{"Voltage":"24V DC"}</c>.</summary>
    public SortedDictionary<string, string> OptionValues { get; private set; } = new(StringComparer.Ordinal);

    // Persisted as integer minor units + currency (ADR 0002); exposed as Money below.
    public long PriceMinor { get; private set; }
    public long? SalePriceMinor { get; private set; }
    public long? CompareAtPriceMinor { get; private set; }
    public long? CostPriceMinor { get; private set; }
    public Currency Currency { get; private set; } = Currency.BDT;

    public DateTime? SaleStartsAtUtc { get; private set; }
    public DateTime? SaleEndsAtUtc { get; private set; }

    public Money Price => Money.FromMinorUnits(PriceMinor, Currency);
    public Money? SalePrice => SalePriceMinor is { } v ? Money.FromMinorUnits(v, Currency) : null;
    public Money? CompareAtPrice => CompareAtPriceMinor is { } v ? Money.FromMinorUnits(v, Currency) : null;
    public Money? CostPrice => CostPriceMinor is { } v ? Money.FromMinorUnits(v, Currency) : null;
    public decimal WeightKg { get; private set; } = 0.5m;
    public bool IsDefault { get; private set; }
    public VariantStatus Status { get; private set; } = VariantStatus.Active;
    /// <summary>Smallest quantity sold on one order line (PIM); always whole packs.</summary>
    public int MinOrderQty { get; private set; } = 1;
    /// <summary>Units per pack; a line's quantity must be a multiple of it.</summary>
    public int PackSize { get; private set; } = 1;

    /// <summary>The manufacturer's part number as printed (e.g. "6ES7214-1AG40-0XB0"); what buyers search for.</summary>
    public string? ManufacturerPartNumber { get; private set; }

    /// <summary><see cref="PartNumbers.Normalise"/> of the part number: what look-ups compare.</summary>
    public string? PartNumberKey { get; private set; }

    /// <summary><see cref="PartNumbers.Fuzzy"/> of the key, for the second look.</summary>
    public string? PartNumberFuzzyKey { get; private set; }

    internal string OptionKey => OptionKeyOf(OptionValues);

    private ProductVariant() { }

    internal ProductVariant(Guid id, Guid productId, string sku, string? barcode, IDictionary<string, string> optionValues,
        Money price, Money? compareAtPrice, Money? costPrice, decimal weightKg, bool isDefault) : base(id)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ValidationException("sku", "SKU is required.");
        if (!price.IsPositive) throw new ValidationException("price", "Price must be greater than zero.");
        if (weightKg <= 0) throw new ValidationException("weightKg", "Weight must be positive.");
        EnsureSameCurrency(price, compareAtPrice);
        EnsureSameCurrency(price, costPrice);

        ProductId = productId;
        Sku = sku.Trim().ToUpperInvariant();
        Barcode = barcode;
        OptionValues = new SortedDictionary<string, string>(optionValues, StringComparer.Ordinal);
        PriceMinor = price.ToMinorUnits();
        CompareAtPriceMinor = compareAtPrice?.ToMinorUnits();
        CostPriceMinor = costPrice?.ToMinorUnits();
        Currency = price.Currency;
        WeightKg = weightKg;
        IsDefault = isDefault;
    }

    internal static string ComputeOptionKey(IEnumerable<KeyValuePair<string, string>> options) => OptionKeyOf(options);

    private static string OptionKeyOf(IEnumerable<KeyValuePair<string, string>> options) =>
        string.Join("|", options.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key}={o.Value}"));

    internal void ClearDefault() => IsDefault = false;

    public void ChangePrice(Money price, Money? compareAtPrice)
    {
        if (!price.IsPositive) throw new ValidationException("price", "Price must be greater than zero.");
        EnsureSameCurrency(price, compareAtPrice);
        if (SalePriceMinor is { } sale && sale >= price.ToMinorUnits())
            throw new ValidationException("price", "Clear or lower the scheduled sale price before raising the regular price to or above it.");
        PriceMinor = price.ToMinorUnits();
        CompareAtPriceMinor = compareAtPrice?.ToMinorUnits();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Schedules a promotional price valid in <c>[startsAt, endsAt)</c>.</summary>
    public void ScheduleSale(Money salePrice, DateTime startsAtUtc, DateTime endsAtUtc)
    {
        if (!salePrice.IsPositive) throw new ValidationException("salePrice", "Sale price must be greater than zero.");
        if (salePrice >= Price) throw new ValidationException("salePrice", "Sale price must be below the regular price.");
        if (startsAtUtc.Kind != DateTimeKind.Utc || endsAtUtc.Kind != DateTimeKind.Utc)
            throw new ValidationException("saleWindow", "Sale window timestamps must be UTC.");
        if (endsAtUtc <= startsAtUtc) throw new ValidationException("saleEndsAt", "Sale must end after it starts.");
        EnsureSameCurrency(Price, salePrice);

        SalePriceMinor = salePrice.ToMinorUnits();
        SaleStartsAtUtc = startsAtUtc;
        SaleEndsAtUtc = endsAtUtc;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void ClearSale()
    {
        SalePriceMinor = null;
        SaleStartsAtUtc = null;
        SaleEndsAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Sets the order units. The minimum must be whole packs, and both must fit on one cart line.</summary>
    public void SetOrderUnits(int minOrderQty, int packSize)
    {
        // No upper bound of its own: a pack larger than one line could ever hold leaves no valid minimum (checked below).
        if (packSize < 1) throw new ValidationException("packSize", "Pack size must be at least 1.");
        if (minOrderQty < 1 || minOrderQty > Cart.CartEngine.MaxQuantityPerLine)
            throw new ValidationException("minOrderQty", $"Minimum order quantity must be between 1 and {Cart.CartEngine.MaxQuantityPerLine}.");
        if (minOrderQty % packSize != 0)
            throw new ValidationException("minOrderQty", "The minimum order quantity must be a whole number of packs.");
        MinOrderQty = minOrderQty;
        PackSize = packSize;
    }

    /// <summary>Why <paramref name="quantity"/> cannot be ordered on one line, or null when it can.</summary>
    public string? QuantityProblem(int quantity) =>
        quantity < MinOrderQty ? $"{Sku}: the minimum order is {MinOrderQty}."
        : quantity % PackSize != 0 ? $"{Sku}: sold in packs of {PackSize}; order a multiple of {PackSize}."
        : null;

    /// <summary>
    /// Sets or clears (null or blank) the manufacturer part number. At most 64 printable characters, and it must keep at least
    /// two letters or digits once spaces and punctuation are dropped, or it could never be found.
    /// </summary>
    public void SetManufacturerPartNumber(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            ManufacturerPartNumber = PartNumberKey = PartNumberFuzzyKey = null;
            UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        string value = partNumber.Trim();
        if (value.Length > PartNumbers.MaxLength)
            throw new ValidationException("manufacturerPartNumber", $"A part number can be at most {PartNumbers.MaxLength} characters.");
        if (value.Any(char.IsControl))
            throw new ValidationException("manufacturerPartNumber", "A part number cannot contain control characters.");
        string key = PartNumbers.Normalise(value);
        if (key.Length < 2)
            throw new ValidationException("manufacturerPartNumber", "A part number needs at least two letters or digits.");

        ManufacturerPartNumber = value;
        PartNumberKey = key;
        PartNumberFuzzyKey = PartNumbers.Fuzzy(key);
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>What the cart needs to know about this SKU, with its order units.</summary>
    public Cart.VariantFacts ToCartFacts(bool sellable, long unitNetMinor, int available) =>
        new(Id, sellable, unitNetMinor, available, MinOrderQty, PackSize);

    public void Deactivate() => Status = VariantStatus.Inactive;
    public void Activate() => Status = VariantStatus.Active;

    private static void EnsureSameCurrency(Money reference, Money? other)
    {
        if (other is { } o && o.Currency != reference.Currency)
            throw new CurrencyMismatchException(reference.Currency, o.Currency);
    }
}

internal static class VariantGenerator
{
    public static IEnumerable<Dictionary<string, string>> Combinations(IReadOnlyDictionary<string, IReadOnlyList<string>> axes)
    {
        var ordered = axes.OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        IEnumerable<Dictionary<string, string>> acc = [new Dictionary<string, string>()];
        foreach (var (name, values) in ordered)
        {
            acc = acc.SelectMany(prefix => values.Select(v => new Dictionary<string, string>(prefix) { [name] = v }));
        }

        return acc;
    }

    public static string BuildSku(string prefix, IEnumerable<string> optionValues)
    {
        var parts = optionValues.Select(v => new string(v.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant());
        return string.Join("-", new[] { prefix.Trim().ToUpperInvariant() }.Concat(parts));
    }
}
