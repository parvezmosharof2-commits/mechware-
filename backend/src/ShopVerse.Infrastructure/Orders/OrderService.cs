using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Application.Inventory;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Catalog;
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

        // Requests with the same key queue up here. Without this, a client retrying while its first request is still
        // reserving stock would lose the stock race and be told "insufficient stock" instead of getting its own order back.
        // The connection is held open so the session-level lock and the later statements share one database session.
        await db.Database.OpenConnectionAsync(ct);
        var lockKey = $"order:{request.UserId:N}:{request.IdempotencyKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({lockKey}, 0))", ct);
        try
        {
            return await PlaceLockedAsync(request, lines, hash, ct);
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

        if (request.CompanyId is { } companyId) await RequireCompanyMayBuyAsync(request.UserId, companyId, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var mode = request.CompanyId is null ? TaxMode.Inclusive : TaxMode.Exclusive;
        var (priced, applied) = await PriceAsync(request, lines, mode, now, ct);

        // A rare clash of two random order numbers is retried with a fresh one rather than shown to the buyer.
        for (var attempt = 0; ; attempt++)
        {
            var orderId = Guid.NewGuid();
            var reserved = await ReserveAsync(orderId, lines, ct);

            var order = Order.Place(orderId, _nextOrderNumber(now), request.UserId, request.CompanyId, priced, mode,
                request.ShipToJson ?? "{}", OrderStatus.PendingPayment, now);
            db.Orders.Add(order);
            db.IdempotencyKeys.Add(new IdempotencyRecord(request.UserId, request.IdempotencyKey, hash, orderId));

            try
            {
                await SaveAsync(order, request, applied, now, ct);
                // Credit orders are confirmed at once (brief §8.5): the held stock becomes a real deduction.
                if (request.PayOnCredit) await inventory.CommitAsync(orderId, ct);

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
    /// An order in a company's name needs a buyer of that company and a verified, unsuspended company. A user who is not
    /// a member gets "not found", the same as for any other company they cannot see.
    /// </summary>
    private async Task RequireCompanyMayBuyAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        await Companies.CompanyAccess.RequireMemberAsync(db, userId, companyId, Domain.Companies.CompanyRoles.Buyer, ct);
        await Companies.CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);
        var status = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Status).FirstAsync(ct);
        if (status != Domain.Companies.CompanyStatus.Active)
            throw new ConflictException("COMPANY_NOT_VERIFIED", "The company has not been verified by MECHWARE yet.");
    }

    public static readonly TimeSpan UnpaidOrderLifetime = TimeSpan.FromMinutes(30);

    public async Task<OrderDto> CancelAsync(Guid userId, Guid orderId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required to cancel an order.");
        var now = clock.GetUtcNow().UtcDateTime;

        if (!await CancelOneAsync(orderId, userId, reason.Trim(), now, onlyIfUnpaidSince: null, ct))
            throw new KeyNotFoundException("Order not found.");
        return await GetAsync(userId, orderId, ct);
    }

    public async Task<int> ExpireUnpaidAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default)
    {
        var cutoff = nowUtc - UnpaidOrderLifetime;
        var ids = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.PendingPayment && o.PlacedAtUtc <= cutoff)
            .OrderBy(o => o.PlacedAtUtc).Select(o => o.Id).Take(batchSize).ToListAsync(ct);

        var cancelled = 0;
        foreach (var id in ids)
            if (await CancelOneAsync(id, null, "Payment was not received within 30 minutes", nowUtc, cutoff, ct)) cancelled++;
        return cancelled;
    }

    /// <returns>False when the order is not found (or not the caller's) or, for a sweep, is no longer an overdue unpaid order.</returns>
    private async Task<bool> CancelOneAsync(Guid orderId, Guid? ownerUserId, string reason, DateTime now, DateTime? onlyIfUnpaidSince, CancellationToken ct)
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
            if (onlyIfUnpaidSince is { } cutoff && (order.Status != OrderStatus.PendingPayment || order.PlacedAtUtc > cutoff)) return false;

            // Until stock can be returned to the shelf, only an order whose stock is still merely held may be cancelled.
            if (order.Status is not (OrderStatus.PendingPayment or OrderStatus.PendingApproval))
                throw new ConflictException("ORDER_NOT_CANCELLABLE", $"An order that is {ToWire(order.Status)} can no longer be cancelled here.");

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
        var canonical = string.Join(';', lines.Select(l => $"{l.VariantId:N}:{l.Quantity}")) + $"|{r.CompanyId:N}|{r.ShipToJson}|{r.PayOnCredit}|{string.Join(',', NormalisedCodes(r).Order())}";
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
            order.PlacedAtUtc + UnpaidOrderLifetime, true);
    }


    private async Task<(PricedOrder Priced, IReadOnlyList<AppliedPromotion> Applied)> PriceAsync(
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
        return (OrderPricing.Price(discounted, terms.Values.ToList(), mode), applied.Applied);
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
        var candidates = await db.Promotions.AsNoTracking()
            .Where(p => p.IsActive && (p.Code == null || codes.Contains(p.Code)))
            .ToListAsync(ct);

        var unknown = codes.Where(c => !candidates.Any(p => p.Code == c)).ToList();
        if (unknown.Count > 0) throw new ConflictException("COUPON_INVALID", $"This code is not valid: {string.Join(", ", unknown)}.");

        var ids = candidates.Select(p => p.Id).ToList();
        var userUses = await db.PromotionUserUsage.AsNoTracking()
            .Where(u => u.UserId == request.UserId && ids.Contains(u.PromotionId))
            .ToDictionaryAsync(u => u.PromotionId, u => u.Uses, ct);
        var usage = candidates.ToDictionary(p => p.Id, p => new PromotionUsage(p.RedeemedCount, userUses.GetValueOrDefault(p.Id)));

        var cart = drafts.Select(d => new CartLine(d.LineId, d.VariantId, d.SellerId, categoryOf[d.LineId], d.Quantity, d.UnitPriceMinor)).ToList();
        var result = PromotionEngine.Apply(cart, candidates.Select(p => p.ToPromotion()).ToList(), now, usage);

        foreach (var code in codes)
        {
            var promo = candidates.First(p => p.Code == code);
            if (result.Rejected.FirstOrDefault(r => r.PromotionId == promo.Id) is { } rejected)
                throw new ConflictException("COUPON_" + rejected.Reason, $"The code {code} cannot be used on this order ({rejected.Reason}).");
        }

        return result;
    }

    /// <summary>
    /// Saves the order in one transaction: for a credit order the company's credit is checked and held first; every promotion
    /// the order uses is claimed with an atomic conditional update, so two orders can never both take the last use.
    /// </summary>
    private async Task SaveAsync(Order order, PlaceOrderRequest request, IReadOnlyList<AppliedPromotion> applied, DateTime now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (request.PayOnCredit)
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

    private async Task<DateTime> ReserveAsync(Guid orderId, List<PlaceOrderLine> lines, CancellationToken ct)
    {
        var result = await inventory.ReserveAsync(
            new ReserveStockRequest(orderId, lines.Select(l => new ReservationLine(l.VariantId, l.Quantity)).ToList(), UnpaidOrderLifetime), ct);
        if (result.Succeeded) return result.ExpiresAtUtc ?? clock.GetUtcNow().UtcDateTime + UnpaidOrderLifetime;

        if (result.UnavailableVariants is { Count: > 0 })
            throw new ConflictException("VARIANT_UNAVAILABLE", "Some items are not available.");
        if (result.Shortfalls.Count > 0)
            throw new ConflictException("INSUFFICIENT_STOCK", string.Join("; ", result.Shortfalls.Select(i => $"{i.VariantId}: wanted {i.Requested}, {i.Available} left")));
        throw new ConflictException("CHECKOUT_BUSY", "Checkout is busy for one of these items; please retry.");
    }
}
