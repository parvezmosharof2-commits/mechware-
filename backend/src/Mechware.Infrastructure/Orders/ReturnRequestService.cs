using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Inventory;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// F2.1 return requests. Who may see what: a buyer sees and returns only orders they placed themselves (the rule of
/// <c>GET /orders/by-number</c>); for a company order they must also still be an effective member of that company and name it. Anything
/// else is the same 404. Opening and withdrawing lock the <c>orders</c> row first (FOR NO KEY UPDATE, like F1.1 and E7.2), so all
/// requests of one order queue up and the delivered-minus-claimed check cannot be passed twice; no payment, company or promotion row
/// is locked, so no cycle with the global order (payments → orders → companies → promotions). The F2.3a staff moves take the same order
/// lock first, then the return row (FOR NO KEY UPDATE), and touch inventory only after the commit (its own transactions).
/// </summary>
public sealed partial class ReturnRequestService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock, ReturnOptions? options = null,
    IInventoryService? inventory = null, ILogger<ReturnRequestService>? log = null)
    : IReturnRequestService
{
    /// <summary>The stock reference type of a return's restock: key (return, return id, ReturnRestocked, variant), capped by its order.</summary>
    public const string ReturnReference = "return";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReturnOptions settings = ReturnOptions.Validated(options);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,100}$")]
    private static partial Regex KeyPattern();

    public async Task<OpenedReturnDto> OpenAsync(OpenReturnRequest request, CancellationToken ct = default)
    {
        if (!KeyPattern().IsMatch(request.IdempotencyKey ?? string.Empty))
            throw new ValidationException("idempotencyKey", "An Idempotency-Key of 8 to 100 letters, digits, '-' or '_' is required.");
        var key = request.IdempotencyKey!;
        var lines = ReturnRequest.NormaliseLines(request.Lines?.Select(l => new ReturnLineRequest(l.OrderItemId, l.Quantity)).ToList());
        var reason = ReturnRequest.ParseReason(request.ReasonCode);
        var note = ReturnRequest.NormaliseNote(request.Note, reason);
        if (!OrderNumber.IsValid(request.OrderNumber)) throw new KeyNotFoundException("Order not found.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.OrderNumber}|{request.CompanyId}|{reason}|{note}|{string.Join(';', lines.Select(l => $"{l.OrderItemId:N}x{l.Quantity}"))}")));

        if (await ReplayAsync(request.UserId, key, hash, ct) is { } early) return early;
        var now = Now();
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = (await db.Orders.FromSqlInterpolated(
                $"SELECT o.*, o.xmin FROM orders o WHERE o.order_number = {request.OrderNumber} AND o.user_id = {request.UserId} FOR NO KEY UPDATE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault() ?? throw new KeyNotFoundException("Order not found.");
        if (order.CompanyId != request.CompanyId) throw new KeyNotFoundException("Order not found.");
        if (order.CompanyId is { } companyId
            && !await db.CompanyMembers.AsNoTracking().Effective().AnyAsync(m => m.CompanyId == companyId && m.UserId == request.UserId, ct))
            throw new KeyNotFoundException("Order not found.");
        // A parallel request with the same key that committed while this one waited for the order lock.
        if (await ReplayAsync(request.UserId, key, hash, ct) is { } replay) return replay;

        var deliveredAt = await db.Set<Shipment>().Where(s => s.OrderId == order.Id).MaxAsync(s => s.DeliveredAtUtc, ct)
                          ?? await db.OrderEvents.Where(e => e.OrderId == order.Id && e.ToStatus == OrderStatus.Delivered).MaxAsync(e => (DateTime?)e.AtUtc, ct);
        var orderLines = await OrderLinesAsync(order.Id, ct);
        var opened = ReturnRequest.Open(order.Id, order.OrderNumber, order.Status, deliveredAt, order.CompanyId, request.UserId, reason, note,
            lines, orderLines, settings.WindowDays, now, key, hash);
        db.ReturnRequests.Add(opened);
        db.AuditLogs.Add(Audit("return.requested", opened, null, now));
        try
        {
            await db.SaveChangesAsync(ct);   // the request, its lines, the outbox event, the audit row
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_return_requests_idempotency" })
        {
            throw new ConflictException("IDEMPOTENCY_RACE", "Another request with this key is in progress.");
        }
        await tx.CommitAsync(ct);
        return new OpenedReturnDto((await ToDtosAsync([opened], ct))[0], false);
    }

    /// <summary>Each line of the order with its delivered quantity, what open returns already claim and whether its category may be returned.</summary>
    private async Task<List<ReturnableLine>> OrderLinesAsync(Guid orderId, CancellationToken ct)
    {
        var items = await (from i in db.OrderItems
                           join s in db.SubOrders on i.SubOrderId equals s.Id
                           where s.OrderId == orderId
                           select new
                           {
                               i.Id,
                               i.Quantity,
                               Path = (from v in db.ProductVariants
                                       join p in db.Products on v.ProductId equals p.Id
                                       join c in db.Categories on p.CategoryId equals c.Id
                                       where v.Id == i.VariantId
                                       select c.MaterializedPath).FirstOrDefault(),
                               Claimed = db.ReturnRequests.Where(r => r.Status != ReturnStatus.Withdrawn && r.Status != ReturnStatus.Rejected)
                                   .SelectMany(r => r.Lines).Where(l => l.OrderItemId == i.Id).Sum(l => (int?)l.Quantity) ?? 0
                           }).ToListAsync(ct);
        var blocked = settings.NonReturnableSlugs();
        return items.Select(i => new ReturnableLine(i.Id, i.Quantity, i.Claimed,
            blocked.Count == 0 || i.Path is null || !i.Path.Split('.').Any(blocked.Contains))).ToList();
    }

    private async Task<OpenedReturnDto?> ReplayAsync(Guid userId, string key, string hash, CancellationToken ct)
    {
        var earlier = await db.ReturnRequests.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.RequestedBy == userId && r.IdempotencyKey == key, ct);
        if (earlier is null) return null;
        if (earlier.RequestHash != hash)
            throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request.");
        return new OpenedReturnDto((await ToDtosAsync([earlier], ct))[0], true);
    }

    public async Task<ReturnRequestDto> WithdrawAsync(Guid userId, Guid returnId, CancellationToken ct = default)
    {
        var orderId = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId && r.RequestedBy == userId)
            .Select(r => (Guid?)r.OrderId).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Return not found.");
        var now = Now();
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orderNumber = (await db.Database.SqlQuery<string>($"SELECT order_number AS \"Value\" FROM orders WHERE id = {orderId} FOR NO KEY UPDATE").ToListAsync(ct)).Single();
        var request = await db.ReturnRequests.Include(r => r.Lines).SingleAsync(r => r.Id == returnId, ct);
        if (request.Withdraw(orderNumber, now))
        {
            db.AuditLogs.Add(Audit("return.withdrawn", request, nameof(ReturnStatus.Requested), now));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await ToDtosAsync([request], ct))[0];
    }

    public Task<PagedResult<ReturnRequestDto>> ListOwnAsync(Guid userId, int page, int pageSize, CancellationToken ct = default) =>
        PageAsync(db.ReturnRequests.Where(r => r.RequestedBy == userId), page, pageSize, ToDtosAsync, ct);

    public async Task<ReturnRequestDto> GetOwnAsync(Guid userId, Guid returnId, CancellationToken ct = default) =>
        await OneAsync(db.ReturnRequests.Where(r => r.Id == returnId && r.RequestedBy == userId), ToDtosAsync, ct);

    public Task<StaffReturnDto> ApproveAsync(Guid returnId, ReturnStepRequest request, CancellationToken ct = default) =>
        MoveAsync(returnId, request.RowVersion, "return.approved", (r, number, actor, now) => r.Approve(actor, number, now), ct);

    public Task<StaffReturnDto> RejectAsync(Guid returnId, RejectReturnRequest request, CancellationToken ct = default)
    {
        var reason = ReturnRequest.NormaliseRejectionReason(request.Reason);
        return MoveAsync(returnId, request.RowVersion, "return.rejected", (r, number, actor, now) => r.Reject(actor, reason, number, now), ct);
    }

    public Task<StaffReturnDto> MarkReceivedAsync(Guid returnId, ReturnStepRequest request, CancellationToken ct = default) =>
        MoveAsync(returnId, request.RowVersion, "return.received", (r, number, actor, now) => r.MarkReceived(actor, number, now), ct);

    public async Task<StaffReturnDto> InspectAsync(Guid returnId, InspectReturnRequest request, CancellationToken ct = default)
    {
        var stock = inventory ?? throw new InvalidOperationException("Inspecting a return needs the inventory service.");
        var lines = ReturnRequest.NormaliseInspection(request.Lines?.Select(l => new ReturnInspection(l.ReturnLineId, l.AcceptedQty, l.RestockQty, l.Note)).ToList());
        var inspected = await MoveAsync(returnId, request.RowVersion, "return.inspected", (r, number, actor, now) => r.Inspect(actor, lines, number, now), ct);

        // After the commit, never inside it (RestockAsync opens its own transaction) and not cancellable by the caller going away: the
        // inspection stands whatever happens next. A failure leaves RestockPending; the E7.3 sweep (case return_restock) finishes it.
        try
        {
            await RestockAsync(db, stock, returnId, user.UserId, []);
            return inspected with { RestockPending = false };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogError(ex, "Return {ReturnId} is inspected but its stock was not put back; the stock sweep finishes it", returnId);
            return inspected;
        }
    }

    /// <summary>
    /// The post-commit restock of an inspected return, shared with the E7.3 sweep (actor null = system): per variant once, the sum of its
    /// lines' restock quantities, key (return, return id, ReturnRestocked, variant), against the order's deduction (E7.1 warehouse rule and
    /// cap: never more than the order's committed holds took minus everything already put back against that order). Idempotent; lines
    /// actually put back (not replays) are added to <paramref name="restocked"/>; it throws on the first failure, leaving the rest for a retry.
    /// </summary>
    internal static async Task RestockAsync(MechwareDbContext db, IInventoryService inventory, Guid returnId, Guid? actor, List<ReturnedStockDto> restocked)
    {
        var plan = await (from l in db.Set<ReturnRequestLine>()
                          join r in db.ReturnRequests on l.ReturnRequestId equals r.Id
                          join i in db.OrderItems on l.OrderItemId equals i.Id
                          where r.Id == returnId && r.InspectedAtUtc != null && l.RestockQuantity > 0
                          group l by new { r.OrderId, i.VariantId } into g
                          select new { g.Key.OrderId, g.Key.VariantId, Quantity = g.Sum(x => x.RestockQuantity ?? 0) })
            .ToListAsync(CancellationToken.None);
        foreach (var line in plan.OrderBy(p => p.VariantId))
        {
            var result = await inventory.RestockAsync(new RestockRequest(line.VariantId, line.Quantity, RestockReason.ReturnRestocked, ReturnReference,
                returnId, ActorUserId: actor, AgainstOrderId: line.OrderId), CancellationToken.None);
            if (!result.AlreadyApplied) restocked.Add(new ReturnedStockDto(line.VariantId, line.Quantity));
        }
    }

    /// <summary>
    /// One staff move in one transaction: the order row (FOR NO KEY UPDATE, as F2.1: every return of the order queues behind it, so a
    /// parallel withdraw and approve have exactly one winner), then the return row, the rowVersion check, the move, the audit row and the
    /// outbox event (domain-event interceptor).
    /// </summary>
    private async Task<StaffReturnDto> MoveAsync(
        Guid returnId, uint? rowVersion, string auditAction, Action<ReturnRequest, string, Guid, DateTime> move, CancellationToken ct)
    {
        var actor = user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        if (rowVersion is not { } expected) throw new ValidationException("rowVersion", "Send the rowVersion of the return you loaded.");
        var orderId = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId).Select(r => (Guid?)r.OrderId).FirstOrDefaultAsync(ct)
                      ?? throw new KeyNotFoundException("Return not found.");
        var now = Now();
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orderNumber = (await db.Database.SqlQuery<string>($"SELECT order_number AS \"Value\" FROM orders WHERE id = {orderId} FOR NO KEY UPDATE").ToListAsync(ct)).Single();
        var request = (await db.ReturnRequests.FromSqlInterpolated($"SELECT r.*, r.xmin FROM return_requests r WHERE r.id = {returnId} FOR NO KEY UPDATE")
            .Include(r => r.Lines).ToListAsync(ct)).Single();
        if (db.Entry(request).Property<uint>("xmin").CurrentValue != expected)
            throw new ConflictException("RETURN_CHANGED", "Someone changed this return since you loaded it. Reload it and try again.");
        var from = request.Status;
        move(request, orderNumber, actor, now);   // 409 RETURN_INVALID_TRANSITION from a wrong status
        db.AuditLogs.Add(Audit(auditAction, request, from.ToString(), now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("RETURN_CHANGED", "Someone changed this return at the same time. Reload it and try again.");
        }
        await tx.CommitAsync(ct);
        return (await ToStaffDtosAsync([request], ct))[0];
    }

    public Task<PagedResult<StaffReturnDto>> ListStaffAsync(StaffReturnFilter filter, CancellationToken ct = default)
    {
        var q = db.ReturnRequests.AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = Enum.GetValues<ReturnStatus>().Cast<ReturnStatus?>().FirstOrDefault(s => OrderService.ToWire(s!.Value) == filter.Status.Trim())
                         ?? throw new ValidationException("status", "Unknown return status.");
            q = q.Where(r => r.Status == status);
        }
        if (filter.CompanyId is { } companyId) q = q.Where(r => r.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var prefix = filter.OrderNumber.Trim().ToUpperInvariant();
            if (prefix.Length > 16 || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                throw new ValidationException("orderNumber", "An order number (or its start) has letters, digits and dashes only.");
            q = q.Where(r => db.Orders.Any(o => o.Id == r.OrderId && o.OrderNumber.StartsWith(prefix)));
        }
        return PageAsync(q, filter.Page, filter.PageSize, ToStaffDtosAsync, ct);
    }

    public async Task<StaffReturnDto> GetStaffAsync(Guid returnId, CancellationToken ct = default) =>
        await OneAsync(db.ReturnRequests.Where(r => r.Id == returnId), ToStaffDtosAsync, ct);

    private delegate Task<List<T>> Mapper<T>(IReadOnlyList<ReturnRequest> rows, CancellationToken ct);

    private static async Task<T> OneAsync<T>(IQueryable<ReturnRequest> q, Mapper<T> map, CancellationToken ct)
    {
        var request = await q.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Return not found.");
        return (await map([request], ct))[0];
    }

    private static async Task<PagedResult<T>> PageAsync<T>(IQueryable<ReturnRequest> q, int page, int pageSize, Mapper<T> map, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, IReturnRequestService.MaxPageSize);
        var total = await q.CountAsync(ct);
        var rows = await q.AsNoTracking().Include(r => r.Lines).OrderByDescending(r => r.RequestedAtUtc).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(await map(rows, ct), page, pageSize, total);
    }

    /// <summary>Order numbers and the line snapshots (SKU, title, variant) of the order lines, two small queries for the whole page.</summary>
    private async Task<(Dictionary<Guid, string> Numbers, Dictionary<Guid, OrderItem> Items)> NamesAsync(IReadOnlyList<ReturnRequest> rows, CancellationToken ct)
    {
        var orderIds = rows.Select(r => r.OrderId).Distinct().ToList();
        var itemIds = rows.SelectMany(r => r.Lines).Select(l => l.OrderItemId).Distinct().ToList();
        return (await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct),
            await db.OrderItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct));
    }

    /// <summary>The buyer's view: a whitelist without staff ids, inspection notes or restock figures.</summary>
    private async Task<List<ReturnRequestDto>> ToDtosAsync(IReadOnlyList<ReturnRequest> rows, CancellationToken ct)
    {
        var (numbers, items) = await NamesAsync(rows, ct);
        return rows.Select(r => new ReturnRequestDto(r.Id, r.OrderId, numbers[r.OrderId], r.CompanyId, r.RequestedBy, OrderService.ToWire(r.Status),
            OrderService.ToWire(r.Reason), r.Note, r.RequestedAtUtc, r.WithdrawnAtUtc, r.DecidedAtUtc, r.RejectionReason, r.ReceivedAtUtc, r.InspectedAtUtc,
            r.Lines.OrderBy(l => items[l.OrderItemId].Sku).Select(l => new ReturnLineDto(l.OrderItemId, items[l.OrderItemId].VariantId,
                items[l.OrderItemId].Sku, items[l.OrderItemId].Title, l.Quantity, l.AcceptedQuantity)).ToList())).ToList();
    }

    /// <summary>The staff view: everything, the rowVersion, and whether restocked units are still not back on the shelf.</summary>
    private async Task<List<StaffReturnDto>> ToStaffDtosAsync(IReadOnlyList<ReturnRequest> rows, CancellationToken ct)
    {
        var (numbers, items) = await NamesAsync(rows, ct);
        var ids = rows.Select(r => r.Id).ToList();
        var versions = await db.ReturnRequests.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, Version = EF.Property<uint>(r, "xmin") }).ToDictionaryAsync(r => r.Id, r => r.Version, ct);
        var restocked = (await (from m in db.StockMovements
                                join i in db.InventoryItems on m.InventoryItemId equals i.Id
                                where m.ReferenceType == ReturnReference && m.Reason == RestockReason.ReturnRestocked && ids.Contains(m.ReferenceId)
                                select new { m.ReferenceId, i.VariantId }).Distinct().ToListAsync(ct))
            .Select(x => (x.ReferenceId, x.VariantId)).ToHashSet();
        return rows.Select(r => new StaffReturnDto(r.Id, r.OrderId, numbers[r.OrderId], r.CompanyId, r.RequestedBy, OrderService.ToWire(r.Status),
            OrderService.ToWire(r.Reason), r.Note, r.RequestedAtUtc, r.WithdrawnAtUtc, r.DecidedBy, r.DecidedAtUtc, r.RejectionReason,
            r.ReceivedBy, r.ReceivedAtUtc, r.InspectedBy, r.InspectedAtUtc,
            r.InspectedAtUtc is not null && r.Lines.Any(l => l.RestockQuantity > 0 && !restocked.Contains((r.Id, items[l.OrderItemId].VariantId))),
            versions[r.Id],
            r.Lines.OrderBy(l => items[l.OrderItemId].Sku).Select(l => new StaffReturnLineDto(l.Id, l.OrderItemId, items[l.OrderItemId].VariantId,
                items[l.OrderItemId].Sku, items[l.OrderItemId].Title, l.Quantity, l.AcceptedQuantity, l.RestockQuantity, l.InspectionNote)).ToList())).ToList();
    }

    /// <summary>The clock at the database's microsecond precision, so an answer shows exactly what was stored.</summary>
    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % 10));
    }

    private AuditLog Audit(string action, ReturnRequest r, string? fromStatus, DateTime now) =>
        new(Guid.NewGuid(), user.UserId ?? r.RequestedBy, user.IpAddress, action, "return_requests", r.Id.ToString(),
            fromStatus is null ? null : JsonSerializer.Serialize(new { Status = fromStatus }, Json),
            JsonSerializer.Serialize(new
            {
                Status = r.Status.ToString(),
                r.OrderId,
                r.CompanyId,
                Reason = r.Reason.ToString(),
                r.RejectionReason,
                Lines = r.Lines.Select(l => new { l.Id, l.OrderItemId, l.Quantity, l.AcceptedQuantity, l.RestockQuantity, l.InspectionNote })
            }, Json), user.CorrelationId, now);
}
