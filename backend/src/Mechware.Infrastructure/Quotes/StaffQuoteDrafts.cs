using Microsoft.EntityFrameworkCore;
using Mechware.Application.Pricing;
using Mechware.Application.Quotes;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// The draft editor and its price preview (ADR-0011 §4, §5, §9, §12; D4.7, threat model QT3, QT6, QT7). Both turn the request into the
/// domain's <see cref="QuoteDraft"/> the same way: catalogue facts and the public price of now, the buyer's own resolver price for the
/// quote's company (Exclusive, net), and a copy of the named company site. The save then locks <c>quotes</c> → <c>quote_versions</c>
/// FOR NO KEY UPDATE, compares the version's <c>xmin</c> with the editor's <c>rowVersion</c> (409 <c>QUOTE_VERSION_CHANGED</c>), and lets
/// the domain refuse a conflicted editor, a version that is no longer a Draft and every content rule; the editor becomes a contributor.
/// The preview applies the same edit to an untracked copy and runs <see cref="QuoteApprovalPolicy"/> on it: nothing is written.
/// </summary>
public sealed partial class StaffQuoteService
{
    public async Task<StaffQuoteDetailDto> EditDraftAsync(Guid quoteId, int versionNo, EditQuoteDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RowVersion is not { } rowVersion) throw new ValidationException("rowVersion", "Send the rowVersion of the version you edited.");
        var (actor, now) = (Actor, Now);
        var staff = await StaffAsync(actor, ct);
        var head = await db.Quotes.AsNoTracking().Where(q => q.Id == quoteId).Select(q => new { q.CompanyId, q.RfqId })
            .FirstOrDefaultAsync(ct) ?? throw QuoteNotFound();
        var (draft, _) = await DraftOfAsync(head.CompanyId, head.RfqId, request, now, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await LockQuoteAsync(quoteId, ct);
        var version = quote.Version(versionNo);
        if (db.Entry(version).Property<uint>("xmin").CurrentValue != rowVersion)
            throw new ConflictException("QUOTE_VERSION_CHANGED", "Someone changed this version since you loaded it. Reload it and edit again.");
        // A new contact must be an effective Buyer or Admin member of the quote's company (QT6); the domain decides whether it may change.
        if (request.ContactUserId is { } chosen && chosen != quote.ContactUserId) await ContactAsync(quote.CompanyId, chosen, null, ct);
        var before = new { quote.ContactUserId, version.ValidityDays, Lines = version.Lines.Count, version.GoodsNetMinor, version.GrandTotalMinor };
        if (request.ContactUserId is { } contact) quote.ChangeContact(contact, staff, now);
        quote.EditDraft(versionNo, draft, staff, options, now);   // QT6 conflict of interest, Draft only, every content rule; contributor
        Audit("quote.draft_changed", "quotes", quote.Id, before, new
        {
            quote.Number,
            version.VersionNo,
            quote.ContactUserId,
            version.ValidityDays,
            PaymentTerms = version.PaymentTerms.ToString(),
            ShippingMode = version.ShippingMode.ToString(),
            version.ShipToSiteId,
            Lines = version.Lines.Count,
            version.GoodsNetMinor,
            version.GrandTotalMinor,
        }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quoteId, ct);
    }

    public async Task<QuotePricePreviewDto> PreviewAsync(Guid quoteId, int versionNo, EditQuoteDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (actor, now) = (Actor, Now);
        var staff = await StaffAsync(actor, ct);
        var quote = await Loaded(db.Quotes.AsNoTracking()).FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw QuoteNotFound();
        var (draft, references) = await DraftOfAsync(quote.CompanyId, quote.RfqId, request, now, ct);
        quote.EditDraft(versionNo, draft, staff, options, now);   // on the untracked copy: the save's own rules, nothing is written
        var version = quote.Version(versionNo);
        var facts = await checks.ForVariantsAsync([.. version.Lines.Select(l => l.VariantId)], ct);
        var check = QuoteApprovalPolicy.Evaluate(version, facts.Costs, facts.Floors, references, options, facts.AllowBelowCost);
        return StaffQuoteViews.Preview(version, check, user.HasPermission(Permissions.PricingViewCost));
    }

