using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// <see cref="IQuoteAcceptanceGate"/> (ADR-0011 §8, §9, §13; threat model QT1, QT4, QT5, QT13). Membership is read from
/// <c>company_members</c> for the company in the URL on every call (<see cref="BuyerQuoteAccess"/>); every acceptability rule is the
/// domain's <see cref="Quote.Accept"/>, run once on an untracked copy as the early answer and again on the locked rows, so the two can
/// never disagree. The order side never writes quote rows: whether an earlier order lapsed is read from that order (§8.6).
/// </summary>
public sealed class QuoteAcceptanceGate(MechwareDbContext db, QuoteOptions options, ICurrentUserService user) : IQuoteAcceptanceGate
{
    public const string NotFoundMessage = "Quote not found.";

    public async Task<QuoteForOrder> FindAsync(Guid userId, Guid companyId, Guid quoteId, int versionNo, CancellationToken ct = default)
    {
        var member = await BuyerQuoteAccess.FindAsync(db, userId, companyId, ct);
        if (member is not { MayRead: true }) throw NotFound();
        var quote = await Loaded(db.Quotes.AsNoTracking()).FirstOrDefaultAsync(q => q.Id == quoteId && q.CompanyId == companyId, ct) ?? throw NotFound();
        var v = quote.Versions.FirstOrDefault(x => x.VersionNo == versionNo);
        // Drafts and versions waiting for approval do not exist for the buyer; a member outside Quotes:VisibleTo sees only the quotes they are the contact for.
        if (v is null || v.Status is S.Draft or S.PendingApproval || ((member.Roles & options.VisibleTo) == 0 && quote.ContactUserId != userId))
            throw NotFound();
        if ((member.Roles & CompanyRoles.Buyer) == 0)
            throw new ForbiddenException("COMPANY_ROLE_REQUIRED", "Only a member with the Buyer role can accept a quote.");
        return new QuoteForOrder(quote.Id, quote.Number, quote.CompanyId, v.Id, v.VersionNo, v.PaymentTerms, v.ShippingMode, v.ShipToSiteId, v.ShipTo,
            v.ShippingFees ?? [], [.. v.Lines.OrderBy(l => l.LineNo).Select(l => new QuoteOrderLine(l.Id, l.VariantId, l.SellerId, l.Sku, l.TitleEn, l.Quantity,
                l.UnitPriceMinor, l.PublicUnitPriceMinor))]);
    }

    public async Task EnsureAcceptableAsync(QuoteForOrder quote, string contentHash, Guid userId, DateTime nowUtc, CancellationToken ct = default)
    {
        // The same decision as under the lock, on a copy that is never saved (the order id is a placeholder).
        var copy = await Loaded(db.Quotes.AsNoTracking()).SingleAsync(q => q.Id == quote.QuoteId, ct);
        copy.Accept(quote.VersionNo, contentHash, Guid.NewGuid(), userId, await LapsedAsync(db, copy, ct), nowUtc);
    }

    public async Task AcceptAsync(QuoteForOrder quote, string contentHash, Guid orderId, Guid userId, DateTime nowUtc, CancellationToken ct = default)
    {
        // §9: the quote row, then its versions in version order, FOR NO KEY UPDATE (the order rows reference them by foreign key, which
        // only takes FOR KEY SHARE). A withdrawal, a newer version or another acceptance that committed first is seen here and wins.
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM quotes WHERE id = {quote.QuoteId} FOR NO KEY UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM quote_versions WHERE quote_id = {quote.QuoteId} ORDER BY version_no FOR NO KEY UPDATE").ToListAsync(ct);
        var locked = await Loaded(db.Quotes).SingleAsync(q => q.Id == quote.QuoteId, ct);
        var v = locked.Version(quote.VersionNo);
        if (v.Id != quote.VersionId) throw new ConflictException("QUOTE_CHANGED", "The quote is not the one you reviewed. Reload it and review again.");
        var (status, previousOrder) = (v.Status, v.AcceptedOrderId);
        locked.Accept(quote.VersionNo, contentHash, orderId, userId, await LapsedAsync(db, locked, ct), nowUtc);   // outbox QuoteAcceptedEvent
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), userId, user.IpAddress, "quote.accepted", "quotes", locked.Id.ToString(),
            JsonSerializer.Serialize(new { v.VersionNo, Status = status.ToString(), AcceptedOrderId = previousOrder }),
            JsonSerializer.Serialize(new { locked.Number, v.VersionNo, Status = v.Status.ToString(), v.ContentHash, AcceptedOrderId = orderId }),
            user.CorrelationId, nowUtc));
    }

    /// <summary>§8.6: the Accepted version's order is Cancelled and was never confirmed, so the version may be superseded or accepted again.</summary>
    internal static async Task<bool> LapsedAsync(MechwareDbContext db, Quote quote, CancellationToken ct) =>
        quote.Versions.FirstOrDefault(v => v.Status == S.Accepted)?.AcceptedOrderId is { } orderId
        && await db.Orders.AnyAsync(o => o.Id == orderId && o.Status == OrderStatus.Cancelled && o.ConfirmedAtUtc == null, ct);

    private static KeyNotFoundException NotFound() => new(NotFoundMessage);

    private static IQueryable<Quote> Loaded(IQueryable<Quote> quotes) =>
        quotes.Include(q => q.Versions.OrderBy(v => v.VersionNo)).ThenInclude(v => v.Lines).AsSplitQuery();
}
