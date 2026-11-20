using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// Staff reads over all orders (G5.5): the filtered list and the full detail. Every read runs in one REPEATABLE READ snapshot, so
/// totals, lines, payments and credit agree. The buyer's name and phone are in the detail only (the route needs
/// <c>orders.view_all</c>); they are never logged. No provider reference, payment note or card data is selected at all.
/// </summary>
public sealed class StaffOrderReadService(MechwareDbContext db) : IStaffOrderReadService
{
    private static readonly string[] PaymentStates = ["none", "on_credit", "pending", "captured", "failed", "needs_refund", "refunded"];
    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    public Task<PagedResult<StaffOrderListItemDto>> ListAsync(StaffOrderFilter f, DateTime nowUtc, CancellationToken ct = default)
    {
        var status = ParseStatus(f.Status);
        var payment = ParsePaymentState(f.PaymentState);
        var (fromUtc, toUtc) = ParseRange(f.From, f.To);
        var number = ParseNumberPrefix(f.OrderNumber);
        var page = Math.Max(1, f.Page);
        var pageSize = Math.Clamp(f.PageSize, 1, IStaffOrderReadService.MaxPageSize);
        var asOf = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var open = OrderDeadlineService.DeliveryOpen;
        var handover = OrderDeadlineService.HandoverOpen;
        PaymentStatus? wanted = payment is null or "none" or "on_credit" ? null : Enum.Parse<PaymentStatus>(payment.Replace("_", ""), true);

        return ReadSnapshot.RunAsync(db, async () =>
        {
            var q = db.Orders.AsNoTracking().Select(o => new
            {
                Order = o,
                Pay = db.Payments.Where(p => p.OrderId == o.Id)
                    .OrderByDescending(p => p.Status == PaymentStatus.Captured || p.Status == PaymentStatus.Refunded || p.Status == PaymentStatus.NeedsRefund)
                    .ThenByDescending(p => p.CreatedAtUtc).Select(p => (PaymentStatus?)p.Status).FirstOrDefault(),
                Late = (o.LegalDeliveryDeadlineUtc != null && o.LegalDeliveryDeadlineUtc < asOf && open.Contains(o.Status)) ||
                       (o.CourierHandoverDeadlineUtc != null && o.CourierHandoverDeadlineUtc < asOf && handover.Contains(o.Status))
            });
            if (status is { } s) q = q.Where(x => x.Order.Status == s);
            if (f.CompanyId is { } c) q = q.Where(x => x.Order.CompanyId == c);
            if (number is not null) q = q.Where(x => x.Order.OrderNumber.StartsWith(number));
            if (fromUtc is { } from) q = q.Where(x => x.Order.PlacedAtUtc >= from);
            if (toUtc is { } to) q = q.Where(x => x.Order.PlacedAtUtc < to);
            if (f.Overdue is { } overdue) q = q.Where(x => x.Late == overdue);
            if (payment == "none") q = q.Where(x => x.Pay == null && !x.Order.PaysOnCredit);
            else if (payment == "on_credit") q = q.Where(x => x.Pay == null && x.Order.PaysOnCredit);
            else if (wanted is { } w) q = q.Where(x => x.Pay == w);

            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(x => x.Order.PlacedAtUtc).ThenByDescending(x => x.Order.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Order.Id,
                    x.Order.OrderNumber,
                    x.Order.Status,
                    x.Order.CompanyId,
                    x.Order.GrandTotalMinor,
                    x.Order.PlacedAtUtc,
                    x.Order.LegalDeliveryDeadlineUtc,
                    x.Order.CourierHandoverDeadlineUtc,
                    x.Order.PaysOnCredit,
                    x.Order.QuoteId,
                    x.Pay
                }).ToListAsync(ct);

            var companyIds = rows.Where(r => r.CompanyId != null).Select(r => r.CompanyId!.Value).Distinct().ToList();
            var quoteIds = rows.Where(r => r.QuoteId != null).Select(r => r.QuoteId!.Value).Distinct().ToList();
            var companies = await db.Companies.AsNoTracking().Where(x => companyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.LegalName, ct);
            var quotes = await db.Quotes.AsNoTracking().Where(x => quoteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Number, ct);

            var items = rows.Select(r => new StaffOrderListItemDto(r.Id, r.OrderNumber, OrderService.ToWire(r.Status), r.CompanyId,
                r.CompanyId is { } cid ? companies.GetValueOrDefault(cid) : null, r.GrandTotalMinor, PaymentStateOf(r.Pay, r.PaysOnCredit), r.PlacedAtUtc,
                r.LegalDeliveryDeadlineUtc, r.CourierHandoverDeadlineUtc,
                r.LegalDeliveryDeadlineUtc < asOf && OrderDeadlineService.DeliveryOpen.Contains(r.Status),
                r.CourierHandoverDeadlineUtc < asOf && OrderDeadlineService.HandoverOpen.Contains(r.Status),
                r.QuoteId is null ? "cart" : "quote", r.QuoteId is { } qid ? quotes.GetValueOrDefault(qid) : null)).ToList();
            return new PagedResult<StaffOrderListItemDto>(items, page, pageSize, total);
        }, ct);
    }

    public Task<StaffOrderDetailDto> GetAsync(Guid orderId, DateTime nowUtc, CancellationToken ct = default) =>
        ReadSnapshot.RunAsync(db, async () =>
        {
            var order = await db.Orders.AsNoTracking().AsSplitQuery().Include(o => o.SubOrders).ThenInclude(s => s.Items)
                            .FirstOrDefaultAsync(o => o.Id == orderId, ct) ?? throw new KeyNotFoundException("Order not found.");
            var events = await db.OrderEvents.AsNoTracking().Where(e => e.OrderId == orderId).OrderBy(e => e.Sequence).ToListAsync(ct);
            var sellerIds = order.SubOrders.Select(s => s.SellerId).Distinct().ToList();
            var stores = await db.Sellers.AsNoTracking().Where(s => sellerIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.StoreName, ct);
            var payments = await db.Payments.AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.CreatedAtUtc).ToListAsync(ct);
            var paymentIds = payments.Select(p => p.Id).ToList();
            var refunds = await db.Refunds.AsNoTracking().Where(r => paymentIds.Contains(r.PaymentId)).OrderBy(r => r.CreatedAtUtc).ToListAsync(ct);
            var buyer = await db.Users.AsNoTracking().Where(u => u.Id == order.UserId).Select(u => new StaffOrderBuyerDto(u.Id, u.FullName, u.Phone)).FirstOrDefaultAsync(ct)
                        ?? new StaffOrderBuyerDto(order.UserId, string.Empty, null);
            var companyName = order.CompanyId is { } cid
                ? await db.Companies.AsNoTracking().Where(c => c.Id == cid).Select(c => c.LegalName).FirstOrDefaultAsync(ct) : null;
            var quote = order.QuoteId is { } qid && order.QuoteVersionId is { } vid
                ? await db.Quotes.AsNoTracking().Where(q => q.Id == qid).Select(q => new StaffOrderQuoteDto(q.Id, q.Number, vid)).FirstOrDefaultAsync(ct) : null;

            var money = payments.OrderByDescending(p => p.Status is PaymentStatus.Captured or PaymentStatus.Refunded or PaymentStatus.NeedsRefund)
                .ThenByDescending(p => p.CreatedAtUtc).FirstOrDefault();
            return new StaffOrderDetailDto(order.Id, order.OrderNumber, OrderService.ToWire(order.Status), order.Currency, OrderService.ToWire(order.TaxMode),
                order.PlacedAtUtc, order.ConfirmedAtUtc, order.PaymentDueAtUtc, order.LegalDeliveryDeadlineUtc, order.CourierHandoverDeadlineUtc,
                order.DeadlineSameCity, order.PurchaseOrderNumber, order.SubtotalMinor, order.DiscountMinor, order.TaxMinor, order.ShippingMinor,
                order.GrandTotalMinor, order.QuoteId is null ? "cart" : "quote", quote, order.CompanyId, companyName, buyer,
                OrderService.ShipToForBuyer(order.ShipToJson), PaymentStateOf(money?.Status, order.PaysOnCredit), order.PaysOnCredit,
                await CreditAsync(order, nowUtc, ct),
                order.SubOrders.OrderBy(s => s.SubOrderNumber).Select(s => new StaffSubOrderDto(s.Id, s.SubOrderNumber, s.SellerId, stores.GetValueOrDefault(s.SellerId),
                    OrderService.ToWire(s.Status), s.ShippingMinor, s.TotalMinor,
                    s.Items.OrderBy(i => i.Sku).Select(i => new StaffOrderLineDto(i.VariantId, i.Sku, i.Title, i.Quantity, i.UnitPriceMinor, i.VatRate, i.GrossMinor,
                        i.DiscountMinor, i.NetMinor, i.TaxMinor, i.TotalMinor, new StaffPriceSnapshotDto(i.PriceSource is { } src ? OrderService.ToWire(src) : null,
                            i.PriceBasis is { } basis ? OrderService.ToWire(basis) : null, i.PriceListId, i.PriceListVersion, i.ContractRef,
                            i.PublicUnitPriceMinor, i.QuoteLineId))).ToList())).ToList(),
                events.Select(e => new StaffOrderEventDto(e.Sequence, OrderService.ToWire(e.FromStatus), OrderService.ToWire(e.ToStatus), e.ActorId, e.Reason, e.AtUtc)).ToList(),
                payments.Select(p => new StaffPaymentDto(p.Id, p.Provider, OrderService.ToWire(p.Status), p.AmountMinor, p.Currency, p.CreatedAtUtc, p.CapturedAtUtc,
                    refunds.Where(r => r.PaymentId == p.Id).Select(r => new StaffRefundDto(r.Id, r.AmountMinor, OrderService.ToWire(r.Status), r.Reason, r.RequestedBy,
                        r.DecidedBy, r.DecidedAtUtc, r.CreatedAtUtc)).ToList())).ToList());
        }, ct);

    private async Task<StaffOrderCreditDto?> CreditAsync(Order order, DateTime nowUtc, CancellationToken ct)
    {
        if (!order.PaysOnCredit || order.CompanyId is not { } companyId) return null;
        var lines = await db.CreditLedgerEntries.AsNoTracking().Where(l => l.OrderId == order.Id && l.CompanyId == companyId).ToListAsync(ct);
        var charge = lines.Where(l => l.Kind == CreditLedgerKind.Charge).ToList();
        if (charge.Count == 0 || charge[0].DueOn is not { } dueOn) return null;
        var receivable = lines.Sum(l => l.ReceivableDeltaMinor);
        var charged = charge.Sum(l => l.ReceivableDeltaMinor);
        var status = CreditBuyerView.OrderStatus(order.Status == OrderStatus.Cancelled, charged, receivable, dueOn, CreditCalendar.DhakaDate(nowUtc));
        return new StaffOrderCreditDto(status, dueOn, charged, Math.Max(0, receivable));
    }

    private static string PaymentStateOf(PaymentStatus? status, bool paysOnCredit) =>
        status is { } s ? OrderService.ToWire(s) : paysOnCredit ? "on_credit" : "none";

    private static OrderStatus? ParseStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var value in Enum.GetValues<OrderStatus>())
            if (OrderService.ToWire(value).Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) return value;
        throw new ValidationException("status", $"Use one of: {string.Join(", ", Enum.GetValues<OrderStatus>().Select(OrderService.ToWire))}.");
    }

    private static string? ParsePaymentState(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToLowerInvariant();
        return PaymentStates.Contains(t) ? t : throw new ValidationException("paymentState", $"Use one of: {string.Join(", ", PaymentStates)}.");
    }

    private static string? ParseNumberPrefix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant();
        if (t.Length > 40 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ValidationException("orderNumber", "An order number is letters, digits and dashes, at most 40 characters.");
        return t;
    }

    /// <summary>Dhaka calendar dates (yyyy-MM-dd, inclusive) to a UTC range [from 00:00 +06, day after "to" 00:00 +06).</summary>
    private static (DateTime? FromUtc, DateTime? ToUtc) ParseRange(string? from, string? to)
    {
        static DateOnly? Day(string? text, string field)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new ValidationException(field, "Use a date like 2026-11-21 (Dhaka calendar date).");
        }
        var f = Day(from, "from");
        var t = Day(to, "to");
        if (f is { } a && t is { } b && a > b) throw new ValidationException("from", "'from' must not be after 'to'.");
        return (f is { } x ? DateTime.SpecifyKind(x.ToDateTime(TimeOnly.MinValue) - Dhaka, DateTimeKind.Utc) : null,
            t is { } y ? DateTime.SpecifyKind(y.AddDays(1).ToDateTime(TimeOnly.MinValue) - Dhaka, DateTimeKind.Utc) : null);
    }
}
