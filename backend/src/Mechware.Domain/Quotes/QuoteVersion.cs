using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Pricing.PriceLists;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Domain.Quotes;

/// <summary>
/// One version of a <see cref="Quote"/> (ADR-0011 §3, §4, §6). Only a Draft changes, and every editor becomes a contributor.
/// Leaving Draft freezes the content (fresh line facts, company snapshot, preparer, content hash); from then on only the next
/// step's columns are written. Status moves only along <see cref="CanMove"/>; the <see cref="Quote"/> drives every move.
/// </summary>
public sealed class QuoteVersion : Entity<Guid>
{
    private static readonly HashSet<(S, S)> Allowed =
    [
        (S.Draft, S.Sent), (S.Draft, S.PendingApproval), (S.PendingApproval, S.Sent), (S.PendingApproval, S.Draft),
        (S.Sent, S.Accepted), (S.Sent, S.Rejected), (S.Sent, S.Withdrawn), (S.Sent, S.Superseded), (S.Accepted, S.Superseded),
    ];

    private readonly List<QuoteLine> _lines = [];
    private readonly List<QuoteVersionContributor> _contributors = [];

    public Guid QuoteId { get; private set; }
    public int VersionNo { get; private set; }
    public S Status { get; private set; } = S.Draft;
    public int ValidityDays { get; private set; }
    public QuotePaymentTerms PaymentTerms { get; private set; } = QuotePaymentTerms.AsAccount;
    public QuoteShippingMode ShippingMode { get; private set; } = QuoteShippingMode.Calculated;
    public Guid? ShipToSiteId { get; private set; }

    /// <summary>The address of <see cref="ShipToSiteId"/> when the draft was saved (fixed delivery only); in the content hash.</summary>
    public QuoteShipTo? ShipTo { get; private set; }

    public IReadOnlyList<QuoteShippingFee>? ShippingFees { get; private set; }
    public string? TermsText { get; private set; }
    public string? BuyerNote { get; private set; }

    /// <summary>Staff only: never shown to the buyer and not in the content hash.</summary>
    public string? InternalNote { get; private set; }

    public QuoteCompanySnapshot? CompanySnapshot { get; private set; }
    public string? PreparerName { get; private set; }

    /// <summary><see cref="QuoteContentHash"/> of the frozen content, computed by the domain when the version leaves Draft.</summary>
    public string? ContentHash { get; private set; }

    /// <summary>Staff only: the approval flags of the send, replaced by those of the approval (§5).</summary>
    public QuoteFlags? Flags { get; private set; }

    /// <summary>Version totals (<see cref="QuoteTotals"/>): kept current in a Draft, fixed at the send.</summary>
    public long GoodsNetMinor { get; private set; }

    public long TaxMinor { get; private set; }
    public long GoodsTotalMinor { get; private set; }

    /// <summary>Fixed delivery only (with a fee for every seller); null for calculated delivery.</summary>
    public long? ShippingTotalMinor { get; private set; }

    public long? GrandTotalMinor { get; private set; }

