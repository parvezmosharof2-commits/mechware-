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
        PriceMinor = price.ToMinorUnits();
        CompareAtPriceMinor = compareAtPrice?.ToMinorUnits();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Schedules a promotional price valid in <c>[startsAt, endsAt)</c>.</summary>
    public void ScheduleSale(Money salePrice, DateTime startsAtUtc, DateTime endsAtUtc)
    {
        if (!salePrice.IsPositive) throw new ValidationException("salePrice", "Sale price must be greater than zero.");
        if (salePrice >= Price) throw new ValidationException("salePrice", "Sale price must be below the regular price.");
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
