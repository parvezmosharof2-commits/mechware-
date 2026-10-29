using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.Domain.Pricing;

/// <summary>How VAT is presented: BD storefront prices include VAT, international prices exclude it.</summary>
public enum TaxMode { Inclusive = 1, Exclusive = 2 }

/// <summary>Everything the engine needs to price one variant, with no I/O.</summary>
public sealed record PricingInput(
    Money Price,
    Money? CompareAtPrice,
    Money? SalePrice,
    DateTime? SaleStartsAtUtc,
    DateTime? SaleEndsAtUtc,
    decimal VatRate)
{
    public static PricingInput From(ProductVariant variant, decimal vatRate) => new(
        variant.Price, variant.CompareAtPrice, variant.SalePrice, variant.SaleStartsAtUtc, variant.SaleEndsAtUtc, vatRate);
}

/// <summary>Computed price presentation. All amounts share one currency.</summary>
/// <param name="Net">Effective price before VAT.</param>
/// <param name="Tax">VAT amount.</param>
/// <param name="Gross">What the buyer pays per unit (Net + Tax).</param>
/// <param name="Display">The headline price for the requested <see cref="TaxMode"/>.</param>
/// <param name="StrikeThrough">Reference price shown crossed out, if any (display tax mode).</param>
/// <param name="DiscountPercent">Whole-percent discount vs. <see cref="StrikeThrough"/>, rounded down.</param>
public sealed record PriceQuote(
    Money Net, Money Tax, Money Gross, Money Display, Money? StrikeThrough, int DiscountPercent, bool SaleActive);

/// <summary>
/// Deterministic pure pricing (brief §8.1). Rules:
/// <list type="bullet">
/// <item>Effective price = sale price inside <c>[start, end)</c>, otherwise the regular price.</item>
/// <item>Strike-through = compare-at when it exceeds the effective price; else the regular price while a sale runs.</item>
/// <item>Discount % = floor((strike − price) / strike × 100).</item>
/// <item>Catalog prices are stored as <i>entered</i> amounts: tax-inclusive mode treats them as gross, exclusive mode as net.</item>
/// <item>Intermediate math uses 128-bit <see cref="decimal"/>; only the final amounts are rounded (banker's, via <see cref="Money"/>).</item>
/// </list>
/// </summary>
public static class PricingEngine
{
    public static PriceQuote Quote(PricingInput input, DateTime asOfUtc, TaxMode mode)
    {
        if (input.VatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");
        var currency = input.Price.Currency;
        if (input.SalePrice is { } s && s.Currency != currency) throw new CurrencyMismatchException(currency, s.Currency);
        if (input.CompareAtPrice is { } c && c.Currency != currency) throw new CurrencyMismatchException(currency, c.Currency);

        var saleActive = IsSaleActive(input, asOfUtc);
        var effective = saleActive ? input.SalePrice!.Value : input.Price;

        Money? strike = null;
        if (input.CompareAtPrice is { } compare && compare > effective) strike = compare;
        else if (saleActive && input.Price > effective) strike = input.Price;

        var vat = input.VatRate;
        Money net, tax, gross;
        if (mode == TaxMode.Inclusive)
        {
            // Entered price is gross; extract the VAT component: tax = gross - gross / (1 + rate)
            gross = effective;
            var netAmount = effective.Amount / (1m + vat);
            net = new Money(netAmount, currency);
            tax = gross - net;
        }
        else
        {
            net = effective;
            tax = new Money(effective.Amount * vat, currency);
            gross = net + tax;
        }

        // In exclusive mode the headline is the net price (tax added at checkout).
        var display = mode == TaxMode.Inclusive ? gross : net;
        var discount = strike is { } st ? DiscountPercent(st, effective) : 0;
        return new PriceQuote(net, tax, gross, display, strike, discount, saleActive);
    }

    public static bool IsSaleActive(PricingInput input, DateTime asOfUtc) =>
        input.SalePrice is { } sale
        && sale.IsPositive
        && input.SaleStartsAtUtc is { } start
        && input.SaleEndsAtUtc is { } end
        && asOfUtc >= start
        && asOfUtc < end;

    /// <summary>Whole-percent discount rounded <b>down</b>; 0 when <paramref name="price"/> is not below <paramref name="reference"/>.</summary>
    public static int DiscountPercent(Money reference, Money price)
    {
        if (reference.Currency != price.Currency) throw new CurrencyMismatchException(reference.Currency, price.Currency);
        if (reference.Amount <= 0m || price >= reference) return 0;
        var pct = (reference.Amount - price.Amount) / reference.Amount * 100m;
        return (int)decimal.Floor(pct);
    }
}
