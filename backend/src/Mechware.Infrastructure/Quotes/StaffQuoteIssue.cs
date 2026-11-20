using Microsoft.EntityFrameworkCore;
using Mechware.Application.Pricing;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// Send, approve, reject approval, recall and withdraw (ADR-0011 §3, §5, §7, §9, §12, §14; D4.8, threat model QT3, QT5, QT6, QT7). Every
/// command locks <c>quotes</c> → <c>quote_versions</c> (by version number) FOR NO KEY UPDATE and reads the aggregate under those locks; a
/// command that sends then locks the RFQ row (§9: <c>rfqs</c> after <c>quote_versions</c>) to mark it answered. The policy of
/// <see cref="QuoteApprovalPolicy"/> runs inside the transaction with the cost, floors and the buyer's own resolver price of that moment,
/// at the send and again at the approval; the domain decides everything else (conflict of interest, four-eyes, hash, acknowledgement,
/// supersede, validity from the instant it is sent). One audit row per command, outbox events from the domain, one commit.
/// </summary>
public sealed partial class StaffQuoteService
{
    public async Task<StaffQuoteSendDto> SendAsync(Guid quoteId, int versionNo, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        var staff = await StaffAsync(actor, ct);
        var preparer = await db.Users.AsNoTracking().Where(u => u.Id == actor).Select(u => u.FullName).SingleAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await LockQuoteAsync(quoteId, ct);
        var v = quote.Version(versionNo);
        var live = quote.LiveVersion?.VersionNo;
        var company = await CompanySnapshotAsync(quote.CompanyId, ct);
        var (check, facts) = await CheckNowAsync(quote.CompanyId, v, now, ct);
        var shipTo = v.ShipToSiteId is { } siteId && v.ShippingMode == QuoteShippingMode.Fixed ? await ShipToAsync(quote.CompanyId, siteId, ct) : null;
        var status = quote.Send(versionNo, new QuoteIssue(company, preparer, facts, check, shipTo, await QuoteTermsService.SnapshotAsync(db, ct)), staff, await LapsedAsync(quote, ct), options, now);
        if (status == S.Sent) await AnsweredAsync(quote, now, ct);
        var flags = v.Flags!;
        Audit(status == S.Sent ? "quote.sent" : "quote.submitted_for_approval", "quotes", quote.Id, new { VersionNo = versionNo, Status = nameof(S.Draft) }, new
        {
            quote.Number,
            v.VersionNo,
            Status = v.Status.ToString(),
            v.ContentHash,
            v.ValidUntilUtc,
            SupersededVersionNo = status == S.Sent ? live : null,
            BelowFloorLines = flags.BelowFloorLineIds.Count,
            OverAuthorityLines = flags.OverAuthorityLineIds.Count,
            flags.LongValidity,
        }, actor, now);
        await SaveAsync(tx, ct);
        return new StaffQuoteSendDto(StaffQuoteViews.Wire(status), FlagsDto(flags), await GetQuoteAsync(quoteId, ct));
    }