    /// <summary>
    /// The domain draft of a request: per line the catalogue facts of now (active, seller, SKU, part number, titles, VAT rate, MOQ/pack
    /// problem) and the public price, both from one resolver pass for the quote's company, whose answers are also the references the
    /// policy measures discounts against (the buyer's own price now, never a previous version); a line asking for an RFQ line must name one
    /// of the quote's own RFQ; a fixed delivery's site must be the company's and is copied. Prices are the editor's; nothing here is saved.
    /// </summary>
    private async Task<(QuoteDraft Draft, IReadOnlyList<PriceResolution> References)> DraftOfAsync(Guid companyId, Guid? rfqId, EditQuoteDraftRequest r,
        DateTime now, CancellationToken ct)
    {
        var lines = r.Lines ?? [];
        if (lines.Count > QuoteLimits.MaxLinesPerVersion)
            throw new ValidationException("lines", $"A quote has at most {QuoteLimits.MaxLinesPerVersion} lines; split a larger one.");
        if (lines.Any(l => l is null) || r.ShippingFees?.Any(f => f is null) == true) throw new ValidationException("lines", "A line or a fee is empty.");
        var ids = lines.Select(l => l.VariantId).Distinct().ToList();
        var catalogue = await (from v in db.ProductVariants.AsNoTracking()
                               join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                               join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                               where ids.Contains(v.Id)
                               select new { Variant = v, Product = p, c.VatRate }).ToDictionaryAsync(x => x.Variant.Id, ct);
        for (var i = 0; i < lines.Count; i++)
            if (!catalogue.ContainsKey(lines[i].VariantId)) throw new ValidationException($"lines[{i + 1}]", "Choose a catalogue item.");
        var asked = lines.Where(l => l.RfqLineId is not null).Select(l => l.RfqLineId!.Value).Distinct().ToList();
        if (asked.Count > 0 && (rfqId is null || await db.Set<RfqLine>().CountAsync(x => x.RfqId == rfqId && asked.Contains(x.Id), ct) != asked.Count))
            throw new ValidationException("rfqLineId", "A line can answer only a line of this quote's own request.");

        // Only lines the domain could accept are priced (an inactive item or a quantity out of range is refused by the domain first).
        bool Active(Guid variant) => catalogue[variant] is var c && c.Variant.Status == VariantStatus.Active && c.Product.Status == ProductStatus.Active
                                     && c.Product.DeletedAtUtc == null;
        var priced = lines.Where(l => Active(l.VariantId) && l.Quantity is >= 1 and <= QuoteLimits.MaxQuantityPerLine).DistinctBy(l => l.VariantId).ToList();
        var resolved = priced.Count == 0 ? [] : await prices.ResolveAsync(companyId, [.. priced.Select(l => new PriceRequestLine(l.VariantId, l.Quantity,
            PricingInput.From(catalogue[l.VariantId].Variant, catalogue[l.VariantId].VatRate)))], TaxMode.Exclusive, now, ct);
        var price = priced.Zip(resolved).ToDictionary(x => x.First.VariantId, x => x.Second.Price);

        var drafts = lines.Select(l =>
        {
            var c = catalogue[l.VariantId];
            var facts = new QuoteLineFacts(l.VariantId, Active(l.VariantId), c.Product.SellerId, c.Variant.Sku, c.Variant.ManufacturerPartNumber,
                c.Product.TitleEn, string.IsNullOrWhiteSpace(c.Product.TitleBn) ? c.Product.TitleEn : c.Product.TitleBn, c.VatRate,
                price.TryGetValue(l.VariantId, out var p) ? p.PublicUnitPriceMinor : 0,
                l.Quantity is >= 1 and <= QuoteLimits.MaxQuantityPerLine ? c.Variant.QuantityProblem(l.Quantity) : null);
            return new QuoteLineDraft(l.VariantId, l.Quantity, l.UnitPriceMinor, facts, l.LineNote, l.BelowFloorReason, l.RfqLineId);
        }).ToList();

        QuoteShipTo? shipTo = null;
        if (r.ShipToSiteId is { } siteId)
        {
            var site = await db.CompanySites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId, ct)
                       ?? throw new ValidationException("shipToSiteId", "Choose a delivery site of this company.");
            shipTo = new QuoteShipTo(site.Name, site.ContactName, site.ContactPhone, site.Line1, site.Line2, site.Upazila, site.District, site.Division,
                site.PostalCode);
        }

        var draft = new QuoteDraft(r.ValidityDays,
            string.IsNullOrWhiteSpace(r.PaymentTerms) ? QuotePaymentTerms.AsAccount : Parse<QuotePaymentTerms>(r.PaymentTerms.Trim(), "paymentTerms"),
            string.IsNullOrWhiteSpace(r.ShippingMode) ? QuoteShippingMode.Calculated : Parse<QuoteShippingMode>(r.ShippingMode.Trim(), "shippingMode"),
            r.ShipToSiteId, r.ShippingFees?.Select(f => new QuoteShippingFee(f.SellerId, f.ShippingMinor)).ToList(), r.TermsText, r.BuyerNote, r.InternalNote,
            drafts, shipTo);
        return (draft, [.. price.Values]);
    }
}