    public Guid CreatedBy { get; private set; }
    public Guid? SubmittedBy { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public string? ApprovalComment { get; private set; }
    public Guid? ApprovalRejectedBy { get; private set; }
    public DateTime? ApprovalRejectedAtUtc { get; private set; }
    public Guid? SentBy { get; private set; }
    public DateTime? SentAtUtc { get; private set; }

    /// <summary><see cref="QuoteValidity.ValidUntilUtc"/> of the send instant; exclusive.</summary>
    public DateTime? ValidUntilUtc { get; private set; }

    public Guid? AcceptedOrderId { get; private set; }
    public Guid? AcceptedBy { get; private set; }
    public DateTime? AcceptedAtUtc { get; private set; }
    public Guid? RejectedBy { get; private set; }
    public DateTime? RejectedAtUtc { get; private set; }
    public string? RejectReason { get; private set; }
    public Guid? WithdrawnBy { get; private set; }
    public DateTime? WithdrawnAtUtc { get; private set; }
    public string? WithdrawReason { get; private set; }
    public DateTime? SupersededAtUtc { get; private set; }
    public IReadOnlyCollection<QuoteLine> Lines => _lines.AsReadOnly();
    public IReadOnlyCollection<QuoteVersionContributor> Contributors => _contributors.AsReadOnly();

    private QuoteVersion() { }

    internal QuoteVersion(Guid id, Guid quoteId, int versionNo, int validityDays, Guid createdBy, DateTime nowUtc) : base(id)
    {
        if (id == Guid.Empty) throw new ValidationException("id", "A version needs an id.");
        if (createdBy == Guid.Empty) throw new ValidationException("createdBy", "The author is required.");
        (QuoteId, VersionNo, ValidityDays, CreatedBy, CreatedAtUtc) = (quoteId, versionNo, validityDays, createdBy, nowUtc);
        Contribute(createdBy, nowUtc);
    }

    public static bool CanMove(S from, S to) => Allowed.Contains((from, to));

    public static bool IsHash(string? value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>Sent and the clock has reached <see cref="ValidUntilUtc"/>. Computed, never stored (§7).</summary>
    public bool IsExpired(DateTime nowUtc) => Status == S.Sent && ValidUntilUtc is { } until && !QuoteValidity.IsValidAt(until, nowUtc);

    // ---- Draft ---------------------------------------------------------------------------------------------------------

    internal void Edit(QuoteDraft draft, Guid editorId, QuoteOptions options, DateTime nowUtc)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(draft);
        ValidateValidity(draft.ValidityDays, options);
        if (!Enum.IsDefined(draft.PaymentTerms)) throw new ValidationException("paymentTerms", "Unknown payment terms.");
        var (fees, shipTo) = ValidShipping(draft);
        var terms = Text.Optional(draft.TermsText, QuoteLimits.MaxTermsLength, "termsText");
        var buyerNote = Text.Optional(draft.BuyerNote, QuoteLimits.MaxNoteLength, "buyerNote");
        var internalNote = Text.Optional(draft.InternalNote, QuoteLimits.MaxNoteLength, "internalNote");
        if (draft.Lines is not { Count: <= QuoteLimits.MaxLinesPerVersion })
            throw new ValidationException("lines", $"A quote has at most {QuoteLimits.MaxLinesPerVersion} lines; split a larger one.");
        if (draft.Lines.DistinctBy(l => l.VariantId).Count() != draft.Lines.Count)
            throw new ValidationException("lines", "Each catalogue item appears on one line only.");
        var lines = draft.Lines.Select((l, i) => QuoteLine.From(Guid.NewGuid(), Id, i + 1, l)).ToList();
        var totals = Totals(lines, l => (l.SellerId, l.VatRate), draft.ShippingMode, fees);

        Contribute(editorId, nowUtc);
        (ValidityDays, PaymentTerms, ShippingMode, ShipToSiteId, ShipTo, ShippingFees, TermsText, BuyerNote, InternalNote) =
            (draft.ValidityDays, draft.PaymentTerms, draft.ShippingMode, draft.ShipToSiteId, shipTo, fees, terms, buyerNote, internalNote);
        _lines.Clear();
        _lines.AddRange(lines);
        ApplyTotals(totals);
    }

    /// <summary>
    /// Everything is checked first, so a refusal changes nothing; then the snapshot, totals and check figures are written and the
    /// content hash is computed from them. The status is the caller's.
    /// </summary>
    internal void Freeze(Quote quote, QuoteIssue issue, Guid submitterId, QuoteOptions options, DateTime nowUtc)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(issue);
        if (submitterId == Guid.Empty) throw new ValidationException("submittedBy", "The sender is required.");
        if (_lines.Count == 0) throw new ValidationException("lines", "A quote needs at least one line.");
        ValidateValidity(ValidityDays, options);
        _ = Text.Required(issue.Company?.Name, QuoteLimits.MaxNameLength, "company.name");
        var preparer = Text.Required(issue.PreparerName, QuoteLimits.MaxNameLength, "preparerName");
        var facts = (issue.Facts ?? []).DistinctBy(f => f.VariantId).ToDictionary(f => f.VariantId);
        if (facts.Count != (issue.Facts ?? []).Count || facts.Count != _lines.Count || _lines.Any(l => !facts.ContainsKey(l.VariantId)))
            throw new ConflictException("QUOTE_CHANGED", "The lines changed while they were checked. Check them again.");
        foreach (var l in _lines) QuoteLine.Check(facts[l.VariantId], l.VariantId, $"lines[{l.LineNo}]");
        if (ShippingMode == QuoteShippingMode.Fixed && ShipTo is null)
            throw new ValidationException("shipToSiteId", "Fixed delivery needs the site's address. Save the draft again.");
        if (ShippingMode == QuoteShippingMode.Fixed && !ShippingFees!.Select(f => f.SellerId).ToHashSet().SetEquals(facts.Values.Select(f => f.SellerId)))
            throw new ValidationException("shippingFees", "Fixed delivery needs one fee for each seller of the lines.");
        var totals = Totals(_lines, l => (facts[l.VariantId].SellerId, facts[l.VariantId].VatRate), ShippingMode, ShippingFees);
        var check = Matching(issue.Check, atApproval: false);

        foreach (var l in _lines) l.Apply(facts[l.VariantId]);
        ApplyTotals(totals);
        RecordCheck(check);
        (SubmittedBy, SubmittedAtUtc, CompanySnapshot, PreparerName, UpdatedAtUtc) = (submitterId, nowUtc, issue.Company, preparer, nowUtc);
        ContentHash = QuoteContentHash.Compute(quote, this);
    }

