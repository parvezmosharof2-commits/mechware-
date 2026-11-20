using Mechware.Application.Quotes;
using Mechware.Domain.Quotes;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// The staff view of a version (ADR-0011 §4, §13): everything the desk needs, drafts and internal notes included, but the cost and
/// floor figures only with <c>pricing.view_cost</c> (without it the <c>cost</c> object is not in the JSON at all; Support sees the
/// flags, not the numbers). Built field by field.
/// </summary>
internal static class StaffQuoteViews
{
    /// <summary>The snake_case wire name of an enum value (<c>PendingApproval</c> → <c>pending_approval</c>).</summary>
    public static string Wire<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    public static StaffQuoteVersionDto Version(QuoteVersion v, uint rowVersion, DateTime nowUtc, bool canSeeCost) => new(
        v.Id, v.VersionNo, v.IsExpired(nowUtc) ? "expired" : Wire(v.Status), rowVersion, v.ValidityDays, Wire(v.PaymentTerms), Wire(v.ShippingMode),
        v.ShipToSiteId, v.ShipTo, v.TermsText, v.BuyerNote, v.InternalNote, v.PreparerName, v.ContentHash,
        v.Flags is { } f ? new StaffQuoteFlagsDto(f.BelowFloorLineIds, f.OverAuthorityLineIds, f.LongValidity) : null,
        v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor, v.ShippingTotalMinor, v.GrandTotalMinor, v.CreatedBy,
        [.. v.Contributors.OrderBy(c => c.FirstContributedAtUtc).Select(c => c.UserId)],
        v.SubmittedBy, v.SubmittedAtUtc, v.ApprovedBy, v.ApprovedAtUtc, v.ApprovalComment, v.ApprovalRejectedBy, v.ApprovalRejectedAtUtc,
        v.SentBy, v.SentAtUtc, v.ValidUntilUtc, v.AcceptedOrderId, v.AcceptedAtUtc, v.RejectedAtUtc, v.RejectReason, v.WithdrawnBy, v.WithdrawnAtUtc,
        v.WithdrawReason, v.SupersededAtUtc, v.CreatedAtUtc,
        [.. v.Lines.OrderBy(l => l.LineNo).Select(l => new StaffQuoteLineDto(l.Id, l.LineNo, l.VariantId, l.RfqLineId, l.Sku, l.PartNumber, l.TitleEn,
            l.TitleBn, l.Quantity, l.UnitPriceMinor, l.VatRate, l.LineNetMinor, l.LineTaxMinor, l.LineTotalMinor, l.PublicUnitPriceMinor, l.LineNote,
            l.BelowFloorReason, l.ReferenceUnitPriceMinor, l.ReferenceSource is { } s ? Wire(s) : null, l.BelowFloor, l.OverAuthority,
            canSeeCost ? new StaffQuoteLineCostDto(l.CostMinorAtCheck, l.FloorMinorAtCheck) : null))]);

    /// <summary>A price preview (D4.7) of an edited Draft and the policy run on it; the same cost rule as <see cref="Version"/>.</summary>
    public static QuotePricePreviewDto Preview(QuoteVersion v, QuoteApprovalCheck check, bool canSeeCost)
    {
        var byLine = check.Lines.ToDictionary(c => c.LineId);
        var lines = v.Lines.OrderBy(l => l.LineNo).Select(l => (Line: l, Check: byLine[l.Id])).ToList();
        List<int> Nos(Func<QuoteLine, QuoteLineCheck, bool> where) => [.. lines.Where(x => where(x.Line, x.Check)).Select(x => x.Line.LineNo)];
        var belowCost = Nos((_, c) => check.BelowCostLineIds.Contains(c.LineId));
        var reasons = Nos((l, c) => c.BelowFloor && l.BelowFloorReason is null);
        var problems = new List<string>();
        if (lines.Count == 0) problems.Add("NO_LINES");
        if (v.ShippingMode == QuoteShippingMode.Fixed && v.GrandTotalMinor is null) problems.Add("SHIPPING_FEES_INCOMPLETE");
        if (belowCost.Count > 0) problems.Add("QUOTE_BELOW_COST");
        if (reasons.Count > 0) problems.Add("BELOW_FLOOR_REASON_REQUIRED");
        return new(v.VersionNo, problems.Count > 0 ? "refused" : check.Flags.NeedsApproval ? "approval" : "send", problems,
            Nos((_, c) => c.BelowFloor), Nos((_, c) => c.OverAuthority), check.Flags.LongValidity, belowCost, reasons,
            v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor, v.ShippingTotalMinor, v.GrandTotalMinor,
            [.. lines.Select(x => new QuotePreviewLineDto(x.Line.LineNo, x.Line.VariantId, x.Line.Sku, x.Line.TitleEn, x.Line.TitleBn, x.Line.Quantity,
                x.Line.UnitPriceMinor, x.Line.VatRate, x.Line.LineNetMinor, x.Line.LineTaxMinor, x.Line.LineTotalMinor, x.Line.PublicUnitPriceMinor,
                x.Check.ReferenceUnitPriceMinor, Wire(x.Check.ReferenceSource), x.Check.BelowFloor, x.Check.OverAuthority, x.Check.BelowCost,
                x.Check.BelowFloor && x.Line.BelowFloorReason is null, canSeeCost ? new StaffQuoteLineCostDto(x.Check.CostMinor, x.Check.FloorMinor) : null))]);
    }
}
