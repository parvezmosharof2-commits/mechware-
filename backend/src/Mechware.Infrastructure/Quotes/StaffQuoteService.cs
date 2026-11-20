using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Pricing;
using Mechware.Application.Quotes;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// <see cref="IStaffQuoteService"/> (ADR-0011 §2, §3, §9, §12-§14; D4.6, threat model QT1, QT6). Every command runs in one transaction
/// with its row locks in the global order: an RFQ command locks only its <c>rfqs</c> row; a quote command locks the <c>quotes</c> row,
/// then its <c>quote_versions</c> rows by version number, all <c>FOR NO KEY UPDATE</c> (a foreign-key insert elsewhere only takes
/// FOR KEY SHARE) and nothing after them, so two staff acting on one quote queue and the second sees the first's result. The
/// conflict-of-interest refusal reads the actor's memberships from <c>company_members</c> in the same request. A unique or exclusion
/// violation that still slips through (23505, 23P01 at commit) is a 409, never a 500. Audit row and outbox events commit with the change.
/// </summary>
public sealed partial class StaffQuoteService(MechwareDbContext db, IPriceResolver prices, IPriceCheckFacts checks, QuoteOptions options,
    ICurrentUserService user, TimeProvider clock) : IStaffQuoteService
{
    public const int MaxPageSize = 50;

    private static readonly string[] EditorRoles = [.. new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Support,
        Permissions.Roles.Moderator, Permissions.Roles.Finance, Permissions.Roles.Admin }
        .Where(r => Permissions.GetDefaultPermissionsForRole(r).Contains(Permissions.QuotesEdit))];

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static KeyNotFoundException RfqNotFound() => new("Request not found.");
    private static KeyNotFoundException QuoteNotFound() => new("Quote not found.");

    // ---- RFQ inbox -------------------------------------------------------------------------------------------------------

    public async Task<StaffRfqPage> ListRfqsAsync(StaffQuoteQuery query, CancellationToken ct = default)
    {
        var (page, size, status, assigned, none, like) = Filters(query);
        var rows = from r in db.Rfqs.AsNoTracking() join c in db.Companies.AsNoTracking() on r.CompanyId equals c.Id select new { R = r, c.LegalName };
        var open = status == "open";
        if (open) rows = rows.Where(x => x.R.Status == RfqStatus.Submitted || x.R.Status == RfqStatus.Quoting);
        else if (status is not null) { var s = Parse<RfqStatus>(status); rows = rows.Where(x => x.R.Status == s); }
        if (none) rows = rows.Where(x => x.R.AssignedTo == null);
        else if (assigned is { } a) rows = rows.Where(x => x.R.AssignedTo == a);
        if (query.CompanyId is { } company) rows = rows.Where(x => x.R.CompanyId == company);
        if (like is not null) rows = rows.Where(x => EF.Functions.ILike(x.R.Number, like) || EF.Functions.ILike(x.LegalName, like));

        var total = await rows.CountAsync(ct);
        var ordered = open ? rows.OrderBy(x => x.R.CreatedAtUtc) : rows.OrderByDescending(x => x.R.CreatedAtUtc);   // the inbox: oldest waiting first
        var found = await ordered.ThenBy(x => x.R.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(found.SelectMany(x => new[] { x.R.RequestedBy, x.R.AssignedTo }), ct);
        return new StaffRfqPage([.. found.Select(x => RfqSummary(x.R, x.LegalName, names))], page, size, total);
    }

    public async Task<StaffRfqDetailDto> GetRfqAsync(Guid rfqId, CancellationToken ct = default)
    {
        var rfq = await db.Rfqs.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId, ct) ?? throw RfqNotFound();
        var company = await db.Companies.AsNoTracking().Where(c => c.Id == rfq.CompanyId).Select(c => c.LegalName).SingleAsync(ct);
        var names = await NamesAsync([rfq.RequestedBy, rfq.AssignedTo], ct);
        var now = Now;
        var quotes = (await (from q in db.Quotes.AsNoTracking()
                             join v in db.QuoteVersions.AsNoTracking() on new { Q = q.Id, N = q.LatestVersionNo } equals new { Q = v.QuoteId, N = v.VersionNo }
                             where q.RfqId == rfqId
                             orderby q.CreatedAtUtc
                             select new { q.Id, q.Number, q.ClosedAtUtc, v.Status, v.ValidUntilUtc }).ToListAsync(ct))
            .Select(q => new StaffQuoteRefDto(q.Id, q.Number, StatusOf(q.ClosedAtUtc, q.Status, q.ValidUntilUtc, now))).ToList();
        return new StaffRfqDetailDto(RfqSummary(rfq, company, names), rfq.Message, rfq.ShipToSiteId, await BuyerRfqService.LinesOfAsync(db, rfq, ct), quotes);
    }

    public async Task<StaffRfqDetailDto> AssignRfqAsync(Guid rfqId, AssignRfqRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        if (request.AssigneeId is { } assignee && !await db.UserRoles.AnyAsync(r => r.UserId == assignee && EditorRoles.Contains(r.RoleName)
                && db.Users.Any(u => u.Id == assignee && u.Status == UserStatus.Active && u.DeletedAtUtc == null), ct))
            throw new ValidationException("assigneeId", "Assign the request to a staff member who prepares quotes.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rfq = await LockRfqAsync(rfqId, ct);
        var before = rfq.AssignedTo;
        rfq.Assign(request.AssigneeId, now);   // a Closed or Withdrawn request: 409 RFQ_INVALID_TRANSITION
        Audit("rfq.assigned", "rfqs", rfq.Id, new { AssignedTo = before }, new { rfq.Number, rfq.AssignedTo }, actor, now);
        await SaveAsync(tx, ct);
        return await GetRfqAsync(rfqId, ct);
    }

    public async Task<StaffRfqDetailDto> CloseRfqAsync(Guid rfqId, StaffReasonRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rfq = await LockRfqAsync(rfqId, ct);
        var before = rfq.Status;
        rfq.Close(actor, request.Reason ?? string.Empty, now);   // reason 5-500, shown to the buyer; quotes made from it are not touched
        Audit("rfq.closed", "rfqs", rfq.Id, new { Status = before.ToString() }, new { rfq.Number, Status = rfq.Status.ToString(), rfq.CloseReason }, actor, now);
        await SaveAsync(tx, ct);
        return await GetRfqAsync(rfqId, ct);
    }

    // ---- quotes ----------------------------------------------------------------------------------------------------------

    public async Task<StaffQuoteDetailDto> CreateQuoteAsync(CreateStaffQuoteRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        if ((request.RfqId is null) == (request.CompanyId is null)) throw new ValidationException("rfqId", "Give either the request or the company.");
        var staff = await StaffAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The RFQ row is locked so two quotes started from it at once do not both pre-fill the same lines.
        var rfq = request.RfqId is { } rfqId ? await LockRfqAsync(rfqId, ct) : null;
        var companyId = rfq?.CompanyId ?? request.CompanyId!.Value;
        var status = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => (CompanyStatus?)c.Status).FirstOrDefaultAsync(ct)
                     ?? throw new KeyNotFoundException("Company not found.");
        if (status != CompanyStatus.Active) throw new ConflictException("COMPANY_NOT_ACTIVE", "Quotes are made only for active companies.");
        var contact = await ContactAsync(companyId, request.ContactUserId, rfq?.RequestedBy, ct);

        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(now), companyId, rfq?.Id, contact, staff, options, now);   // QT6
        quote.Assign(actor, now);
        if (rfq is not null)
        {
            await db.Entry(rfq).Collection(r => r.Lines).LoadAsync(ct);
            rfq.StartQuoting(now);   // a Closed or Withdrawn request takes no new quote
            if (await PrefillAsync(rfq, now, ct) is { Count: > 0 } lines)
                quote.EditDraft(1, new QuoteDraft(options.DefaultValidityDays, QuotePaymentTerms.AsAccount, QuoteShippingMode.Calculated, null, null, null,
                    null, null, lines), staff, options, now);
        }

        db.Quotes.Add(quote);
        Audit("quote.created", "quotes", quote.Id, null, new
        {
            quote.Number,
            quote.CompanyId,
            quote.RfqId,
            Rfq = rfq?.Number,
            quote.ContactUserId,
            Lines = quote.Version(1).Lines.Count
        }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quote.Id, ct);
    }

    public async Task<StaffQuotePage> ListQuotesAsync(StaffQuoteQuery query, CancellationToken ct = default)
    {
        var (page, size, status, assigned, none, like) = Filters(query);
        var now = Now;
        var rows = from q in db.Quotes.AsNoTracking()
                   join c in db.Companies.AsNoTracking() on q.CompanyId equals c.Id
                   join v in db.QuoteVersions.AsNoTracking() on new { Q = q.Id, N = q.LatestVersionNo } equals new { Q = v.QuoteId, N = v.VersionNo }
                   select new { Q = q, C = c, v.Status, v.ValidUntilUtc };
        rows = status switch
        {
            null => rows,
            "closed" => rows.Where(x => x.Q.ClosedAtUtc != null),
            "expired" => rows.Where(x => x.Q.ClosedAtUtc == null && x.Status == S.Sent && x.ValidUntilUtc <= now),
            "sent" => rows.Where(x => x.Q.ClosedAtUtc == null && x.Status == S.Sent && x.ValidUntilUtc > now),
            _ => Parse<S>(status.Replace("_", string.Empty)) is var s && s != S.Superseded ? rows.Where(x => x.Q.ClosedAtUtc == null && x.Status == s)
                : throw new ValidationException("status", "Unknown status."),
        };
        if (none) rows = rows.Where(x => x.Q.AssignedTo == null);
        else if (assigned is { } a) rows = rows.Where(x => x.Q.AssignedTo == a);
        if (query.CompanyId is { } company) rows = rows.Where(x => x.Q.CompanyId == company);
        if (like is not null) rows = rows.Where(x => EF.Functions.ILike(x.Q.Number, like) || EF.Functions.ILike(x.C.LegalName, like));

        var total = await rows.CountAsync(ct);
        var found = await rows.OrderByDescending(x => x.Q.CreatedAtUtc).ThenBy(x => x.Q.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(found.SelectMany(x => new[] { x.Q.ContactUserId, x.Q.AssignedTo }), ct);
        var rfqs = await RfqNumbersAsync(found.Select(x => x.Q.RfqId), ct);
        return new StaffQuotePage([.. found.Select(x => QuoteSummary(x.Q, x.C, StatusOf(x.Q.ClosedAtUtc, x.Status, x.ValidUntilUtc, now), names, rfqs, now))],
            page, size, total);
    }

    public async Task<StaffQuoteDetailDto> GetQuoteAsync(Guid quoteId, CancellationToken ct = default)
    {
        var quote = await Loaded(db.Quotes.AsNoTracking()).FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw QuoteNotFound();
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == quote.CompanyId, ct);
        var names = await NamesAsync([quote.ContactUserId, quote.AssignedTo], ct);
        var rfqs = await RfqNumbersAsync([quote.RfqId], ct);
        var tokens = await db.QuoteVersions.AsNoTracking().Where(v => v.QuoteId == quoteId)
            .Select(v => new { v.Id, X = EF.Property<uint>(v, "xmin") }).ToDictionaryAsync(v => v.Id, v => v.X, ct);
        var now = Now;
        var latest = quote.Versions.MaxBy(v => v.VersionNo)!;
        return new StaffQuoteDetailDto(QuoteSummary(quote, company, StatusOf(quote.ClosedAtUtc, latest.Status, latest.ValidUntilUtc, now), names, rfqs, now),
            [.. quote.Versions.OrderBy(v => v.VersionNo).Select(v => StaffQuoteViews.Version(v, tokens[v.Id], now, user.HasPermission(Permissions.PricingViewCost)))]);
    }

    public async Task<StaffQuoteDetailDto> ReviseQuoteAsync(Guid quoteId, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        var staff = await StaffAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await LockQuoteAsync(quoteId, ct);
        var lapsed = quote.Versions.FirstOrDefault(v => v.Status == S.Accepted)?.AcceptedOrderId is { } orderId
                     && await db.Orders.AnyAsync(o => o.Id == orderId && o.Status == OrderStatus.Cancelled && o.ConfirmedAtUtc == null, ct);
        var next = quote.Revise(Guid.NewGuid(), staff, lapsed, now);   // QT6, one open version, no live or confirmed order
        Audit("quote.revised", "quotes", quote.Id, new { LatestVersionNo = next.VersionNo - 1 }, new { quote.Number, LatestVersionNo = next.VersionNo }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quoteId, ct);
    }

    public async Task<StaffQuoteDetailDto> CloseQuoteAsync(Guid quoteId, StaffReasonRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await LockQuoteAsync(quoteId, ct);
        var (live, pending) = (quote.Versions.FirstOrDefault(v => v.Status == S.Sent), quote.Versions.FirstOrDefault(v => v.Status == S.PendingApproval));
        quote.Close(actor, request.Reason ?? string.Empty, now);   // the Sent version is withdrawn (outbox QuoteWithdrawnEvent), a pending one recalled
        Audit("quote.closed", "quotes", quote.Id, new { Live = live?.VersionNo, Pending = pending?.VersionNo },
            new { quote.Number, quote.CloseReason, WithdrawnVersionNo = live?.VersionNo, live?.ContentHash, RecalledVersionNo = pending?.VersionNo }, actor, now);
        await SaveAsync(tx, ct);
        return await GetQuoteAsync(quoteId, ct);
    }

    // ---- pre-fill, contact, staff --------------------------------------------------------------------------------------

    /// <summary>
    /// The RFQ's matched lines that no open quote of it carries yet (a BOM of more than 50 lines becomes several quotes), in line order,
    /// whose item is still sold and whose quantity may be ordered as asked; at most 50. The price is the company's own resolver price
    /// now (net, the order path), so an untouched line is within authority; staff change it in the editor (D4.7).
    /// </summary>
    private async Task<List<QuoteLineDraft>> PrefillAsync(Rfq rfq, DateTime now, CancellationToken ct)
    {
        var taken = await (from l in db.Set<QuoteLine>()
                           join v in db.QuoteVersions on l.VersionId equals v.Id
                           join q in db.Quotes on v.QuoteId equals q.Id
                           where q.RfqId == rfq.Id && q.ClosedAtUtc == null && l.RfqLineId != null
                           select l.RfqLineId!.Value).Distinct().ToListAsync(ct);
        var wanted = rfq.Lines.Where(l => l.MatchStatus == RfqMatchStatus.Matched && l.VariantId is not null && !taken.Contains(l.Id))
            .OrderBy(l => l.LineNo).DistinctBy(l => l.VariantId).ToList();
        var ids = wanted.Select(l => l.VariantId!.Value).ToList();
        var catalogue = await (from v in db.ProductVariants.AsNoTracking()
                               join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                               join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                               where ids.Contains(v.Id) && v.Status == VariantStatus.Active && p.Status == ProductStatus.Active && p.DeletedAtUtc == null
                               select new { Variant = v, p.SellerId, p.TitleEn, p.TitleBn, c.VatRate }).ToDictionaryAsync(x => x.Variant.Id, ct);
        var usable = wanted.Where(l => catalogue.TryGetValue(l.VariantId!.Value, out var c) && c.Variant.QuantityProblem(l.Quantity) is null)
            .Take(QuoteLimits.MaxLinesPerVersion).ToList();
        if (usable.Count == 0) return [];

        var resolved = await prices.ResolveAsync(rfq.CompanyId, [.. usable.Select(l => new PriceRequestLine(l.VariantId!.Value, l.Quantity,
            PricingInput.From(catalogue[l.VariantId!.Value].Variant, catalogue[l.VariantId!.Value].VatRate)))], TaxMode.Exclusive, now, ct);
        return [.. usable.Zip(resolved).Where(x => x.Second.Price.UnitPriceMinor > 0 && x.Second.Price.PublicUnitPriceMinor > 0).Select(x =>
        {
            var c = catalogue[x.First.VariantId!.Value];
            var facts = new QuoteLineFacts(c.Variant.Id, true, c.SellerId, c.Variant.Sku, c.Variant.ManufacturerPartNumber, c.TitleEn,
                string.IsNullOrWhiteSpace(c.TitleBn) ? c.TitleEn : c.TitleBn, c.VatRate, x.Second.Price.PublicUnitPriceMinor, null);
            return new QuoteLineDraft(c.Variant.Id, x.First.Quantity, x.Second.Price.UnitPriceMinor, facts, RfqLineId: x.First.Id);
        })];
    }

    /// <summary>
    /// The buyer who gets the quote (§3, QT6): by default the RFQ's requester while still an effective member; a contact staff choose
    /// must be an effective member of the quote's company with the Buyer or Admin role.
    /// </summary>
    private async Task<Guid> ContactAsync(Guid companyId, Guid? chosen, Guid? requester, CancellationToken ct)
    {
        var id = chosen ?? requester ?? throw new ValidationException("contactUserId", "Choose the buyer contact for this quote.");
        var member = await BuyerQuoteAccess.FindAsync(db, id, companyId, ct);
        if (member is null || (chosen is not null && (member.Roles & (CompanyRoles.Buyer | CompanyRoles.Admin)) == 0))
            throw new ValidationException("contactUserId", "The contact must be a current member of this company; a chosen contact needs the Buyer or Admin role.");
        return id;
    }

    private async Task<QuoteActor> StaffAsync(Guid actor, CancellationToken ct) => new(actor, await BuyerQuoteAccess.CompanyIdsAsync(db, actor, ct));

    // ---- locks, save, audit --------------------------------------------------------------------------------------------

    private async Task<Rfq> LockRfqAsync(Guid rfqId, CancellationToken ct) =>
        (await db.Rfqs.FromSqlInterpolated($"SELECT r.*, r.xmin FROM rfqs r WHERE r.id = {rfqId} FOR NO KEY UPDATE").ToListAsync(ct)).SingleOrDefault()
        ?? throw RfqNotFound();

    /// <summary>§9: the quote row, then its versions in version order, FOR NO KEY UPDATE; then the aggregate is read under those locks.</summary>
    private async Task<Quote> LockQuoteAsync(Guid quoteId, CancellationToken ct)
    {
        if ((await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM quotes WHERE id = {quoteId} FOR NO KEY UPDATE").ToListAsync(ct)).Count == 0)
            throw QuoteNotFound();
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM quote_versions WHERE quote_id = {quoteId} ORDER BY version_no FOR NO KEY UPDATE").ToListAsync(ct);
        return await Loaded(db.Quotes).SingleAsync(q => q.Id == quoteId, ct);
    }

    private static IQueryable<Quote> Loaded(IQueryable<Quote> quotes) => quotes
        .Include(q => q.Versions.OrderBy(v => v.VersionNo)).ThenInclude(v => v.Lines)
        .Include(q => q.Versions).ThenInclude(v => v.Contributors)
        .AsSplitQuery();

    private async Task SaveAsync(IDbContextTransaction tx, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);   // the one-live exclusion is deferred: it fires here
        }
        catch (Exception ex) when (IsRace(ex, out var constraint))
        {
            db.ChangeTracker.Clear();
            throw constraint == "ux_quotes_number"
                ? new ConflictException("QUOTE_NUMBER_CLASH", "The quote could not be numbered. Try again.")
                : new ConflictException("QUOTE_VERSION_CHANGED", "Someone changed this quote at the same time. Reload it and try again.");
        }
    }

    /// <summary>A unique (23505) or exclusion (23P01) violation, from SaveChanges or from the commit of a deferred constraint.</summary>
    internal static bool IsRace(Exception ex, out string? constraint)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        constraint = pg?.ConstraintName;
        return pg?.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation;
    }

    private void Audit(string action, string table, Guid id, object? before, object after, Guid actor, DateTime now) =>
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, action, table, id.ToString(),
            before is null ? null : JsonSerializer.Serialize(before), JsonSerializer.Serialize(after), user.CorrelationId, now));

    // ---- reads -----------------------------------------------------------------------------------------------------------

    private (int Page, int Size, string? Status, Guid? Assigned, bool None, string? Like) Filters(StaffQuoteQuery q)
    {
        var status = string.IsNullOrWhiteSpace(q.Status) ? null : q.Status.Trim().ToLowerInvariant();
        var assigned = q.Assigned?.Trim().ToLowerInvariant();
        Guid? who = assigned switch
        {
            null or "" or "none" => null,
            "me" => user.UserId,
            _ => Guid.TryParse(assigned, out var id) ? id : throw new ValidationException("assigned", "Use me, none or a staff user id."),
        };
        var text = q.Q?.Trim();
        if (text is { Length: > 100 }) throw new ValidationException("q", "Search with at most 100 characters.");
        var like = string.IsNullOrEmpty(text) ? null : "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return (Math.Max(1, q.Page), Math.Clamp(q.PageSize, 1, MaxPageSize), status, who, assigned == "none", like);
    }

    private static T Parse<T>(string value, string field = "status") where T : struct, Enum =>
        !int.TryParse(value, out _) && Enum.TryParse<T>(value.Replace("_", string.Empty), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ValidationException(field, $"Unknown {field}.");

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private async Task<Dictionary<Guid, string>> RfqNumbersAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Rfqs.AsNoTracking().Where(r => wanted.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
    }

    private StaffRfqSummaryDto RfqSummary(Rfq r, string company, Dictionary<Guid, string> names)
    {
        var budget = options.FirstQuoteSlaWorkingHours * 60;
        var waited = QuoteSla.WorkingMinutes(r.CreatedAtUtc, r.FirstQuoteSentAtUtc ?? r.ClosedAtUtc ?? r.WithdrawnAtUtc ?? Now, options.SlaHolidays);
        return new(r.Id, r.Number, StaffQuoteViews.Wire(r.Status), StaffQuoteViews.Wire(r.Source), r.CompanyId, company, r.RequestedBy,
            names.GetValueOrDefault(r.RequestedBy, string.Empty), r.AssignedTo, r.AssignedTo is { } a ? names.GetValueOrDefault(a) : null, r.LineCount,
            r.NeededBy, r.CreatedAtUtc, r.FirstQuoteSentAtUtc, QuoteSla.DueAtUtc(r.CreatedAtUtc, budget, options.SlaHolidays), waited, waited > budget,
            r.ClosedAtUtc, r.CloseReason, r.WithdrawnAtUtc);
    }

    private static StaffQuoteSummaryDto QuoteSummary(Quote q, Company c, string status, Dictionary<Guid, string> names, Dictionary<Guid, string> rfqs, DateTime now) =>
        new(q.Id, q.Number, status, q.CompanyId, c.LegalName, StaffQuoteViews.Wire(c.CreditTerms), c.CreditHoldSinceUtc is not null && !c.CreditHoldLifted(now),
            q.RfqId, q.RfqId is { } r ? rfqs.GetValueOrDefault(r) : null, q.ContactUserId, names.GetValueOrDefault(q.ContactUserId, string.Empty),
            q.AssignedTo, q.AssignedTo is { } a ? names.GetValueOrDefault(a) : null, q.LatestVersionNo, q.CreatedAtUtc, q.UpdatedAtUtc ?? q.CreatedAtUtc, q.ClosedAtUtc, q.CloseReason);

    private static string StatusOf(DateTime? closedAt, S latest, DateTime? validUntil, DateTime now) =>
        closedAt is not null ? "closed" : latest == S.Sent && validUntil is { } until && !QuoteValidity.IsValidAt(until, now) ? "expired" : StaffQuoteViews.Wire(latest);
}