    /// <summary>What a policy run is bound to: this version, its validity and every line's id, variant, quantity and price.</summary>
    internal string CheckKey() =>
        string.Join("|", _lines.OrderBy(l => l.LineNo).Select(l => $"{l.Id:N}:{l.VariantId:N}:{l.Quantity}:{l.UnitPriceMinor}").Prepend($"{Id:N}:{ValidityDays}"));

    /// <summary>
    /// The check if it was computed for this content and allows the step: no line below cost (unless allowed), and every
    /// below-floor line carries its reason. A line that fell below the floor after the send cannot get one: the approver rejects.
    /// </summary>
    internal QuoteApprovalCheck Matching(QuoteApprovalCheck? check, bool atApproval)
    {
        if (check is null || check.VersionId != Id || !string.Equals(check.Key, CheckKey(), StringComparison.Ordinal))
            throw new ConflictException("QUOTE_CHANGED", "The prices were checked for other content. Check them again.");
        if (check.BelowCostLineIds.Count > 0)
            throw new ConflictException("QUOTE_BELOW_COST", "A line is below cost at this moment. Raise its price.");
        if (_lines.Any(l => l.BelowFloorReason is null && check.Flags.BelowFloorLineIds.Contains(l.Id)))
            throw atApproval
                ? new ConflictException("BELOW_FLOOR_REASON_REQUIRED", "A line is now below the margin floor and has no reason. Reject the approval.")
                : new ValidationException("belowFloorReason", "A line below the margin floor needs a written reason.");
        return check;
    }

    /// <summary>The author of this version, its submitter or anyone who changed it (four-eyes, §5).</summary>
    internal bool IsAuthoredBy(Guid userId) => userId == CreatedBy || userId == SubmittedBy || _contributors.Any(c => c.UserId == userId);

    /// <summary>Draft n + 1 with this version's header, notes and lines (new line ids); the reviser is its only contributor.</summary>
    internal QuoteVersion CopyAsDraft(Guid id, int versionNo, Guid reviserId, DateTime nowUtc)
    {
        var next = new QuoteVersion(id, QuoteId, versionNo, ValidityDays, reviserId, nowUtc)
        {
            PaymentTerms = PaymentTerms,
            ShippingMode = ShippingMode,
            ShipToSiteId = ShipToSiteId,
            ShipTo = ShipTo,
            ShippingFees = ShippingFees?.ToList(),
            TermsText = TermsText,
            BuyerNote = BuyerNote,
            InternalNote = InternalNote,
            GoodsNetMinor = GoodsNetMinor,
            TaxMinor = TaxMinor,
            GoodsTotalMinor = GoodsTotalMinor,
            ShippingTotalMinor = ShippingTotalMinor,
            GrandTotalMinor = GrandTotalMinor,
        };
        next._lines.AddRange(_lines.Select(l => l.CopyTo(id)));
        return next;
    }

    // ---- Steps (each validates its input before it moves) -----------------------------------------------------------

    internal void MarkSent(Guid senderId, DateTime nowUtc)
    {
        Move(S.Sent, nowUtc);
        (SentBy, SentAtUtc, ValidUntilUtc) = (senderId, nowUtc, QuoteValidity.ValidUntilUtc(nowUtc, ValidityDays));
    }

    internal void MoveToApproval(DateTime nowUtc) => Move(S.PendingApproval, nowUtc);

    /// <summary>The approval's own policy run replaces the check figures and flags of the send (the only content-free write, §6).</summary>
    internal void RecordApproval(Guid approverId, string? comment, QuoteApprovalCheck check, DateTime nowUtc)
    {
        RecordCheck(check);
        (ApprovedBy, ApprovedAtUtc, ApprovalComment) = (approverId, nowUtc, comment);
    }

