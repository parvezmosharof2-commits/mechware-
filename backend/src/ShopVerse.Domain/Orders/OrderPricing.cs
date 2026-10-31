using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Pricing;

namespace ShopVerse.Domain.Orders;

/// <summary>One purchased line as the server resolved it: price, VAT rate and promotion discount are never taken from the client.</summary>
/// <param name="UnitPriceMinor">Unit price from price resolution (brief §8.1); gross when the mode is inclusive, net when exclusive.</param>
/// <param name="DiscountMinor">Promotion discount allocated to this line (from <see cref="PromotionEngine"/>).</param>
public sealed record OrderLineDraft(
    Guid LineId, Guid VariantId, Guid SellerId, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate, long DiscountMinor);

/// <param name="ShippingMinor">Shipping charged by this seller's shipment (already tax-inclusive or not per the mode).</param>
/// <param name="CommissionRate">Marketplace commission on the seller's net goods value (seller override, else category rate).</param>
public sealed record SellerTerms(Guid SellerId, long ShippingMinor, decimal CommissionRate);

public sealed record PricedLine(
    Guid LineId, Guid VariantId, Guid SellerId, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate,
    long GrossMinor, long DiscountMinor, long NetMinor, long TaxMinor, long TotalMinor);

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

    private static PricedLine PriceLine(OrderLineDraft l, TaxMode mode)
    {
        if (l.Quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (l.UnitPriceMinor < 0) throw new ValidationException("unitPrice", "Unit price cannot be negative.");
        if (l.VatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");

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
            gross, l.DiscountMinor, net, tax, total);
    }
}
