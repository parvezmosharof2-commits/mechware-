using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.Domain.Orders;

/// <summary>One purchased line as the server resolved it: price, VAT rate and promotion discount are never taken from the client.</summary>
/// <param name="UnitPriceMinor">Unit price from price resolution (brief §8.1); gross when the mode is inclusive, net when exclusive.</param>
/// <param name="DiscountMinor">Promotion discount allocated to this line (from <see cref="PromotionEngine"/>).</param>
/// <param name="Price">Where the unit price came from (ADR-0008 §8); frozen on the order line. When given, its variant and unit price must be the line's.</param>
/// <param name="QuoteLineId">The accepted quote line (ADR-0011 §8.3): present exactly when <see cref="Price"/> is a quote price, which takes no discount.</param>
public sealed record OrderLineDraft(
    Guid LineId, Guid VariantId, Guid SellerId, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate, long DiscountMinor,
    PriceResolution? Price = null, Guid? QuoteLineId = null);

/// <param name="ShippingMinor">Shipping charged by this seller's shipment (already tax-inclusive or not per the mode).</param>
/// <param name="CommissionRate">Marketplace commission on the seller's net goods value (seller override, else category rate).</param>
public sealed record SellerTerms(Guid SellerId, long ShippingMinor, decimal CommissionRate);

public sealed record PricedLine(
    Guid LineId, Guid VariantId, Guid SellerId, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate,
    long GrossMinor, long DiscountMinor, long NetMinor, long TaxMinor, long TotalMinor, PriceResolution? Price = null, Guid? QuoteLineId = null);

/// <param name="GoodsNetMinor">Seller's goods after discount, before VAT: the base for commission.</param>
public sealed record PricedSubOrder(
    Guid SellerId, IReadOnlyList<PricedLine> Lines, long GoodsNetMinor, long DiscountMinor, long TaxMinor, long ShippingMinor,
    long TotalMinor, decimal CommissionRate, long CommissionMinor, long SellerEarningMinor);

public sealed record PricedOrder(
    IReadOnlyList<PricedSubOrder> SubOrders, long SubtotalMinor, long DiscountMinor, long TaxMinor, long ShippingMinor, long GrandTotalMinor);

/// <summary>
/// Turns resolved lines into the order's money (brief §8.1, ADR-0002). Rounding happens once per line, in whole poisha
/// (banker's); every higher total is a plain sum of lower ones, so
/// <c>GrandTotal = Σ line totals + shipping</c> holds exactly and invoices, refunds and seller payouts always reconcile.
/// </summary>
public static class OrderPricing
{
    public static PricedOrder Price(IReadOnlyList<OrderLineDraft> lines, IReadOnlyList<SellerTerms> terms, TaxMode mode)
    {
        if (lines.Count == 0) throw new ValidationException("lines", "An order needs at least one line.");

        var termsBySeller = new Dictionary<Guid, SellerTerms>();
        foreach (var t in terms)
        {
            if (t.ShippingMinor < 0) throw new ValidationException("shipping", "Shipping cannot be negative.");
            if (t.CommissionRate is < 0m or > 1m) throw new ValidationException("commission", "Commission rate must be between 0 and 1.");
            if (!termsBySeller.TryAdd(t.SellerId, t)) throw new ValidationException("terms", "Seller terms were given twice.");
        }

        var priced = lines.Select(l => PriceLine(l, mode)).ToList();
        var subOrders = new List<PricedSubOrder>();

        // Sellers appear in the order of their first line, so the result does not depend on dictionary ordering.
        foreach (var group in priced.GroupBy(l => l.SellerId))
        {
            if (!termsBySeller.TryGetValue(group.Key, out var seller))
                throw new ValidationException("terms", $"No terms for seller {group.Key}.");

            var sellerLines = group.ToList();
            var goodsNet = sellerLines.Sum(l => l.NetMinor);
            var tax = sellerLines.Sum(l => l.TaxMinor);
            var discount = sellerLines.Sum(l => l.DiscountMinor);
            var commission = (long)decimal.Round(goodsNet * seller.CommissionRate, 0, MidpointRounding.ToEven);

            subOrders.Add(new PricedSubOrder(
                group.Key, sellerLines, goodsNet, discount, tax, seller.ShippingMinor,
                sellerLines.Sum(l => l.TotalMinor) + seller.ShippingMinor,
                seller.CommissionRate, commission, goodsNet - commission));
        }

        return new PricedOrder(
            subOrders,
            priced.Sum(l => l.GrossMinor),
            subOrders.Sum(s => s.DiscountMinor),
            subOrders.Sum(s => s.TaxMinor),
            subOrders.Sum(s => s.ShippingMinor),
            subOrders.Sum(s => s.TotalMinor));
    }