    /// <summary>PendingApproval → Draft: a rejected approval (with its comment) or a recall. A new send freezes it again.</summary>
    internal void BackToDraft(Guid? rejectedBy, string? comment, DateTime nowUtc)
    {
        Move(S.Draft, nowUtc);
        (ContentHash, SubmittedBy, SubmittedAtUtc, Flags) = (null, null, null, null);
        if (rejectedBy is not null) (ApprovalRejectedBy, ApprovalRejectedAtUtc, ApprovalComment) = (rejectedBy, nowUtc, comment);
    }

    internal void RecordAcceptance(Guid orderId, Guid acceptedBy, DateTime nowUtc)
    {
        if (Status != S.Accepted) Move(S.Accepted, nowUtc);
        (AcceptedOrderId, AcceptedBy, AcceptedAtUtc, UpdatedAtUtc) = (orderId, acceptedBy, nowUtc, nowUtc);
    }

    internal void Reject(Guid rejectedBy, string? reason, DateTime nowUtc)
    {
        var why = Text.Optional(reason, QuoteLimits.MaxRejectReasonLength, "reason");
        Move(S.Rejected, nowUtc);
        (RejectedBy, RejectedAtUtc, RejectReason) = (rejectedBy, nowUtc, why);
    }

    internal void Withdraw(Guid withdrawnBy, string reason, DateTime nowUtc)
    {
        var why = Text.Reason(reason);
        Move(S.Withdrawn, nowUtc);
        (WithdrawnBy, WithdrawnAtUtc, WithdrawReason) = (withdrawnBy, nowUtc, why);
    }

    internal void Supersede(DateTime nowUtc)
    {
        Move(S.Superseded, nowUtc);
        SupersededAtUtc = nowUtc;
    }

    internal ConflictException Refused(S to) => new("QUOTE_INVALID_TRANSITION", $"A {Status} quote version cannot become {to}.");

    private void Move(S to, DateTime nowUtc)
    {
        if (!CanMove(Status, to)) throw Refused(to);
        (Status, UpdatedAtUtc) = (to, nowUtc);
    }

    private void EnsureDraft()
    {
        if (Status != S.Draft)
            throw new ConflictException("QUOTE_NOT_EDITABLE", $"A {Status} quote version cannot be changed. Revise the quote to make a new version.");
    }

    private void Contribute(Guid editorId, DateTime nowUtc)
    {
        if (editorId == Guid.Empty) throw new ValidationException("editor", "The editor is required.");
        if (!_contributors.Any(c => c.UserId == editorId)) _contributors.Add(new QuoteVersionContributor(Id, editorId, nowUtc));
        UpdatedAtUtc = nowUtc;
    }

    private static QuoteVersionTotals Totals(IEnumerable<QuoteLine> lines, Func<QuoteLine, (Guid Seller, decimal Vat)> facts, QuoteShippingMode mode,
        IReadOnlyList<QuoteShippingFee>? fees) =>
        QuoteTotals.Compute(lines.Select(l =>
        {
            var (seller, vat) = facts(l);
            return new QuoteTotalsLine(l.Id, l.VariantId, seller, l.Quantity, l.UnitPriceMinor, vat);
        }).ToList(), mode, fees);

    private void ApplyTotals(QuoteVersionTotals t)
    {
        foreach (var l in _lines) l.ApplyTotals(t.Lines[l.Id]);
        (GoodsNetMinor, TaxMinor, GoodsTotalMinor, ShippingTotalMinor, GrandTotalMinor) =
            (t.GoodsNetMinor, t.TaxMinor, t.GoodsTotalMinor, t.ShippingTotalMinor, t.GrandTotalMinor);
    }

    private void RecordCheck(QuoteApprovalCheck check)
    {
        var byLine = check.Lines.ToDictionary(c => c.LineId);
        foreach (var l in _lines) l.RecordCheck(byLine[l.Id]);
        Flags = check.Flags;
    }

    private static void ValidateValidity(int days, QuoteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (days < 1 || days > Math.Min(options.MaxValidityDays, QuoteOptions.HardMaxValidityDays))
            throw new ValidationException("validityDays", $"A quote is valid for 1 to {options.MaxValidityDays} days.");
    }

