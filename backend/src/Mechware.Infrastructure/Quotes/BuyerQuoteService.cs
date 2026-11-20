using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// <see cref="IBuyerQuoteService"/> (ADR-0011 §8.8, §12-§14; threat model QT1, QT6, QT8). Membership of the company in the URL is read from
/// <c>company_members</c> on every call (<see cref="BuyerQuoteAccess"/>); a role of <see cref="QuoteOptions.VisibleTo"/> sees all of the
/// company's quotes, anyone else only those they are the contact for; a quote with no version Sent or later does not exist for the buyer.
/// The same rule as <see cref="QuoteAcceptanceGate"/>. A rejection locks the quote row, then its versions (§9), and writes the version, one
/// audit row <c>quote.rejected</c> and the outbox event in one commit.
/// </summary>
public sealed class BuyerQuoteService(MechwareDbContext db, QuoteOptions options, ICurrentUserService user, TimeProvider clock) : IBuyerQuoteService
{
    public const int MaxPageSize = 50;

    private static readonly string[] Statuses = ["sent", "expired", "accepted", "rejected", "withdrawn", "superseded"];

    private static KeyNotFoundException NotFound() => new(QuoteAcceptanceGate.NotFoundMessage);

    public async Task<BuyerQuotePage> ListAsync(Guid actorUserId, Guid companyId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var member = await ReaderAsync(actorUserId, companyId, ct);
        var wanted = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (wanted is not null && !Statuses.Contains(wanted)) throw new ValidationException("status", "Unknown status.");
        var now = clock.GetUtcNow().UtcDateTime;
        S? exact = wanted is null or "expired" ? null : Enum.Parse<S>(wanted, ignoreCase: true);

        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
        var rows = from q in Visible(member, actorUserId, companyId)
                   from v in db.QuoteVersions.Where(v => v.QuoteId == q.Id && v.Status != S.Draft && v.Status != S.PendingApproval)
                       .OrderByDescending(v => v.VersionNo).Take(1)
                   where (exact == null || v.Status == exact) && (wanted != "sent" || v.ValidUntilUtc > now)
                         && (wanted != "expired" || (v.Status == S.Sent && v.ValidUntilUtc <= now))
                   select new { Quote = q, Version = v, Lines = db.Set<QuoteLine>().Count(l => l.VersionId == v.Id) };
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(r => r.Version.SentAtUtc).ThenBy(r => r.Quote.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new BuyerQuotePage([.. items.Select(r => Summary(r.Quote, r.Version, r.Lines, actorUserId, now))], page, pageSize, total);
    }

    public async Task<BuyerQuoteDetailDto> GetAsync(Guid actorUserId, Guid companyId, Guid quoteId, CancellationToken ct = default)
    {
        var member = await ReaderAsync(actorUserId, companyId, ct);
        var quote = await Loaded(Visible(member, actorUserId, companyId)).FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw NotFound();
        return await DetailAsync(quote, actorUserId, ct);
    }

    public async Task<BuyerQuoteRefDto> FindByNumberAsync(Guid actorUserId, string? number, CancellationToken ct = default)
    {
        // Unknown, malformed and another company's numbers are the same 404 (QT8); the company is the quote's, the membership checked for it.
        var wanted = number?.Trim().ToUpperInvariant();
        if (!QuoteNumber.IsQuoteNumber(wanted)) throw NotFound();
        var companyId = await db.Quotes.AsNoTracking().Where(q => q.Number == wanted).Select(q => (Guid?)q.CompanyId).FirstOrDefaultAsync(ct) ?? throw NotFound();
        var member = await ReaderAsync(actorUserId, companyId, ct);
        var quoteId = await Visible(member, actorUserId, companyId).Where(q => q.Number == wanted).Select(q => (Guid?)q.Id).FirstOrDefaultAsync(ct) ?? throw NotFound();
        return new BuyerQuoteRefDto(companyId, quoteId, wanted!);
    }

    public async Task<BuyerQuoteDetailDto> RejectAsync(Guid actorUserId, Guid companyId, Guid quoteId, int? versionNo, string? reason, CancellationToken ct = default)
    {
        if (versionNo is not > 0) throw new ValidationException("versionNo", "Name the version of the quote you reject.");
        var member = await ReaderAsync(actorUserId, companyId, ct);
        var seen = await Visible(member, actorUserId, companyId).Where(q => q.Id == quoteId).Select(q => new { q.ContactUserId, q.RfqId }).FirstOrDefaultAsync(ct)
                   ?? throw NotFound();
        // §13: the contact, the RFQ's requester, a Buyer or an Admin; anyone else who may see the quote reads it only.
        if (seen.ContactUserId != actorUserId && (member.Roles & (CompanyRoles.Buyer | CompanyRoles.Admin)) == 0
            && !await db.Rfqs.AnyAsync(r => r.Id == seen.RfqId && r.RequestedBy == actorUserId, ct))
            throw new ForbiddenException("COMPANY_ROLE_REQUIRED", "Only the quote's contact, a Buyer or an Admin can reject a quote.");

        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM quotes WHERE id = {quoteId} FOR NO KEY UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM quote_versions WHERE quote_id = {quoteId} ORDER BY version_no FOR NO KEY UPDATE").ToListAsync(ct);
        var quote = await Loaded(db.Quotes).SingleAsync(q => q.Id == quoteId, ct);
        var v = quote.Versions.FirstOrDefault(x => x.VersionNo == versionNo && x.Status is not (S.Draft or S.PendingApproval)) ?? throw NotFound();
        var before = v.Status;
        quote.Reject(v.VersionNo, actorUserId, reason, now);   // Sent only (QUOTE_INVALID_TRANSITION, QUOTE_CLOSED); outbox QuoteRejectedEvent
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actorUserId, user.IpAddress, "quote.rejected", "quotes", quote.Id.ToString(),
            JsonSerializer.Serialize(new { v.VersionNo, Status = before.ToString() }),
            JsonSerializer.Serialize(new { quote.Number, v.VersionNo, Status = v.Status.ToString(), v.ContentHash, v.RejectReason }), user.CorrelationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DetailAsync(quote, actorUserId, ct);
    }

    // ---- access ----------------------------------------------------------------------------------------------------------

    private async Task<BuyerQuoteMember> ReaderAsync(Guid actorUserId, Guid companyId, CancellationToken ct) =>
        await BuyerQuoteAccess.FindAsync(db, actorUserId, companyId, ct) is { MayRead: true } member ? member : throw NotFound();

    /// <summary>The company's quotes this member may see that have a version Sent or later.</summary>
    private IQueryable<Quote> Visible(BuyerQuoteMember member, Guid actorUserId, Guid companyId)
    {
        var all = (member.Roles & options.VisibleTo) != 0;
        return db.Quotes.AsNoTracking().Where(q => q.CompanyId == companyId && (all || q.ContactUserId == actorUserId)
            && db.QuoteVersions.Any(v => v.QuoteId == q.Id && v.Status != S.Draft && v.Status != S.PendingApproval));
    }

    private static IQueryable<Quote> Loaded(IQueryable<Quote> quotes) =>
        quotes.Include(q => q.Versions.OrderBy(v => v.VersionNo)).ThenInclude(v => v.Lines).AsSplitQuery();

    // ---- views (field by field, QT6) -------------------------------------------------------------------------------------

    private async Task<BuyerQuoteDetailDto> DetailAsync(Quote quote, Guid actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var shown = quote.Versions.Where(v => v.Status is not (S.Draft or S.PendingApproval)).OrderBy(v => v.VersionNo).ToList();
        var latest = shown[^1];
        var acceptable = quote.IsAcceptable(latest.VersionNo, await QuoteAcceptanceGate.LapsedAsync(db, quote, ct), now);
        return new BuyerQuoteDetailDto(Summary(quote, latest, latest.Lines.Count, actor, now),
            [.. shown.Select(v => Version(v, acceptable && v == latest, now))]);
    }

    private static string StatusOf(QuoteVersion v, DateTime now) => v.IsExpired(now) ? "expired" : StaffQuoteViews.Wire(v.Status);

    private static BuyerQuoteSummaryDto Summary(Quote q, QuoteVersion v, int lines, Guid actor, DateTime now) =>
        new(q.Id, q.Number, q.RfqId, v.VersionNo, StatusOf(v, now), v.SentAtUtc, v.ValidUntilUtc, lines, v.GoodsTotalMinor, v.GrandTotalMinor,
            StaffQuoteViews.Wire(v.PaymentTerms), StaffQuoteViews.Wire(v.ShippingMode), q.ContactUserId == actor, q.ClosedAtUtc);

    private static BuyerQuoteVersionDto Version(QuoteVersion v, bool acceptable, DateTime now) =>
        new(v.VersionNo, StatusOf(v, now), acceptable, v.SentAtUtc, v.ValidUntilUtc, v.ValidityDays, StaffQuoteViews.Wire(v.PaymentTerms),
            StaffQuoteViews.Wire(v.ShippingMode), v.ShipToSiteId, v.ShipTo, v.ShippingFees, v.TermsText, v.BuyerNote, v.CompanySnapshot, v.PreparerName,
            v.ContentHash, v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor, v.ShippingTotalMinor, v.GrandTotalMinor, v.AcceptedOrderId, v.AcceptedAtUtc,
            v.RejectedAtUtc, v.RejectReason, v.WithdrawnAtUtc, v.SupersededAtUtc,
            [.. v.Lines.OrderBy(l => l.LineNo).Select(l => new BuyerQuoteLineDto(l.Id, l.LineNo, l.VariantId, l.Sku, l.PartNumber, l.TitleEn, l.TitleBn,
                l.Quantity, l.UnitPriceMinor, l.VatRate, l.LineNetMinor, l.LineTaxMinor, l.LineTotalMinor, l.LineNote))]);
}
