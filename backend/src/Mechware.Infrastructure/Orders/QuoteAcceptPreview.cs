using Microsoft.EntityFrameworkCore;
using Mechware.Application.Orders;
using Mechware.Application.Pricing;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Quotes;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// The part of placement that the accept preview shares with acceptance (ADR-0011 §8.3, §8.4; QT15): one method, <see cref="PrepareAsync"/>,
/// runs the checks and prices the order for both, so what the preview shows is what acceptance charges at the same instant.
/// </summary>
public sealed partial class OrderService
{
    /// <summary>
    /// What accepting would charge now (§8.4): the gate, the advance-only rule, the early acceptability check, the account, the company,
    /// cost centre, delivery and catalogue checks and the quote pricing of <see cref="PlaceFromQuoteAsync"/>, with no stock held, no
    /// idempotency record and nothing written; plus the notices <c>vatRateChanged</c>, <c>lowerPriceNow</c> and <c>stockShort</c> per line.
    /// </summary>
    public async Task<QuoteAcceptPreviewDto> PreviewQuoteAcceptanceAsync(AcceptQuoteRequest request, CancellationToken ct = default)
    {
        var (quote, asOrder) = await QuoteAsOrderAsync(request, ct);
        var lines = Normalise(asOrder);
        var now = clock.GetUtcNow().UtcDateTime;
        var (_, _, _, priced, _, _) = await PrepareAsync(asOrder, lines, new QuoteAcceptance(quote, request.ContentHash!), now, ct);

        // The buyer's own price now, as an order from the cart would resolve it (no promotions: those need the cart), and the free units.
        var ids = quote.Lines.Select(l => l.VariantId).ToList();
        var catalogue = await (from v in db.ProductVariants.AsNoTracking()
                               join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                               join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                               where ids.Contains(v.Id)
                               select new { Variant = v, c.VatRate }).ToDictionaryAsync(x => x.Variant.Id, ct);
        var ownPrice = (await prices.ResolveAsync(quote.CompanyId, [.. quote.Lines.Select(l => new PriceRequestLine(l.VariantId, l.Quantity,
                PricingInput.From(catalogue[l.VariantId].Variant, catalogue[l.VariantId].VatRate)))], TaxMode.Exclusive, now, ct))
            .ToDictionary(r => r.Price.VariantId, r => r.Price.UnitPriceMinor);
        var free = await (from i in db.InventoryItems.AsNoTracking()
                          join w in db.Warehouses.AsNoTracking() on i.WarehouseId equals w.Id
                          where ids.Contains(i.VariantId) && w.IsActive && i.OnHand > i.Reserved
                          group i by i.VariantId into g
                          select new { g.Key, Free = g.Sum(i => i.OnHand - i.Reserved) }).ToDictionaryAsync(x => x.Key, x => x.Free, ct);

        var byQuoteLine = priced.SubOrders.SelectMany(s => s.Lines).ToDictionary(l => l.QuoteLineId!.Value);
        var preview = quote.Lines.Select(q => (Quoted: q, Line: byQuoteLine[q.LineId])).Select(x => new QuoteAcceptPreviewLineDto(x.Quoted.LineId,
            x.Line.VariantId, x.Line.Sku, x.Line.Title, x.Line.Quantity, x.Line.UnitPriceMinor, x.Line.VatRate, x.Line.NetMinor, x.Line.TaxMinor,
            x.Line.TotalMinor, x.Line.VatRate != x.Quoted.SentVatRate, ownPrice[x.Quoted.VariantId] < x.Quoted.UnitPriceMinor,
            free.GetValueOrDefault(x.Quoted.VariantId) < x.Quoted.Quantity)).ToList();
        return new QuoteAcceptPreviewDto(quote.QuoteId, quote.VersionNo, preview, priced.SubtotalMinor, priced.TaxMinor, priced.ShippingMinor,
            priced.GrandTotalMinor, preview.Any(l => l.VatRateChanged), preview.Any(l => l.LowerPriceNow), preview.Any(l => l.StockShort));
    }