    private static (List<QuoteShippingFee>? Fees, QuoteShipTo? ShipTo) ValidShipping(QuoteDraft d)
    {
        if (!Enum.IsDefined(d.ShippingMode)) throw new ValidationException("shippingMode", "Unknown shipping mode.");
        if (d.ShippingMode == QuoteShippingMode.Calculated)
            return d.ShipToSiteId is null && d.ShipTo is null && d.ShippingFees is not { Count: > 0 } ? (null, null)
                : throw new ValidationException("shippingMode", "Calculated delivery has no site and no fees; the buyer chooses at acceptance.");
        if (d.ShipToSiteId is not { } site || site == Guid.Empty || d.ShipTo is null || d.ShippingFees is not { Count: > 0 } fees)
            throw new ValidationException("shipToSiteId", "Fixed delivery needs the company site and the fees.");
        if (fees.Any(f => f.SellerId == Guid.Empty || f.ShippingMinor is < 0 or > PriceListLimits.MaxUnitPriceMinor)
            || fees.DistinctBy(f => f.SellerId).Count() != fees.Count)
            throw new ValidationException("shippingFees", "One fee of zero or more per seller.");
        return ([.. fees], ValidShipTo(d.ShipTo));
    }

    /// <summary>The site copy as plain text within the company-site limits (QT17), so the hash and the PDF see exactly what is stored.</summary>
    private static QuoteShipTo ValidShipTo(QuoteShipTo a) => new(
        Text.Required(a.SiteName, QuoteLimits.MaxSiteNameLength, "shipTo.siteName"),
        Text.Required(a.RecipientName, QuoteLimits.MaxRecipientLength, "shipTo.recipientName"),
        Text.Required(a.Phone, QuoteLimits.MaxPhoneLength, "shipTo.phone"),
        Text.Required(a.Line1, QuoteLimits.MaxAddressLineLength, "shipTo.line1"),
        Text.Optional(a.Line2, QuoteLimits.MaxAddressLineLength, "shipTo.line2"),
        Text.Required(a.Upazila, QuoteLimits.MaxPlaceLength, "shipTo.upazila"),
        Text.Required(a.District, QuoteLimits.MaxPlaceLength, "shipTo.district"),
        Text.Required(a.Division, QuoteLimits.MaxPlaceLength, "shipTo.division"),
        Text.Optional(a.PostalCode, QuoteLimits.MaxPostalCodeLength, "shipTo.postalCode"));
}

/// <summary>
/// One line of a version (§4): a catalogue variant, a quantity and an absolute VAT-exclusive unit price in whole poisha, plus
/// the snapshot of the variant written by the server, the line's money (<see cref="QuoteTotals"/>) and the staff-only check
/// figures of the last policy run (<see cref="QuoteApprovalPolicy"/>), which are never in the content hash.
/// </summary>
public sealed class QuoteLine : Entity<Guid>
{
    public Guid VersionId { get; private set; }
    public int LineNo { get; private set; }
    public Guid VariantId { get; private set; }
    public Guid? RfqLineId { get; private set; }
    public int Quantity { get; private set; }
    public long UnitPriceMinor { get; private set; }
    public string? LineNote { get; private set; }
    public string? BelowFloorReason { get; private set; }
    public Guid SellerId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string? PartNumber { get; private set; }
    public string TitleEn { get; private set; } = string.Empty;
    public string TitleBn { get; private set; } = string.Empty;
    public decimal VatRate { get; private set; }
    public long PublicUnitPriceMinor { get; private set; }
    public long LineNetMinor { get; private set; }
    public long LineTaxMinor { get; private set; }
    public long LineTotalMinor { get; private set; }

    // Staff-only check figures (§4); cost and floor are shown only with pricing.view_cost.
    public long? ReferenceUnitPriceMinor { get; private set; }
    public PriceSource? ReferenceSource { get; private set; }
    public long? CostMinorAtCheck { get; private set; }
    public long? FloorMinorAtCheck { get; private set; }
    public bool BelowFloor { get; private set; }
    public bool OverAuthority { get; private set; }

    private QuoteLine() { }

