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

        var now = clock.GetUtcNow().UtcDateTime;
        var mode = request.CompanyId is null ? TaxMode.Inclusive : TaxMode.Exclusive;
        var priced = await PriceAsync(lines, mode, now, ct);

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
                await db.SaveChangesAsync(ct);
                return new PlacedOrder(order.Id, order.OrderNumber, "pending_payment", order.GrandTotalMinor, reserved, false);
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

    private static List<PlaceOrderLine> Normalise(PlaceOrderRequest r)
    {
        if (!KeyPattern().IsMatch(r.IdempotencyKey ?? string.Empty))
            throw new ValidationException("idempotencyKey", "An Idempotency-Key of 8 to 100 letters, digits, '-' or '_' is required.");
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
        var canonical = string.Join(';', lines.Select(l => $"{l.VariantId:N}:{l.Quantity}")) + $"|{r.CompanyId:N}|{r.ShipToJson}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<PlacedOrder?> ReplayAsync(Guid userId, string key, string hash, CancellationToken ct)
    {
        var record = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
        if (record is null) return null;
        if (record.RequestHash != hash)
            throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request.");

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == record.OrderId, ct);
        return new PlacedOrder(order.Id, order.OrderNumber, "pending_payment", order.GrandTotalMinor,
            order.PlacedAtUtc + StockReservationTtl, true);
    }

    private static readonly TimeSpan StockReservationTtl = TimeSpan.FromMinutes(15);

    private async Task<PricedOrder> PriceAsync(List<PlaceOrderLine> lines, TaxMode mode, DateTime now, CancellationToken ct)
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
        var terms = new Dictionary<Guid, SellerTerms>();
        foreach (var line in lines)
        {
            var r = byId[line.VariantId];
            var quote = PricingEngine.Quote(PricingInput.From(r.Variant, r.Category.VatRate), now, mode);
            drafts.Add(new OrderLineDraft(Guid.NewGuid(), r.Variant.Id, r.Seller.Id, r.Variant.Sku, r.Product.TitleEn,
                line.Quantity, quote.Display.ToMinorUnits(), r.Category.VatRate, 0));
            // Shipping zones and seller-specific commission tiers arrive with their own slices; until then shipping is 0.
            terms.TryAdd(r.Seller.Id, new SellerTerms(r.Seller.Id, 0, r.Seller.CommissionOverrideRate ?? r.Category.CommissionRate));
        }

        return OrderPricing.Price(drafts, terms.Values.ToList(), mode);
    }

    private async Task<DateTime> ReserveAsync(Guid orderId, List<PlaceOrderLine> lines, CancellationToken ct)
    {
        var result = await inventory.ReserveAsync(
            new ReserveStockRequest(orderId, lines.Select(l => new ReservationLine(l.VariantId, l.Quantity)).ToList()), ct);
        if (result.Succeeded) return result.ExpiresAtUtc ?? clock.GetUtcNow().UtcDateTime + StockReservationTtl;

        if (result.UnavailableVariants is { Count: > 0 })
            throw new ConflictException("VARIANT_UNAVAILABLE", "Some items are not available.");
        if (result.Shortfalls.Count > 0)
            throw new ConflictException("INSUFFICIENT_STOCK", string.Join("; ", result.Shortfalls.Select(i => $"{i.VariantId}: wanted {i.Requested}, {i.Available} left")));
        throw new ConflictException("CHECKOUT_BUSY", "Checkout is busy for one of these items; please retry.");
    }
}