    /// <summary>
    /// What the goods count toward a free-shipping threshold (E8.2): after discounts, VAT included, shipping excluded. A
    /// VAT-inclusive (guest or personal) order counts its own line totals. A company order counts what the same lines count
    /// for a guest: quantity × the guest's VAT-inclusive unit price, less the line's own discount with its VAT. So neither
    /// the rounding of public prices to net (owner decision 2) nor a contract price (rule A) ever costs a company the free
    /// shipping a guest gets for the same cart, and a company never pays more than a guest for it.
    /// </summary>
    /// <param name="guestUnitPriceMinor">Per line id: what a guest pays per unit for that quantity at the order's instant; read only for exclusive orders.</param>
    public static long FreeShippingBasisMinor(IReadOnlyList<OrderLineDraft> lines, IReadOnlyDictionary<Guid, long> guestUnitPriceMinor, TaxMode mode)
    {
        long basis = 0;
        foreach (var line in lines)
        {
            var total = PriceLine(line, mode).TotalMinor;
            if (mode == TaxMode.Exclusive)
            {
                // undiscounted − discounted = the line's discount with its VAT; never below zero (rule A keeps the guest value higher).
                var discountWithVat = PriceLine(line with { DiscountMinor = 0 }, mode).TotalMinor - total;
                total = Math.Max(total, checked(line.Quantity * guestUnitPriceMinor[line.LineId]) - discountWithVat);
            }

            basis = checked(basis + total);
        }

        return basis;
    }

    /// <summary>
    /// One line's money exactly as <see cref="Price"/> computes it (VAT rounded once per line). The buyer price endpoint
    /// (D2.10) shows this, so the line it shows is the line an order for the same quantity would carry before promotions.
    /// </summary>
    public static PricedLine PriceLine(OrderLineDraft l, TaxMode mode)
    {
        if (l.Quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (l.UnitPriceMinor < 0) throw new ValidationException("unitPrice", "Unit price cannot be negative.");
        if (l.VatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");
        // The snapshot must describe the price actually charged, or an audit would explain a different number.
        if (l.Price is { } p && (p.VariantId != l.VariantId || p.UnitPriceMinor != l.UnitPriceMinor))
            throw new ValidationException("price", "The price snapshot does not match the line.");
        // ADR-0011 §8.3, ADR-0008 §6 (QT2): a quote line is the quoted price exactly, never discounted, and only a quote price names a quote line.
        if ((l.QuoteLineId is not null) != (l.Price is { Source: PriceSource.Quote }) || (l.QuoteLineId is not null && l.DiscountMinor != 0))
            throw new ValidationException("price", "A quote price needs its quote line and takes no discount.");

        var gross = checked(l.Quantity * l.UnitPriceMinor);
        if (l.DiscountMinor < 0 || l.DiscountMinor > gross) throw new ValidationException("discount", "A discount cannot exceed the line's value.");
        var afterDiscount = gross - l.DiscountMinor;

        long net, tax, total;
        if (mode == TaxMode.Inclusive)
        {
            // The price already contains VAT: split it, never add it twice.
            total = afterDiscount;
            net = (long)decimal.Round(afterDiscount / (1m + l.VatRate), 0, MidpointRounding.ToEven);
            tax = total - net;
        }
        else
        {
            net = afterDiscount;
            tax = (long)decimal.Round(net * l.VatRate, 0, MidpointRounding.ToEven);
            total = net + tax;
        }

        return new PricedLine(l.LineId, l.VariantId, l.SellerId, l.Sku, l.Title, l.Quantity, l.UnitPriceMinor, l.VatRate,
            gross, l.DiscountMinor, net, tax, total, l.Price, l.QuoteLineId);
    }
}
