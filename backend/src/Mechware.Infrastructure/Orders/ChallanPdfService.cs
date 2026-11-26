using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Application.Orders;
using Mechware.Application.Pdf;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Orders.Pdf;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// <see cref="IChallanPdfService"/> (E6.4d). A challan is not a tax document: it is rendered from the order, its first shipment and the order lines on
/// every request, never stored, and carries no price (the model has no money field). Access is decided from the database: the placer, or an
/// effective Admin/Buyer of the order's company (<c>Credit:BalanceVisibleTo</c>); everything else is one <c>KeyNotFoundException</c>.
/// An order that has not shipped is 409 <c>CHALLAN_NOT_AVAILABLE</c> for those who may see it.
/// </summary>
public sealed class ChallanPdfService(MechwareDbContext db, IPdfRenderer renderer, InvoicingOptions options, CreditOptions credit) : IChallanPdfService
{
    private static KeyNotFoundException NotFound() => new("Order not found.");

    private static string Locale(string? locale)
    {
        var l = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim().ToLowerInvariant();
        return l is "en" or "bn" ? l : throw new ValidationException("locale", "The language must be en or bn.");
    }

    public async Task<ChallanPdfFile> BuyerAsync(Guid actorUserId, Guid orderId, string? locale, CancellationToken ct = default)
    {
        var lang = Locale(locale);
        var order = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.UserId, o.CompanyId }).FirstOrDefaultAsync(ct) ?? throw NotFound();
        if (order.UserId != actorUserId && !(order.CompanyId is { } company && await BuyerCreditAccess.MayViewAsync(db, actorUserId, company, credit, ct))) throw NotFound();
        return await BuildAsync(orderId, lang, ct);
    }

    public Task<ChallanPdfFile> StaffAsync(Guid orderId, string? locale, CancellationToken ct = default) => BuildAsync(orderId, Locale(locale), ct);

    private async Task<ChallanPdfFile> BuildAsync(Guid orderId, string lang, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct) ?? throw NotFound();
        var shipment = await db.Set<Shipment>().AsNoTracking().Where(s => s.OrderId == orderId).OrderBy(s => s.ShippedAtUtc).FirstOrDefaultAsync(ct)
            ?? throw new ConflictException("CHALLAN_NOT_AVAILABLE", "The delivery challan is available once the order has shipped.");
        var items = await db.Set<SubOrder>().AsNoTracking().Where(s => s.OrderId == orderId).SelectMany(s => s.Items).ToListAsync(ct);
        var buyer = order.CompanyId is { } c
            ? await db.Companies.AsNoTracking().Where(x => x.Id == c).Select(x => x.LegalName).FirstAsync(ct)
            : await db.Users.AsNoTracking().Where(u => u.Id == order.UserId).Select(u => u.FullName).FirstAsync(ct);
        var shipTo = InvoiceSnapshotBuilder.ShipTo(order.ShipToJson);
        var number = ChallanPdfComposer.ChallanNumber(order.OrderNumber, options.NumberPrefix);
        var data = new ChallanData(number, order.OrderNumber, order.PurchaseOrderNumber, FiscalYear.DhakaDate(shipment.ShippedAtUtc), buyer,
            string.IsNullOrWhiteSpace(shipTo?.RecipientName) ? null : shipTo.RecipientName,
            shipTo is null ? null : string.Join(", ", new[] { shipTo.SiteName, shipTo.OneLine() }.Where(x => !string.IsNullOrWhiteSpace(x))),
            shipment.CourierName, shipment.TrackingNumber,
            [.. items.OrderBy(i => i.Sku, StringComparer.Ordinal).ThenBy(i => i.Id).Select(i => new ChallanLine(i.Sku, i.Title, i.Quantity))]);
        return new ChallanPdfFile(ChallanPdfComposer.Render(renderer, data, lang, options).Bytes, $"{number}-{lang}.pdf");
    }
}