    internal static QuoteLine From(Guid id, Guid versionId, int lineNo, QuoteLineDraft d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var field = $"lines[{lineNo}]";
        if (d.VariantId == Guid.Empty) throw new ValidationException(field, "Choose a catalogue item.");
        if (d.Quantity is < 1 or > QuoteLimits.MaxQuantityPerLine)
            throw new ValidationException(field, $"Quantity must be 1 to {QuoteLimits.MaxQuantityPerLine}.");
        if (d.UnitPriceMinor is < 1 or > PriceListLimits.MaxUnitPriceMinor)
            throw new ValidationException(field, "The unit price must be positive and not above the maximum unit price.");
        Check(d.Facts, d.VariantId, field);
        var line = new QuoteLine
        {
            Id = id,
            VersionId = versionId,
            LineNo = lineNo,
            VariantId = d.VariantId,
            RfqLineId = d.RfqLineId,
            Quantity = d.Quantity,
            UnitPriceMinor = d.UnitPriceMinor,
            LineNote = Text.Optional(d.LineNote, QuoteLimits.MaxLineNoteLength, field),
            BelowFloorReason = string.IsNullOrWhiteSpace(d.BelowFloorReason) ? null : Text.Reason(d.BelowFloorReason),
        };
        line.Apply(d.Facts);
        return line;
    }

    /// <summary>The same rules at draft and at send: an active variant, an orderable quantity, a complete snapshot.</summary>
    internal static void Check(QuoteLineFacts? f, Guid variantId, string field)
    {
        if (f is null || f.VariantId != variantId) throw new ValidationException(field, "The catalogue facts are of another item.");
        if (!f.Active) throw new ConflictException("VARIANT_UNAVAILABLE", $"{f.Sku} is no longer sold.");
        if (f.QuantityProblem is { } problem) throw new ValidationException(field, problem);
        if (f.SellerId == Guid.Empty) throw new ValidationException(field, "The seller is required.");
        _ = Text.Required(f.Sku, QuoteLimits.MaxSkuLength, field);
        _ = Text.Optional(f.PartNumber, QuoteLimits.MaxSkuLength, field);
        _ = Text.Required(f.TitleEn, QuoteLimits.MaxTitleLength, field);
        _ = Text.Required(f.TitleBn, QuoteLimits.MaxTitleLength, field);
        if (f.VatRate is < 0 or > 1 || decimal.Round(f.VatRate, 4) != f.VatRate)
            throw new ValidationException(field, "The VAT rate is 0 to 1 with at most four decimals.");
        if (f.PublicUnitPriceMinor is < 1 or > PriceListLimits.MaxUnitPriceMinor)
            throw new ValidationException(field, "The item needs a public price.");
    }

    internal void Apply(QuoteLineFacts f) =>
        (SellerId, Sku, PartNumber, TitleEn, TitleBn, VatRate, PublicUnitPriceMinor) =
        (f.SellerId, f.Sku.Trim(), Text.Optional(f.PartNumber, QuoteLimits.MaxSkuLength, "partNumber"), f.TitleEn.Trim(), f.TitleBn.Trim(),
            f.VatRate, f.PublicUnitPriceMinor);

    internal void ApplyTotals(QuoteLineTotals t) => (LineNetMinor, LineTaxMinor, LineTotalMinor) = (t.NetMinor, t.TaxMinor, t.TotalMinor);

    internal void RecordCheck(QuoteLineCheck c) =>
        (ReferenceUnitPriceMinor, ReferenceSource, CostMinorAtCheck, FloorMinorAtCheck, BelowFloor, OverAuthority) =
        (c.ReferenceUnitPriceMinor, c.ReferenceSource, c.CostMinor, c.FloorMinor, c.BelowFloor, c.OverAuthority);

    /// <summary>The same line for a new Draft; the old check figures are not carried over (a new send checks again).</summary>
    internal QuoteLine CopyTo(Guid versionId)
    {
        var copy = (QuoteLine)MemberwiseClone();
        (copy.Id, copy.VersionId) = (Guid.NewGuid(), versionId);
        (copy.ReferenceUnitPriceMinor, copy.ReferenceSource, copy.CostMinorAtCheck, copy.FloorMinorAtCheck, copy.BelowFloor, copy.OverAuthority) =
            (null, null, null, null, false, false);
        return copy;
    }
}

/// <summary>A user who created or changed a version (§5); none of them may approve it (<see cref="Quote.Approve"/>; DB trigger in D4.4).</summary>
public sealed class QuoteVersionContributor
{
    public Guid VersionId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime FirstContributedAtUtc { get; private set; }

    private QuoteVersionContributor() { }

    internal QuoteVersionContributor(Guid versionId, Guid userId, DateTime atUtc) =>
        (VersionId, UserId, FirstContributedAtUtc) = (versionId, userId, atUtc);
}