    /// <summary>
    /// Who may accept what (§8.2): one 404 for anything the caller may not see, 403 without the Buyer role. The order is the quote's, in the
    /// quote's company: its lines and that company come from the quote, never from the request.
    /// </summary>
    private async Task<(QuoteForOrder Quote, PlaceOrderRequest AsOrder)> QuoteAsOrderAsync(AcceptQuoteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gate = quotes ?? throw new InvalidOperationException("Quote acceptance needs an IQuoteAcceptanceGate.");
        if (request.VersionNo is not > 0) throw new ValidationException("versionNo", "Name the version of the quote you accept.");
        if (string.IsNullOrWhiteSpace(request.ContentHash)) throw new ValidationException("contentHash", "Send the content hash of the quote you reviewed.");
        var quote = await gate.FindAsync(request.UserId, request.CompanyId, request.QuoteId, request.VersionNo.Value, ct);
        return (quote, new PlaceOrderRequest(request.UserId, request.IdempotencyKey, [.. quote.Lines.Select(l => new PlaceOrderLine(l.VariantId, l.Quantity))],
            request.ShippingAddressId, quote.CompanyId, request.PayOnCredit, null, request.ShipToSiteId, request.CostCentreId, request.PurchaseOrderNumber,
            request.ExpectedGrandTotalMinor));
    }

    /// <summary>
    /// The checks and the pricing of an order, in placement's order, before any stock is held (shared by <see cref="PlaceAsync"/>,
    /// <see cref="PlaceFromQuoteAsync"/> and <see cref="PreviewQuoteAcceptanceAsync"/>).
    /// </summary>
    private async Task<(CompanyMember? Member, string ShipTo, TaxMode Mode, PricedOrder Priced, IReadOnlyList<AppliedPromotion> Applied, IReadOnlyList<Guid> Categories)>
        PrepareAsync(PlaceOrderRequest request, List<PlaceOrderLine> lines, QuoteAcceptance? quote, DateTime now, CancellationToken ct)
    {
        if (quote is not null)
        {
            // ADR-0011 §8.3 steps 3-4 (QT12): a quote never grants credit, so advance-only terms are paid, never put on account; then the
            // early answer before any stock is held. Both are decided again where it binds (credit under the company lock, the quote under its own).
            if (quote.Quote.PaymentTerms == QuotePaymentTerms.Advance && request.PayOnCredit)
                throw new ConflictException("QUOTE_REQUIRES_ADVANCE_PAYMENT", "This quote is for payment in advance only. Choose a payment method instead of company credit.");
            await quotes!.EnsureAcceptableAsync(quote.Quote, quote.ContentHash, request.UserId, now, ct);
        }

        await RequireBuyerMayHoldStockAsync(request, ct);
        var member = request.CompanyId is { } companyId ? await RequireCompanyMayBuyAsync(request.UserId, companyId, ct) : null;
        if (request.CostCentreId is not null && request.CompanyId is null)
            throw new ValidationException("costCentreId", "A cost centre applies to company orders only.");
        // A member assigned to cost centres buys for those only (threat model v1, M3).
        if (member is { CostCentreIds.Count: > 0 })
        {
            if (request.CostCentreId is not { } chosen)
                throw new ValidationException("costCentreId", "Choose one of your cost centres for this order.");
            if (!member.CostCentreIds.Contains(chosen))
                throw new ForbiddenException("COST_CENTRE_NOT_ASSIGNED", "You are not assigned to this cost centre.");
        }
        var (shipTo, districtId) = quote?.Quote is { ShippingMode: QuoteShippingMode.Fixed } fixedDelivery
            ? await QuotedSiteAsync(request, fixedDelivery, ct)
            : await ResolveShipToAsync(request, ct);

        // Negotiated prices only for the company whose membership was just checked (B1), never for the id in the body.
        var mode = member?.CompanyId is null ? TaxMode.Inclusive : TaxMode.Exclusive;
        if (quote is not null)
        {
            var (quoted, quotedCategories) = await PriceQuoteAsync(quote.Quote, lines, districtId, ct);
            return (member, shipTo, mode, quoted, [], quotedCategories);
        }
        var (priced, applied, categories, _) = await PriceAsync(request, lines, member?.CompanyId, mode, now, districtId, ct);
        return (member, shipTo, mode, priced, applied, categories);
    }
}
