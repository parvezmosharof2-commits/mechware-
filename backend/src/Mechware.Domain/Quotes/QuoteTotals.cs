using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;

namespace Mechware.Domain.Quotes;

/// <summary>One line's money input: an absolute VAT-exclusive unit price and the line's VAT rate.</summary>
public sealed record QuoteTotalsLine(Guid LineId, Guid VariantId, Guid SellerId, int Quantity, long UnitPriceMinor, decimal VatRate);

public sealed record QuoteLineTotals(long NetMinor, long TaxMinor, long TotalMinor);

/// <param name="ShippingTotalMinor">Only for fixed delivery with a fee for every seller of the lines; calculated delivery is priced at acceptance.</param>
/// <param name="GrandTotalMinor">Goods total + shipping, under the same condition.</param>
public sealed record QuoteVersionTotals(IReadOnlyDictionary<Guid, QuoteLineTotals> Lines, long GoodsNetMinor, long TaxMinor, long GoodsTotalMinor,
    long? ShippingTotalMinor, long? GrandTotalMinor);

/// <summary>
/// Totals of a quote version (ADR-0011 §4) computed by the order's own rule, <see cref="OrderPricing.Price"/> in Exclusive mode
/// with no discount: VAT rounded once per line (banker's, ADR-0002) and every total a plain sum. So the accepted order carries
/// exactly the money the buyer was quoted, to the poisha, while the VAT rate is unchanged.
/// </summary>
public static class QuoteTotals
{
    public static QuoteVersionTotals Compute(IReadOnlyList<QuoteTotalsLine> lines, QuoteShippingMode mode, IReadOnlyList<QuoteShippingFee>? fees)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return new(new Dictionary<Guid, QuoteLineTotals>(), 0, 0, 0, null, null);
        var fee = new Dictionary<Guid, long>();
        foreach (var f in fees ?? [])
            if (!fee.TryAdd(f.SellerId, f.ShippingMinor)) throw new ValidationException("shippingFees", "One fee per seller.");
        var sellers = lines.Select(l => l.SellerId).Distinct().ToList();
        var isFixed = mode == QuoteShippingMode.Fixed && fee.Count == sellers.Count && sellers.All(fee.ContainsKey);

        var priced = OrderPricing.Price(
            lines.Select(l => new OrderLineDraft(l.LineId, l.VariantId, l.SellerId, string.Empty, string.Empty, l.Quantity, l.UnitPriceMinor, l.VatRate, 0))
                .ToList(),
            sellers.Select(s => new SellerTerms(s, isFixed ? fee[s] : 0, 0m)).ToList(),
            TaxMode.Exclusive);
        var byLine = priced.SubOrders.SelectMany(s => s.Lines).ToDictionary(l => l.LineId, l => new QuoteLineTotals(l.NetMinor, l.TaxMinor, l.TotalMinor));
        var net = priced.SubOrders.Sum(s => s.GoodsNetMinor);
        return new(byLine, net, priced.TaxMinor, checked(net + priced.TaxMinor),
            isFixed ? priced.ShippingMinor : null, isFixed ? priced.GrandTotalMinor : null);
    }
}