    public async Task<StaffQuoteDetailDto> ApproveAsync(Guid quoteId, int versionNo, ApproveQuoteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ContentHash)) throw new ValidationException("contentHash", "Send the content hash of the version you reviewed.");
        var (belowFloor, overAuthority) = (request.BelowFloorLineIds ?? [], request.OverAuthorityLineIds ?? []);
        if (belowFloor.Count > QuoteLimits.MaxLinesPerVersion || overAuthority.Count > QuoteLimits.MaxLinesPerVersion)
            throw new ValidationException("belowFloorLineIds", $"A quote has at most {QuoteLimits.MaxLinesPerVersion} lines.");
        var (actor, now) = (Actor, Now);
        var staff = await StaffAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The locks first: a second approval (or a recall, a rejection, a close) waits here and then finds the version no longer pending.
        var quote = await LockQuoteAsync(quoteId, ct);
        var v = quote.Version(versionNo);
        var live = quote.LiveVersion?.VersionNo;
        _ = await CompanySnapshotAsync(quote.CompanyId, ct);   // an approval sends: only to an active company
        var (check, _) = await CheckNowAsync(quote.CompanyId, v, now, ct);
        var approval = new QuoteApproval(user.HasPermission(Permissions.QuotesApproveBelowFloor), request.ContentHash, belowFloor, overAuthority, request.Comment);
        try
        {
            quote.Approve(versionNo, staff, approval, check, await LapsedAsync(quote, ct), now);
        }
        catch (ConflictException ex) when (ex.Code == "QUOTE_FLAGS_NOT_ACKNOWLEDGED")
        {
            // Line ids only, so an approver without pricing.view_cost learns which lines, never an amount.
            throw new ConflictException(ex.Code, $"Acknowledge exactly the lines flagged now. Below the floor: [{string.Join(", ", check.Flags.BelowFloorLineIds)}]; " +
                $"over the discount authority: [{string.Join(", ", check.Flags.OverAuthorityLineIds)}].");
        }

        await AnsweredAsync(quote, now, ct);
        Audit("quote.approved", "quotes", quote.Id, new { VersionNo = versionNo, Status = nameof(S.PendingApproval) }, new
        {
            quote.Number,
            v.VersionNo,
            Status = v.Status.ToString(),
            v.ContentHash,
            v.ValidUntilUtc,
            SupersededVersionNo = live,
            BelowFloorAcknowledged = check.Flags.BelowFloorLineIds.Count,
            OverAuthorityAcknowledged = check.Flags.OverAuthorityLineIds.Count,
            check.Flags.LongValidity,
            v.ApprovalComment,
        }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quoteId, ct);
    }

    public Task<StaffQuoteDetailDto> RejectApprovalAsync(Guid quoteId, int versionNo, StaffCommentRequest request, CancellationToken ct = default) =>
        BackToDraftAsync(quoteId, versionNo, "quote.approval_rejected", (quote, actor, now) => quote.RejectApproval(versionNo, actor, request?.Comment ?? string.Empty, now), ct);

    public Task<StaffQuoteDetailDto> RecallAsync(Guid quoteId, int versionNo, CancellationToken ct = default) =>
        BackToDraftAsync(quoteId, versionNo, "quote.recalled", (quote, actor, now) => quote.Recall(versionNo, actor, now), ct);

    public async Task<StaffQuoteDetailDto> WithdrawAsync(Guid quoteId, int versionNo, StaffReasonRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await LockQuoteAsync(quoteId, ct);
        var v = quote.Version(versionNo);
        quote.Withdraw(versionNo, actor, request?.Reason ?? string.Empty, now);   // Sent only, reason 5-500; outbox QuoteWithdrawnEvent
        Audit("quote.withdrawn", "quotes", quote.Id, new { VersionNo = versionNo, Status = nameof(S.Sent) },
            new { quote.Number, v.VersionNo, Status = v.Status.ToString(), v.ContentHash, v.WithdrawReason }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quoteId, ct);
    }

    public async Task<StaffApprovalPage> ListApprovalsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var (actor, p, size) = (Actor, Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
        var rows = from v in db.QuoteVersions.AsNoTracking()
                   join q in db.Quotes.AsNoTracking() on v.QuoteId equals q.Id
                   join c in db.Companies.AsNoTracking() on q.CompanyId equals c.Id
                   where v.Status == S.PendingApproval && q.ClosedAtUtc == null
                   select new { V = v, q.Number, q.CompanyId, c.LegalName, Lines = v.Lines.Count(), Authors = v.Contributors.Select(x => x.UserId).ToList() };
        var total = await rows.CountAsync(ct);
        var found = await rows.OrderBy(x => x.V.SubmittedAtUtc).ThenBy(x => x.V.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(found.Select(x => x.V.SubmittedBy), ct);
        var mine = await BuyerQuoteAccess.CompanyIdsAsync(db, actor, ct);
        var belowFloorAllowed = user.HasPermission(Permissions.QuotesApproveBelowFloor);
        return new StaffApprovalPage([.. found.Select(x =>
        {
            var flags = x.V.Flags ?? QuoteFlags.None;
            var authored = x.V.CreatedBy == actor || x.V.SubmittedBy == actor || x.Authors.Contains(actor);
            return new StaffApprovalItemDto(x.V.QuoteId, x.Number, x.V.VersionNo, x.CompanyId, x.LegalName, x.V.SubmittedBy!.Value,
                names.GetValueOrDefault(x.V.SubmittedBy!.Value, string.Empty), x.V.SubmittedAtUtc!.Value, x.Lines, x.V.ValidityDays, x.V.GoodsNetMinor,
                x.V.GoodsTotalMinor, x.V.GrandTotalMinor, FlagsDto(flags),
                !authored && !mine.Contains(x.CompanyId) && (flags.BelowFloorLineIds.Count == 0 || belowFloorAllowed));
        })], p, size, total);
    }

    /// <summary>PendingApproval → Draft (§3): the version is unfrozen (hash, submitter and flags cleared); nothing becomes binding, so no step-up.</summary>
    private async Task<StaffQuoteDetailDto> BackToDraftAsync(Guid quoteId, int versionNo, string action, Action<Quote, Guid, DateTime> step, CancellationToken ct)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await LockQuoteAsync(quoteId, ct);
        var v = quote.Version(versionNo);
        var hash = v.ContentHash;
        step(quote, actor, now);   // PendingApproval only (409 QUOTE_INVALID_TRANSITION otherwise); a rejection needs a comment of 5-500
        Audit(action, "quotes", quote.Id, new { VersionNo = versionNo, Status = nameof(S.PendingApproval), ContentHash = hash },
            new { quote.Number, v.VersionNo, Status = v.Status.ToString(), ContentHash = hash, v.ApprovalComment }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quoteId, ct);
    }

    /// <summary>
    /// The policy run of this moment for the version's lines (§5): catalogue facts and the public price of now, the buyer's own resolver
    /// price for the quote's company (Exclusive, net; never a previous version), and cost, floors and <c>Pricing:AllowBelowCost</c> through
    /// <see cref="IPriceCheckFacts"/>. Also the line facts a send freezes.
    /// </summary>
    private async Task<(QuoteApprovalCheck Check, List<QuoteLineFacts> Facts)> CheckNowAsync(Guid companyId, QuoteVersion v, DateTime now, CancellationToken ct)
    {
        var lines = v.Lines.OrderBy(l => l.LineNo).ToList();
        var catalogue = await CatalogueAsync(lines.Select(l => l.VariantId), ct);
        var resolved = lines.Count == 0 ? [] : await prices.ResolveAsync(companyId, [.. lines.Select(l => new PriceRequestLine(l.VariantId, l.Quantity,
            PricingInput.From(catalogue[l.VariantId].Variant, catalogue[l.VariantId].VatRate)))], TaxMode.Exclusive, now, ct);
        var references = resolved.Select(r => r.Price).ToList();
        var facts = lines.Zip(references, (l, r) => catalogue[l.VariantId].Facts(l.Quantity, r.PublicUnitPriceMinor)).ToList();
        var costs = await checks.ForVariantsAsync([.. lines.Select(l => l.VariantId)], ct);
        return (QuoteApprovalPolicy.Evaluate(v, costs.Costs, costs.Floors, references, options, costs.AllowBelowCost), facts);
    }

    /// <summary>
    /// The buyer company as the PDF prints it (§4, §6): legal name, BIN and, as the address, the default delivery site's (null without one).
    /// A quote is sent only to an active company (as it is created only for one).
    /// </summary>
    private async Task<QuoteCompanySnapshot> CompanySnapshotAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == companyId, ct);
        if (company.Status != CompanyStatus.Active) throw new ConflictException("COMPANY_NOT_ACTIVE", "Quotes are sent only to active companies.");
        var site = await db.CompanySites.AsNoTracking().Where(s => s.CompanyId == companyId && s.IsDefaultDelivery).FirstOrDefaultAsync(ct);
        var address = site is null ? null
            : string.Join(", ", new[] { site.Line1, site.Line2, site.Upazila, site.District, site.Division, site.PostalCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new QuoteCompanySnapshot(company.LegalName, string.IsNullOrWhiteSpace(company.Bin) ? null : company.Bin, address);
    }

    /// <summary>§8.6, one rule for the desk and the acceptance: <see cref="QuoteAcceptanceGate.LapsedAsync"/>.</summary>
    private Task<bool> LapsedAsync(Quote quote, CancellationToken ct) => QuoteAcceptanceGate.LapsedAsync(db, quote, ct);

    /// <summary>A sent quote answers its RFQ (the first one stamps the SLA); the RFQ row is locked after the quote tier (§9).</summary>
    private async Task AnsweredAsync(Quote quote, DateTime now, CancellationToken ct)
    {
        if (quote.RfqId is { } rfqId) (await LockRfqAsync(rfqId, ct)).RecordQuoteSent(now);
    }

    private static StaffQuoteFlagsDto FlagsDto(QuoteFlags f) => new(f.BelowFloorLineIds, f.OverAuthorityLineIds, f.LongValidity);
}
