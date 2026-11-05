using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Application.Inventory;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Approvals;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Companies;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Pricing;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Orders;

public sealed partial class OrderService(
    ShopVerseDbContext db, IInventoryService inventory, TimeProvider clock, Func<DateTime, string>? orderNumbers = null) : IOrderService
{
    private readonly Func<DateTime, string> _nextOrderNumber = orderNumbers ?? (now => OrderNumber.Generate(now));

    [GeneratedRegex("^[A-Za-z0-9_-]{8,100}$")]
    private static partial Regex KeyPattern();

    public async Task<PlacedOrder> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var lines = Normalise(request);
        var hash = HashOf(request, lines);

        // A buyer's order requests queue up here, one at a time. With the same key this stops a client retrying while its
        // first request is still reserving stock from losing the stock race and being told "insufficient stock" instead
        // of getting its own order back. Across keys it makes the open-unpaid-order cap exact: parallel requests cannot all
        // count the same "2 open orders" and all pass. The connection is held open so the session-level lock and the
        // later statements share one database session.
        await db.Database.OpenConnectionAsync(ct);
        var lockKey = $"order:{request.UserId:N}";
        // Orders charged to one cost centre also queue up, so two buyers cannot both fit under the same monthly budget.
        // Always taken after the buyer's lock, in this order, so two requests can never wait on each other.
        var centreKey = request.CostCentreId is { } centre ? $"cost-centre:{centre:N}" : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({lockKey}, 0))", ct);
        try
        {
            if (centreKey is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({centreKey}, 0))", ct);
            try
            {
                return await PlaceLockedAsync(request, lines, hash, ct);
            }
            finally
            {
                if (centreKey is not null)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({centreKey}, 0))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({lockKey}, 0))", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<PlacedOrder> PlaceLockedAsync(PlaceOrderRequest request, List<PlaceOrderLine> lines, string hash, CancellationToken ct)
    {
        if (await ReplayAsync(request.UserId, request.IdempotencyKey, hash, ct) is { } replay) return replay;

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
        var shipTo = await ResolveShipToAsync(request, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var mode = request.CompanyId is null ? TaxMode.Inclusive : TaxMode.Exclusive;
        var (priced, applied, categories) = await PriceAsync(request, lines, mode, now, ct);

        // A company order that one of the company's approval rules covers waits for that approval before it can be paid or
        // charged to credit (threat model v1, H1). The amount is the server's own total, never a number from the client.
        var approvalRule = request.CompanyId is { } forCompany
            ? await FindApprovalRuleAsync(request, forCompany, priced.GrandTotalMinor, categories, ct)
            : null;
        var initialStatus = approvalRule is null ? OrderStatus.PendingPayment : OrderStatus.PendingApproval;

        // Spend limits and budgets (threat model v1, M3): going over one is allowed only through an approval. With no rule to
        // route it to an approver, the order is refused rather than slipping past the limit.
        if (member is not null && approvalRule is null)
        {
            if (member.SpendLimitMinor is { } limit && priced.GrandTotalMinor > limit)
                throw new ConflictException("SPEND_LIMIT_EXCEEDED",
                    "This order is above your spending limit and no approval rule covers it. Ask a company admin.");
            if (request.CostCentreId is { } centre && await OverBudgetAsync(centre, priced.GrandTotalMinor, now, ct))
                throw new ConflictException("BUDGET_EXCEEDED",
                    "This order would take the cost centre over its monthly budget and no approval rule covers it.");
        }

        // A rare clash of two random order numbers is retried with a fresh one rather than shown to the buyer.
        for (var attempt = 0; ; attempt++)
        {
            var orderId = Guid.NewGuid();
            var reserved = await ReserveAsync(orderId, lines, approvalRule is null ? UnpaidOrderLifetime : ApprovalHoldLifetime + UnpaidOrderLifetime, ct);

            var order = Order.Place(orderId, _nextOrderNumber(now), request.UserId, request.CompanyId, priced, mode,
                shipTo, initialStatus, now, request.PayOnCredit, now + UnpaidOrderLifetime, request.CostCentreId);
            db.Orders.Add(order);
            db.IdempotencyKeys.Add(new IdempotencyRecord(request.UserId, request.IdempotencyKey, hash, orderId));

            try
            {
                await SaveAsync(order, request, applied, approvalRule, categories, now, ct);
                // Credit orders that need no approval are confirmed at once (brief §8.5): the held stock becomes a real deduction.
                if (order.Status == OrderStatus.Confirmed) await inventory.CommitAsync(orderId, ct);

                return new PlacedOrder(order.Id, order.OrderNumber, ToWire(order.Status), order.GrandTotalMinor, reserved, false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                // Nothing was committed, so give the stock back before deciding what the clash means.
                db.ChangeTracker.Clear();
                await inventory.CancelAsync(orderId, CancellationToken.None);

                if (pg.ConstraintName == "ux_idempotency_user_key")
                {
                    // A parallel request with the same key won the race: answer with its order.
                    return await ReplayAsync(request.UserId, request.IdempotencyKey, hash, ct)
                           ?? throw new ConflictException("IDEMPOTENCY_RACE", "Another request with this key is in progress.");
                }

                if (pg.ConstraintName == "ux_orders_number" && attempt < 4) continue;
                throw;
            }
            catch
            {
                db.ChangeTracker.Clear();
                await inventory.CancelAsync(orderId, CancellationToken.None);
                throw;
            }
        }
    }

    /// <summary>
    /// Placing an order holds stock, so only a verified, active account may do it, and only up to
    /// <see cref="PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer"/> orders may wait for payment at once. Credit orders are
    /// confirmed immediately and hold nothing, so they are not refused by the cap. Called under the per-buyer lock.
    /// </summary>
    private async Task RequireBuyerMayHoldStockAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        var status = await db.Users.AsNoTracking().Where(u => u.Id == request.UserId && u.DeletedAtUtc == null)
            .Select(u => (Domain.Identity.UserStatus?)u.Status).FirstOrDefaultAsync(ct);
        if (status != Domain.Identity.UserStatus.Active)
            throw new ForbiddenException("ACCOUNT_NOT_VERIFIED", "Verify your e-mail address or phone number before placing an order.");

        if (request.PayOnCredit) return;

        var open = await db.Orders.AsNoTracking()
            .CountAsync(o => o.UserId == request.UserId && (o.Status == OrderStatus.PendingPayment || o.Status == OrderStatus.PendingApproval), ct);
        if (open >= PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer)
            throw new ConflictException("TOO_MANY_UNPAID_ORDERS",
                $"You already have {open} orders waiting for payment. Pay or cancel one before placing another.");
    }

    /// <summary>
    /// The delivery address, copied from the buyer's address book or the company's delivery sites and serialised by the
    /// server. Finding #4 of the 2026-11-04 test: the client used to send the address as free JSON, so invalid JSON was a
    /// 500, two megabytes were stored, and an order could be placed with no address at all. Someone else's address or
    /// site answers "not found", like any other id the caller cannot see. Called after the company check.
    /// </summary>
    private async Task<string> ResolveShipToAsync(PlaceOrderRequest r, CancellationToken ct)
    {
        if (r.ShippingAddressId is not null && r.ShipToSiteId is not null)
            throw new ValidationException("shipTo", "Give either a saved address or a company site, not both.");
        if (r.ShipToSiteId is not null && r.CompanyId is null)
            throw new ValidationException("shipToSiteId", "A company site can only be used for an order in the company's name.");

        if (r.ShippingAddressId is { } addressId)
        {
            var address = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == r.UserId, ct)
                          ?? throw new KeyNotFoundException("Shipping address not found.");
            return ShipToFromAddress(address);
        }

        if (r.CompanyId is { } companyId)
        {
            var site = await db.CompanySites.AsNoTracking()
                .Where(s => s.CompanyId == companyId && (r.ShipToSiteId == null ? s.IsDefaultDelivery : s.Id == r.ShipToSiteId))
                .FirstOrDefaultAsync(ct);
            if (site is not null) return ShipToFromSite(site);
            if (r.ShipToSiteId is not null) throw new KeyNotFoundException("Delivery site not found.");
        }

        var fallback = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == r.UserId && a.IsDefaultShipping, ct);
        return fallback is not null
            ? ShipToFromAddress(fallback)
            : throw new ValidationException("shippingAddressId", "Add a shipping address before placing an order.");
    }

    private static string ShipToFromAddress(Domain.Identity.Address a) => System.Text.Json.JsonSerializer.Serialize(new
    {
        source = "address",
        sourceId = a.Id,
        recipientName = a.RecipientName,
        phone = a.Phone,
        line1 = a.Line1,
        line2 = a.Line2,
        area = a.Area,
        upazila = a.Upazila,
        district = a.District,
        division = a.Division,
        postalCode = a.PostalCode,
        country = a.Country
    });

    private static string ShipToFromSite(Domain.Companies.CompanySite s) => System.Text.Json.JsonSerializer.Serialize(new
    {
        source = "company_site",
        sourceId = s.Id,
        siteName = s.Name,
        recipientName = s.ContactName,
        phone = s.ContactPhone,
        line1 = s.Line1,
        line2 = s.Line2,
        upazila = s.Upazila,
        district = s.District,
        division = s.Division,
        postalCode = s.PostalCode,
        country = "BD"
    });

    /// <summary>
    /// An order in a company's name needs a buyer of that company and a verified, unsuspended company. A user who is not
    /// a member gets "not found", the same as for any other company they cannot see.
    /// </summary>
    private async Task<CompanyMember> RequireCompanyMayBuyAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        var member = await Companies.CompanyAccess.RequireMemberAsync(db, userId, companyId, Domain.Companies.CompanyRoles.Buyer, ct);
        await Companies.CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);
        var status = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Status).FirstAsync(ct);
        if (status != Domain.Companies.CompanyStatus.Active)
            throw new ConflictException("COMPANY_NOT_VERIFIED", "The company has not been verified by MECHWARE yet.");
        return member;
    }

    public static readonly TimeSpan UnpaidOrderLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long an order waits for its company approval before it lapses and its stock goes back on the shelf.</summary>
    public static readonly TimeSpan ApprovalHoldLifetime = TimeSpan.FromHours(24);

    /// <summary>The company's rule for this order, if any. A cost centre, when given, must belong to the company.</summary>
    private async Task<ApprovalRule?> FindApprovalRuleAsync(
        PlaceOrderRequest request, Guid companyId, long amountMinor, IReadOnlyList<Guid> categories, CancellationToken ct)
    {
        if (request.CostCentreId is { } cc && !await db.CostCentres.AnyAsync(c => c.Id == cc && c.CompanyId == companyId, ct))
            throw new KeyNotFoundException("Cost centre not found.");
        var rules = await db.ApprovalRules.AsNoTracking().Where(r => r.CompanyId == companyId && r.IsActive).ToListAsync(ct);
        return ApprovalRule.Select(rules, amountMinor, request.CostCentreId, categories.ToList());
    }

    private static readonly TimeZoneInfo Dhaka = TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka-fixed", TimeSpan.FromHours(6), "Dhaka", "Dhaka");

    /// <summary>
    /// Whether this order would take the cost centre past its budget for the current month in Bangladesh time. Orders that
    /// were cancelled do not count; orders waiting for approval or payment do, since they may still go through.
    /// </summary>
    private async Task<bool> OverBudgetAsync(Guid costCentreId, long amountMinor, DateTime now, CancellationToken ct)
    {
        var budget = await db.CostCentres.AsNoTracking().Where(c => c.Id == costCentreId).Select(c => c.MonthlyBudgetMinor).FirstOrDefaultAsync(ct);
        if (budget is null) return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(now, Dhaka);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified), Dhaka);
        var spent = await db.Orders.AsNoTracking()
            .Where(o => o.CostCentreId == costCentreId && o.PlacedAtUtc >= monthStart && o.Status != OrderStatus.Cancelled)
            .SumAsync(o => (long?)o.GrandTotalMinor, ct) ?? 0;
        return spent + amountMinor > budget.Value;
    }

    public async Task<int> SettleApprovalsAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default)
    {
        var ids = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.PendingApproval)
            .OrderBy(o => o.PlacedAtUtc).Select(o => o.Id).Take(batchSize).ToListAsync(ct);
        var settled = 0;
        foreach (var id in ids)
            if (await ApplyApprovalAsync(id, nowUtc, ct)) settled++;
        return settled;
    }

    public Task<bool> ApplyApprovalAsync(Guid orderId, CancellationToken ct = default) =>
        ApplyApprovalAsync(orderId, clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>
    /// Moves an order waiting for approval on according to its request: approved becomes payable (or, on credit, confirmed
    /// with the credit held now); rejected, cancelled or lapsed is cancelled and its stock freed. Safe to repeat: the sweep
    /// runs it every minute, so an approval decided while the process died is still applied.
    /// </summary>
    private async Task<bool> ApplyApprovalAsync(Guid orderId, DateTime now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var request = await db.ApprovalRequests
            .Where(r => r.SubjectType == PlaceOrderRequest.ApprovalSubjectType && r.SubjectId == orderId)
            .OrderByDescending(r => r.CreatedAtUtc).FirstOrDefaultAsync(ct);

        if (request?.Status == ApprovalStatus.Approved) return await ApproveOrderAsync(orderId, now, ct);
        if (request?.Status is ApprovalStatus.Rejected or ApprovalStatus.Cancelled)
            return await CancelOneAsync(orderId, null, "The company's approver did not approve this order", now, WaitingForApproval, ct);

        var placedAt = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.PlacedAtUtc).FirstAsync(ct);
        if (now - placedAt < ApprovalHoldLifetime) return false;

        // Nobody decided within the hold: the order lapses and so does its request, so it leaves the approvers' inbox.
        if (request is not null)
        {
            request.Expire(now);
            await db.SaveChangesAsync(ct);
        }
        return await CancelOneAsync(orderId, null, "The company did not approve this order within 24 hours", now, WaitingForApproval, ct);
    }

    private static bool WaitingForApproval(Order o) => o.Status == OrderStatus.PendingApproval;

    private async Task<bool> ApproveOrderAsync(Guid orderId, DateTime now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var confirmed = false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var order = (await db.Orders.FromSqlInterpolated($"SELECT o.*, o.xmin FROM orders o WHERE o.id = {orderId} FOR UPDATE")
                .Include(o => o.SubOrders).ToListAsync(ct)).Single();
            if (order.Status != OrderStatus.PendingApproval) return false;

            order.Approve(null, now + UnpaidOrderLifetime, now);
            if (order.PaysOnCredit)
            {
                var company = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {order.CompanyId!.Value} FOR UPDATE").ToListAsync(ct)).Single();
                try { company.ReserveCredit(Domain.Common.ValueObjects.Money.FromMinorUnits(order.GrandTotalMinor)); }
                catch (ConflictException)
                {
                    // The credit was used up while the order waited: it cannot go ahead on credit.
                    await tx.RollbackAsync(ct);
                    return await CancelOneAsync(orderId, null, "Approved, but the company's credit no longer covers this order", now, WaitingForApproval, ct);
                }
                order.Transition(OrderStatus.Confirmed, null, "Placed on credit terms after approval", now);
                confirmed = true;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (confirmed) await inventory.CommitAsync(orderId, ct);
        return true;
    }

    public async Task<OrderDto> CancelAsync(Guid userId, Guid orderId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required to cancel an order.");
        var now = clock.GetUtcNow().UtcDateTime;

        if (!await CancelOneAsync(orderId, userId, reason.Trim(), now, onlyIf: null, ct))
            throw new KeyNotFoundException("Order not found.");
        return await GetAsync(userId, orderId, ct);
    }

    public async Task<int> ExpireUnpaidAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default)
    {
        var ids = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.PendingPayment && o.PaymentDueAtUtc <= nowUtc)
            .OrderBy(o => o.PaymentDueAtUtc).Select(o => o.Id).Take(batchSize).ToListAsync(ct);

        var cancelled = 0;
        foreach (var id in ids)
            if (await CancelOneAsync(id, null, "Payment was not received within 30 minutes", nowUtc,
                    o => o.Status == OrderStatus.PendingPayment && o.PaymentDueAtUtc <= nowUtc, ct)) cancelled++;
        return cancelled;
    }

    /// <returns>False when the order is not found (or not the caller's) or, for a sweep, no longer matches <paramref name="onlyIf"/>.</returns>
    private async Task<bool> CancelOneAsync(Guid orderId, Guid? ownerUserId, string reason, DateTime now, Func<Order, bool>? onlyIf, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Lock the order row so a payment confirmation and a cancellation cannot both win.
            var order = (await db.Orders
                .FromSqlInterpolated($"SELECT o.*, o.xmin FROM orders o WHERE o.id = {orderId} FOR UPDATE")
                .Include(o => o.SubOrders)
                .ToListAsync(ct)).SingleOrDefault();

            if (order is null || (ownerUserId is not null && order.UserId != ownerUserId)) return false;
            if (onlyIf is not null && !onlyIf(order)) return false;

            // Until stock can be returned to the shelf, only an order whose stock is still merely held may be cancelled.
            if (order.Status is not (OrderStatus.PendingPayment or OrderStatus.PendingApproval))
                throw new ConflictException("ORDER_NOT_CANCELLABLE", $"An order that is {ToWire(order.Status)} can no longer be cancelled here.");

            // An order that no longer waits takes its approval request out of the approvers' inbox.
            if (order.Status == OrderStatus.PendingApproval)
            {
                var waiting = await db.ApprovalRequests
                    .Where(r => r.SubjectType == PlaceOrderRequest.ApprovalSubjectType && r.SubjectId == orderId && r.Status == ApprovalStatus.Pending)
                    .ToListAsync(ct);
                foreach (var request in waiting) request.Expire(now);
            }

            order.Transition(OrderStatus.Cancelled, ownerUserId, reason, now);

            // A cancelled order gives its promotion uses back.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE promotions p SET redeemed_count = p.redeemed_count - 1 FROM promotion_redemptions r WHERE r.order_id = {orderId} AND r.promotion_id = p.id", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE promotion_user_usage u SET uses = u.uses - 1 FROM promotion_redemptions r WHERE r.order_id = {orderId} AND r.promotion_id = u.promotion_id AND u.user_id = r.user_id", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM promotion_redemptions WHERE order_id = {orderId}", ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Releasing is idempotent; if the process dies right here the hold simply expires and the sweeper frees it.
        await inventory.CancelAsync(orderId, ct);
        return true;
    }

    public async Task<OrderDto> GetAsync(Guid userId, Guid orderId, CancellationToken ct = default)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(s => s.Items)
                        .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
                    ?? throw new KeyNotFoundException("Order not found.");

        return new OrderDto(order.Id, order.OrderNumber, ToWire(order.Status), order.Currency, order.SubtotalMinor, order.DiscountMinor,
            order.TaxMinor, order.ShippingMinor, order.GrandTotalMinor, order.PlacedAtUtc,
            order.SubOrders.OrderBy(s => s.SubOrderNumber).Select(s => new SubOrderDto(s.Id, s.SubOrderNumber, s.SellerId, ToWire(s.Status),
                s.ShippingMinor, s.TotalMinor,
                s.Items.OrderBy(i => i.Sku).Select(i => new OrderItemDto(i.VariantId, i.Sku, i.Title, i.Quantity, i.UnitPriceMinor,
                    i.DiscountMinor, i.TaxMinor, i.TotalMinor)).ToList())).ToList());
    }

    /// <summary>The snake_case name the API and the database use for a state.</summary>
    private static string ToWire(OrderStatus status) =>
        string.Concat(status.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static List<string> NormalisedCodes(PlaceOrderRequest r) =>
        (r.CouponCodes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();

    private static List<PlaceOrderLine> Normalise(PlaceOrderRequest r)
    {
        if (!KeyPattern().IsMatch(r.IdempotencyKey ?? string.Empty))
            throw new ValidationException("idempotencyKey", "An Idempotency-Key of 8 to 100 letters, digits, '-' or '_' is required.");
        if ((r.CouponCodes?.Count ?? 0) > PlaceOrderRequest.MaxCoupons)
            throw new ValidationException("couponCodes", $"At most {PlaceOrderRequest.MaxCoupons} coupon codes per order.");
        if (r.PayOnCredit && r.CompanyId is null) throw new ValidationException("payOnCredit", "Credit terms apply to company orders only.");
        if (r.Lines is null || r.Lines.Count == 0) throw new ValidationException("lines", "An order needs at least one line.");
        if (r.Lines.Any(l => l.Quantity is <= 0 or > PlaceOrderRequest.MaxQuantityPerLine))
            throw new ValidationException("quantity", $"Each quantity must be between 1 and {PlaceOrderRequest.MaxQuantityPerLine}.");

        // The same variant listed twice is one line.
        var merged = r.Lines.GroupBy(l => l.VariantId)
            .Select(g => new PlaceOrderLine(g.Key, (int)Math.Min(g.Sum(l => (long)l.Quantity), PlaceOrderRequest.MaxQuantityPerLine + 1)))
            .OrderBy(l => l.VariantId).ToList();
        if (merged.Any(l => l.Quantity > PlaceOrderRequest.MaxQuantityPerLine))
            throw new ValidationException("quantity", $"Each quantity must be between 1 and {PlaceOrderRequest.MaxQuantityPerLine}.");
        if (merged.Count > PlaceOrderRequest.MaxLines)
            throw new ValidationException("lines", $"At most {PlaceOrderRequest.MaxLines} different items per order.");
        return merged;
    }

    private static string HashOf(PlaceOrderRequest r, List<PlaceOrderLine> lines)
    {
        var canonical = string.Join(';', lines.Select(l => $"{l.VariantId:N}:{l.Quantity}")) + $"|{r.CompanyId:N}|{r.ShippingAddressId:N}|{r.ShipToSiteId:N}|{r.CostCentreId:N}|{r.PayOnCredit}|{string.Join(',', NormalisedCodes(r).Order())}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<PlacedOrder?> ReplayAsync(Guid userId, string key, string hash, CancellationToken ct)
    {
        var record = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
        if (record is null) return null;
        if (record.RequestHash != hash)
            throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request.");

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == record.OrderId, ct);
        return new PlacedOrder(order.Id, order.OrderNumber, ToWire(order.Status), order.GrandTotalMinor,
            order.PaymentDueAtUtc ?? order.PlacedAtUtc + ApprovalHoldLifetime, true);
    }


    private async Task<(PricedOrder Priced, IReadOnlyList<AppliedPromotion> Applied, IReadOnlyList<Guid> Categories)> PriceAsync(
        PlaceOrderRequest request, List<PlaceOrderLine> lines, TaxMode mode, DateTime now, CancellationToken ct)
    {
        var ids = lines.Select(l => l.VariantId).ToList();
        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                          join s in db.Sellers.AsNoTracking() on p.SellerId equals s.Id
                          where ids.Contains(v.Id)
                          select new { Variant = v, Product = p, Category = c, Seller = s }).ToListAsync(ct);

        var byId = rows.ToDictionary(r => r.Variant.Id);
        var unavailable = lines.Where(l => !byId.TryGetValue(l.VariantId, out var r)
                                           || r.Product.Status != ProductStatus.Active
                                           || r.Variant.Status != VariantStatus.Active).Select(l => l.VariantId).ToList();
        if (unavailable.Count > 0)
            throw new ConflictException("VARIANT_UNAVAILABLE", $"These items are not available: {string.Join(", ", unavailable)}.");

        var drafts = new List<OrderLineDraft>();
        var categoryOf = new Dictionary<Guid, Guid>();
        var terms = new Dictionary<Guid, SellerTerms>();
        foreach (var line in lines)
        {
            var r = byId[line.VariantId];
            var quote = PricingEngine.Quote(PricingInput.From(r.Variant, r.Category.VatRate), now, mode);
            var lineId = Guid.NewGuid();
            categoryOf[lineId] = r.Product.CategoryId;
            drafts.Add(new OrderLineDraft(lineId, r.Variant.Id, r.Seller.Id, r.Variant.Sku, r.Product.TitleEn,
                line.Quantity, quote.Display.ToMinorUnits(), r.Category.VatRate, 0));
            // Shipping zones and seller-specific commission tiers arrive with their own slices; until then shipping is 0.
            terms.TryAdd(r.Seller.Id, new SellerTerms(r.Seller.Id, 0, r.Seller.CommissionOverrideRate ?? r.Category.CommissionRate));
        }

        var applied = await ApplyPromotionsAsync(request, drafts, categoryOf, now, ct);
        var discounted = applied.Lines.Select((d, i) => drafts[i] with { DiscountMinor = d.DiscountMinor }).ToList();
        return (OrderPricing.Price(discounted, terms.Values.ToList(), mode), applied.Applied, categoryOf.Values.Distinct().ToList());
    }

    /// <summary>
    /// Automatic promotions apply on their own; coupon codes the buyer entered apply if they exist and the engine accepts them.
    /// An entered code that cannot be used is an error the buyer is told about, never silently dropped. Usage limits are checked
    /// here only to give a clear message early; the binding check is the atomic claim made when the order is saved.
    /// </summary>
    private async Task<PromotionResult> ApplyPromotionsAsync(
        PlaceOrderRequest request, List<OrderLineDraft> drafts, Dictionary<Guid, Guid> categoryOf, DateTime now, CancellationToken ct)
    {
        var codes = NormalisedCodes(request);
        if (codes.Count > 0) await RequireCouponAttemptsLeftAsync(request.UserId, now, ct);

        var candidates = await db.Promotions.AsNoTracking()
            .Where(p => p.IsActive && (p.Code == null || codes.Contains(p.Code)))
            .ToListAsync(ct);


        var ids = candidates.Select(p => p.Id).ToList();
        var userUses = await db.PromotionUserUsage.AsNoTracking()
            .Where(u => u.UserId == request.UserId && ids.Contains(u.PromotionId))
            .ToDictionaryAsync(u => u.PromotionId, u => u.Uses, ct);
        var usage = candidates.ToDictionary(p => p.Id, p => new PromotionUsage(p.RedeemedCount, userUses.GetValueOrDefault(p.Id)));

        var cart = drafts.Select(d => new CartLine(d.LineId, d.VariantId, d.SellerId, categoryOf[d.LineId], d.Quantity, d.UnitPriceMinor)).ToList();
        var result = PromotionEngine.Apply(cart, candidates.Select(p => p.ToPromotion()).ToList(), now, usage);

        // A code that does not exist, is outside its dates or is used up gets one shared answer, decided over every entered code
        // at once, so neither the wording nor which codes are named can tell a real code from a made-up one (threat model v2,
        // R1). Each such code counts against the hourly limit, so entering several per order does not multiply the guesses.
        string? ReasonFor(string code) =>
            candidates.FirstOrDefault(p => p.Code == code) is { } promo
                ? result.Rejected.FirstOrDefault(r => r.PromotionId == promo.Id)?.Reason
                : NoSuchCode;
        var dead = codes.Where(c => ReasonFor(c) is NoSuchCode or PromotionEngine.NotYetValid or PromotionEngine.Expired or PromotionEngine.GlobalLimitReached).ToList();
        if (dead.Count > 0)
        {
            await CountCouponFailuresAsync(request.UserId, now, dead.Count, ct);
            throw new ConflictException("COUPON_INVALID", $"Not valid or no longer available: {string.Join(", ", dead)}.");
        }

        // The other reasons only make sense to someone who holds the code and can act on them (spend more, change the basket).
        foreach (var code in codes)
        {
            if (ReasonFor(code) is { } reason)
            {
                await CountCouponFailuresAsync(request.UserId, now, 1, ct);
                throw new ConflictException("COUPON_" + reason, $"The code {code} cannot be used on this order ({reason}).");
            }
        }

        return result;
    }

    private const string NoSuchCode = "NO_SUCH_CODE";

    /// <summary>Refused coupon codes one user may enter per hour before every code is refused unread (threat model v2, R1).</summary>
    public const int MaxCouponFailuresPerHour = 10;
    private static readonly TimeSpan CouponFailureWindowLength = TimeSpan.FromHours(1);

    private async Task RequireCouponAttemptsLeftAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var window = await db.CouponFailureWindows.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId, ct);
        if (window is not null && window.WindowStartedAtUtc > now - CouponFailureWindowLength && window.Failures >= MaxCouponFailuresPerHour)
            throw new ConflictException("COUPON_ATTEMPTS_EXCEEDED", "Too many coupon codes were refused. Try again in an hour.");
    }

    /// <summary>One atomic upsert, outside the order's transaction (no order exists yet), so a refusal is counted even though the request fails.</summary>
    private Task CountCouponFailuresAsync(Guid userId, DateTime now, int failures, CancellationToken ct)
    {
        var windowStart = now - CouponFailureWindowLength;
        return db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO coupon_failure_windows (user_id, window_started_at, failures) VALUES ({userId}, {now}, {failures})
            ON CONFLICT (user_id) DO UPDATE SET
                failures = CASE WHEN coupon_failure_windows.window_started_at <= {windowStart} THEN {failures} ELSE coupon_failure_windows.failures + {failures} END,
                window_started_at = CASE WHEN coupon_failure_windows.window_started_at <= {windowStart} THEN {now} ELSE coupon_failure_windows.window_started_at END", ct);
    }

    /// <summary>
    /// Saves the order in one transaction: for a credit order the company's credit is checked and held first; every promotion
    /// the order uses is claimed with an atomic conditional update, so two orders can never both take the last use.
    /// </summary>
    private async Task SaveAsync(Order order, PlaceOrderRequest request, IReadOnlyList<AppliedPromotion> applied,
        ApprovalRule? approvalRule, IReadOnlyList<Guid> categories, DateTime now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (approvalRule is not null)
        {
            // Created with the order, in the same transaction and pointing at it: an order never waits for an approval that
            // does not exist, and no approval can be asked for an amount other than the order's.
            var eligible = (await db.CompanyMembers.AsNoTracking()
                .Where(m => m.CompanyId == request.CompanyId && m.IsActive && approvalRule.ApproverUserIds.Contains(m.UserId)
                            && ((m.Roles & CompanyRoles.Approver) != 0 || (m.Roles & CompanyRoles.Admin) != 0))
                .Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
            db.ApprovalRequests.Add(ApprovalRequest.Create(Guid.NewGuid(), request.UserId, PlaceOrderRequest.ApprovalSubjectType, order.Id,
                order.GrandTotalMinor, request.CostCentreId, categories, approvalRule, eligible));
        }
        else if (request.PayOnCredit)
        {
            var company = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {request.CompanyId!.Value} FOR UPDATE").ToListAsync(ct)).Single();
            company.ReserveCredit(Domain.Common.ValueObjects.Money.FromMinorUnits(order.GrandTotalMinor));
            order.Transition(OrderStatus.Confirmed, order.UserId, "Placed on credit terms", now);
        }

        // A fixed order avoids two orders waiting on each other's promotion rows.
        foreach (var a in applied.OrderBy(x => x.PromotionId))
        {
            var global = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE promotions SET redeemed_count = redeemed_count + 1 WHERE id = {a.PromotionId} AND (global_limit IS NULL OR redeemed_count < global_limit)", ct);
            if (global == 0) throw new ConflictException("COUPON_GLOBAL_LIMIT_REACHED", $"{a.Name} has no uses left.");

            var user = await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO promotion_user_usage (promotion_id, user_id, uses) VALUES ({a.PromotionId}, {order.UserId}, 1)
                ON CONFLICT (promotion_id, user_id) DO UPDATE SET uses = promotion_user_usage.uses + 1
                WHERE (SELECT per_user_limit FROM promotions WHERE id = {a.PromotionId}) IS NULL
                   OR promotion_user_usage.uses < (SELECT per_user_limit FROM promotions WHERE id = {a.PromotionId})", ct);
            if (user == 0) throw new ConflictException("COUPON_PER_USER_LIMIT_REACHED", $"You have already used {a.Name} the allowed number of times.");

            db.PromotionRedemptions.Add(new PromotionRedemption(order.Id, a.PromotionId, order.UserId, a.DiscountMinor));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<DateTime> ReserveAsync(Guid orderId, List<PlaceOrderLine> lines, TimeSpan holdFor, CancellationToken ct)
    {
        var result = await inventory.ReserveAsync(
            new ReserveStockRequest(orderId, lines.Select(l => new ReservationLine(l.VariantId, l.Quantity)).ToList(), holdFor), ct);
        if (result.Succeeded) return result.ExpiresAtUtc ?? clock.GetUtcNow().UtcDateTime + holdFor;

        if (result.UnavailableVariants is { Count: > 0 })
            throw new ConflictException("VARIANT_UNAVAILABLE", "Some items are not available.");
        if (result.Shortfalls.Count > 0)
            throw new ConflictException("INSUFFICIENT_STOCK", string.Join("; ", result.Shortfalls.Select(i => $"{i.VariantId}: wanted {i.Requested}, {i.Available} left")));
        throw new ConflictException("CHECKOUT_BUSY", "Checkout is busy for one of these items; please retry.");
    }
}
