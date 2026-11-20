using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Cart;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// <see cref="IBuyerRfqService"/> (ADR-0011 §2, §12-§14; threat model QT1, QT10, QT17). Who may ask: an effective member, in any role,
/// of an Active company, with a verified account, checked from the database for the company in the URL. The lines are never the
/// client's: <c>cart</c> reads the caller's own server cart, <c>text</c>/<c>csv</c> go through the quick-order parser and matcher (D5.1,
/// D5.2, a port), <c>product</c> checks one catalogue variant. An unmatched part is accepted (staff are there for it); a parse error
/// refuses the whole RFQ. Under the per-company advisory lock the order is: replay, caps, lines, insert, so parallel requests cannot all
/// pass a cap and a replay is never counted. The RFQ, its lines, the audit row and the outbox event are one commit.
/// </summary>
public sealed partial class BuyerRfqService(MechwareDbContext db, IQuickOrderValidator quickOrder, ICartService cart, QuoteOptions options,
    ICurrentUserService user, TimeProvider clock) : IBuyerRfqService
{
    public const int MaxPageSize = 50;
    public const string NotFoundMessage = "Request not found.";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,100}$")]
    private static partial Regex KeyPattern();

    private static KeyNotFoundException NotFound() => new(NotFoundMessage);

    public async Task<RfqCreated> CreateAsync(CreateRfqRequest request, CancellationToken ct = default)
    {
        if (!KeyPattern().IsMatch(request.IdempotencyKey ?? string.Empty))
            throw new ValidationException("idempotencyKey", "An Idempotency-Key of 8 to 100 letters, digits, '-' or '_' is required.");
        var source = ParseSource(request.Source);
        var member = await BuyerQuoteAccess.FindAsync(db, request.UserId, request.CompanyId, ct);
        if (member is null || member.CompanyStatus == CompanyStatus.Rejected) throw NotFound();
        if (member.CompanyStatus == CompanyStatus.Pending)
            throw new ConflictException("COMPANY_NOT_VERIFIED", "Your company is not verified yet. Quotes open once MECHWARE has verified it.");
        if (member.CompanyStatus == CompanyStatus.Suspended)
            throw new ConflictException("COMPANY_SUSPENDED", "Your company is suspended. Contact MECHWARE.");
        var status = await db.Users.AsNoTracking().Where(u => u.Id == request.UserId && u.DeletedAtUtc == null).Select(u => (UserStatus?)u.Status).FirstOrDefaultAsync(ct);
        if (status != UserStatus.Active) throw new ForbiddenException("ACCOUNT_NOT_VERIFIED", "Verify your e-mail address or phone number before asking for a quote.");

        var message = PlainText.Clean(request.Message)?.Trim();
        if (string.IsNullOrEmpty(message)) message = null;
        var hash = HashOf(request, source, message);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"rfq:{request.CompanyId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        if (await ReplayAsync(request.UserId, request.IdempotencyKey!, hash, ct) is { } replay) return new RfqCreated(replay, true);
        if (await db.Rfqs.CountAsync(r => r.CompanyId == request.CompanyId && r.CreatedAtUtc > now.AddHours(-1), ct) >= options.MaxRfqsPerCompanyPerHour)
            throw new TooManyRequestsException("RFQ_RATE_LIMITED", "Your company has asked for many quotes in the last hour. Try again later.");
        if (await db.Rfqs.CountAsync(r => r.CompanyId == request.CompanyId && (r.Status == RfqStatus.Submitted || r.Status == RfqStatus.Quoting), ct) >= options.MaxOpenRfqsPerCompany)
            throw new ConflictException("TOO_MANY_OPEN_RFQS", "Your company has many requests waiting for a quote. Wait for an answer or withdraw one.");
        if (request.ShipToSiteId is { } site && !await db.CompanySites.AnyAsync(s => s.Id == site && s.CompanyId == request.CompanyId, ct))
            throw new ValidationException("shipToSiteId", "Choose one of your company's delivery sites.");

        var lines = await LinesAsync(request, source, ct);
        var rfq = Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(now), request.CompanyId, request.UserId, source, message, request.NeededBy, request.ShipToSiteId,
            lines, request.IdempotencyKey!, hash, now);
        db.Rfqs.Add(rfq);
        db.AuditLogs.Add(Audit("rfq.submitted", rfq.Id, request.UserId, null, JsonSerializer.Serialize(new { rfq.Number, rfq.CompanyId, Source = rfq.Source.ToString(), rfq.LineCount }), now));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            db.ChangeTracker.Clear();
            if (pg.ConstraintName == "ux_rfqs_idempotency")   // the same key raced in through another company's lock
                throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request.");
            throw new ConflictException("RFQ_NUMBER_CLASH", "The request could not be numbered. Try again.");
        }

        return new RfqCreated(await DetailAsync(rfq, request.UserId, ct), false);
    }

    public async Task<RfqPage> ListAsync(Guid actorUserId, Guid companyId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var member = await ReaderAsync(actorUserId, companyId, ct);
        RfqStatus? wanted = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (int.TryParse(status, out _) || !Enum.TryParse<RfqStatus>(status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw new ValidationException("status", "Unknown status.");
            wanted = parsed;
        }

        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
        var query = Visible(member, actorUserId, companyId).Where(r => wanted == null || r.Status == wanted);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new RfqPage([.. rows.Select(r => Summary(r, actorUserId))], page, pageSize, total);
    }

    public async Task<RfqDetailDto> GetAsync(Guid actorUserId, Guid companyId, Guid rfqId, CancellationToken ct = default)
    {
        var member = await ReaderAsync(actorUserId, companyId, ct);
        var rfq = await Visible(member, actorUserId, companyId).Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId, ct) ?? throw NotFound();
        return await DetailAsync(rfq, actorUserId, ct);
    }

    /// <summary>The requester, or a role of <see cref="QuoteOptions.VisibleTo"/>, withdraws an open RFQ. The table has no actor column, so the audit row records who.</summary>
    public async Task<RfqDetailDto> WithdrawAsync(Guid actorUserId, Guid companyId, Guid rfqId, CancellationToken ct = default)
    {
        var member = await ReaderAsync(actorUserId, companyId, ct);
        var rfq = await Visible(member, actorUserId, companyId).AsTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId, ct) ?? throw NotFound();
        if (rfq.Status == RfqStatus.Withdrawn) return await DetailAsync(rfq, actorUserId, ct);   // withdrawing twice is one withdrawal

        var before = rfq.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        rfq.Withdraw(now);
        db.AuditLogs.Add(Audit("rfq.withdrawn", rfq.Id, actorUserId, JsonSerializer.Serialize(new { Status = before.ToString() }),
            JsonSerializer.Serialize(new { rfq.Number, rfq.CompanyId, Status = rfq.Status.ToString() }), now));
        await db.SaveChangesAsync(ct);
        return await DetailAsync(rfq, actorUserId, ct);
    }

    // ---- access ----------------------------------------------------------------------------------------------------------

    private async Task<BuyerQuoteMember> ReaderAsync(Guid actorUserId, Guid companyId, CancellationToken ct) =>
        await BuyerQuoteAccess.FindAsync(db, actorUserId, companyId, ct) is { MayRead: true } member ? member : throw NotFound();

    /// <summary>The company's RFQs this member may see: all of them for a role in <see cref="QuoteOptions.VisibleTo"/>, otherwise the ones they asked for.</summary>
    private IQueryable<Rfq> Visible(BuyerQuoteMember member, Guid actorUserId, Guid companyId)
    {
        var all = (member.Roles & options.VisibleTo) != 0;
        return db.Rfqs.AsNoTracking().Where(r => r.CompanyId == companyId && (all || r.RequestedBy == actorUserId));
    }

    // ---- lines -----------------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<RfqLineInput>> LinesAsync(CreateRfqRequest request, RfqSource source, CancellationToken ct) => source switch
    {
        RfqSource.Cart => await CartLinesAsync(request.UserId, ct),
        RfqSource.Product => await ProductLinesAsync(request, ct),
        _ => await TextLinesAsync(request.Text, ct),
    };

    private async Task<IReadOnlyList<RfqLineInput>> CartLinesAsync(Guid userId, CancellationToken ct)
    {
        var view = await cart.GetAsync(CartOwner.ForUser(userId), ct);
        if (view.Lines.Count == 0) throw Invalid("Your cart is empty.");
        return [.. view.Lines.Select(l => new RfqLineInput(l.Sku, PartNumbers.Normalise(l.Sku), l.Quantity, RfqMatchStatus.Matched, l.VariantId, null, null))];
    }

    private async Task<IReadOnlyList<RfqLineInput>> ProductLinesAsync(CreateRfqRequest request, CancellationToken ct)
    {
        if (request.VariantId is not { } id || request.Quantity is not { } quantity) throw new ValidationException("variantId", "Give the item and a quantity.");
        if (quantity < 1) throw new ValidationException("quantity", "The quantity must be 1 or more.");
        if (quantity > QuoteLimits.MaxQuantityPerLine) throw TooMany([(1, quantity)]);
        var variant = await (from v in db.ProductVariants.AsNoTracking()
                             join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                             where v.Id == id && v.Status == VariantStatus.Active && p.Status == ProductStatus.Active && p.DeletedAtUtc == null
                             select new { v.Id, v.Sku, v.PartNumberKey }).FirstOrDefaultAsync(ct)
                      ?? throw Invalid("This item is not available.", "variantId");
        return [new RfqLineInput(variant.Sku, variant.PartNumberKey, quantity, RfqMatchStatus.Matched, variant.Id, null, null)];
    }

    private async Task<IReadOnlyList<RfqLineInput>> TextLinesAsync(string? text, CancellationToken ct)
    {
        QuickOrderValidation found;
        try { found = await quickOrder.ValidateAsync(text, ct); }
        catch (UnprocessableException ex) { throw Invalid(ex.Message); }   // more than 500 lines, not valid text; too large stays 413
        if (found.Errors.Count > 0)
            throw new UnprocessableException("RFQ_LINES_INVALID", "Some lines could not be read. Nothing was sent.", [.. found.Errors.Select(e => new ValidationError($"lines[{e.LineNumber}]", $"{e.Code}: {e.Message}"))]);
        if (found.Lines.Count == 0) throw Invalid("Give at least one line.");
        if (found.Lines.Where(l => l.RequestedQuantity > QuoteLimits.MaxQuantityPerLine).ToList() is { Count: > 0 } over)
            throw TooMany([.. over.Select(l => (l.LineNumbers[0], l.RequestedQuantity))]);

        return [.. found.Lines.Select(l => new RfqLineInput(l.Raw, PartNumbers.Normalise(l.Part), l.RequestedQuantity, l.Status switch
        {
            "PART_NOT_FOUND" => RfqMatchStatus.NotFound,
            "PART_AMBIGUOUS" => RfqMatchStatus.Ambiguous,
            "PART_OBSOLETE" => RfqMatchStatus.Obsolete,
            _ => RfqMatchStatus.Matched,   // in stock or not: staff quote the lead time
        }, l.Variant is { } v && l.Status != "PART_AMBIGUOUS" ? v.Id : null, l.Status == "PART_OBSOLETE" ? l.Successor?.Variant.Id : null,
            l.LineNumbers.Count > 1 ? "Typed on lines " + string.Join(", ", l.LineNumbers) : null))];
    }

    private static UnprocessableException Invalid(string message, string field = "lines") =>
        new("RFQ_LINES_INVALID", message, [new ValidationError(field, message)]);

    private static UnprocessableException TooMany(IEnumerable<(int Line, int Quantity)> lines) =>
        new("QTY_EXCEEDS_LIMIT", $"At most {QuoteLimits.MaxQuantityPerLine:N0} of one item per request.",
            [.. lines.Select(l => new ValidationError($"lines[{l.Line}]", $"{l.Quantity:N0} is more than {QuoteLimits.MaxQuantityPerLine:N0}."))]);

    // ---- replay, hash, audit ---------------------------------------------------------------------------------------------

    private static RfqSource ParseSource(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "cart" => RfqSource.Cart,
        "text" => RfqSource.Text,
        "csv" => RfqSource.Csv,
        "product" => RfqSource.Product,
        _ => throw new ValidationException("source", "The source is cart, text, csv or product."),
    };

    /// <summary>
    /// What was asked, as a hash: the company, the source, the cleaned message, the date and site, and the source's own input. The cart's
    /// current contents are NOT in it, so a replay after the cart changed still returns the first RFQ instead of "key reused".
    /// </summary>
    private static string HashOf(CreateRfqRequest r, RfqSource source, string? message)
    {
        var own = source switch
        {
            RfqSource.Text or RfqSource.Csv => PlainText.Clean(r.Text),
            RfqSource.Product => $"{r.VariantId:N}:{r.Quantity}",
            _ => null,
        };
        var canonical = JsonSerializer.Serialize(new object?[] { "mechware.rfq.v1", r.CompanyId, source.ToString(), message, r.NeededBy?.ToString("yyyy-MM-dd"), r.ShipToSiteId, own });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<RfqDetailDto?> ReplayAsync(Guid userId, string key, string hash, CancellationToken ct)
    {
        var rfq = await db.Rfqs.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.RequestedBy == userId && r.IdempotencyKey == key, ct);
        if (rfq is null) return null;
        if (rfq.RequestHash != hash) throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request.");
        return await DetailAsync(rfq, userId, ct);
    }

    private AuditLog Audit(string action, Guid rfqId, Guid actor, string? oldValues, string? newValues, DateTime now) =>
        new(Guid.NewGuid(), actor, user.IpAddress, action, "rfqs", rfqId.ToString(), oldValues, newValues, user.CorrelationId, now);

    // ---- answers ---------------------------------------------------------------------------------------------------------

    private static RfqSummaryDto Summary(Rfq r, Guid actor) =>
        new(r.Id, r.Number, r.Status.ToString().ToLowerInvariant(), r.Source.ToString().ToLowerInvariant(), r.LineCount, r.Message, r.NeededBy, r.ShipToSiteId,
            r.RequestedBy == actor, r.CreatedAtUtc, r.ClosedAtUtc, r.CloseReason, r.WithdrawnAtUtc);

    private async Task<RfqDetailDto> DetailAsync(Rfq r, Guid actor, CancellationToken ct) => new(Summary(r, actor), await LinesOfAsync(db, r, ct));

    /// <summary>The RFQ's lines as asked, each with the catalogue item it matched (and an obsolete one's successor); shared with the staff desk.</summary>
    internal static async Task<IReadOnlyList<RfqLineDto>> LinesOfAsync(MechwareDbContext db, Rfq r, CancellationToken ct)
    {
        var ids = r.Lines.SelectMany(l => new[] { l.VariantId, l.SuccessorVariantId }).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var variants = ids.Count == 0 ? [] : await (from v in db.ProductVariants.AsNoTracking()
                                                    join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                                                    where ids.Contains(v.Id)
                                                    select new RfqVariantDto(v.Id, v.Sku, p.TitleEn, p.TitleBn)).ToDictionaryAsync(v => v.Id, ct);
        RfqVariantDto? Of(Guid? id) => id is { } k && variants.TryGetValue(k, out var v) ? v : null;
        return [.. r.Lines.OrderBy(l => l.LineNo).Select(l => new RfqLineDto(l.LineNo, l.RawText, l.PartKey, l.Quantity,
            l.MatchStatus switch { RfqMatchStatus.NotFound => "not_found", var s => s.ToString().ToLowerInvariant() }, Of(l.VariantId), Of(l.SuccessorVariantId), l.Note, l.Id))];
    }
}
