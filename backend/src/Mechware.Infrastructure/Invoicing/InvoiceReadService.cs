using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Application.Credit;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// The invoice reads (E6.8a; ADR-0012 §14). Buyer access is decided here, from the database, never from the URL alone: an order read needs the
/// caller to be the order's placer (the rule of the buyer order detail); a company read needs an effective member whose role is in
/// <c>Credit:BalanceVisibleTo</c> (Admin, Buyer: the B3 rule) of an Active or Suspended company, and every query is then filtered by that company.
/// Every refusal is the one <see cref="KeyNotFoundException"/>. DTOs are built field by field; the buyer BIN is masked for buyers and whole for
/// staff. One REPEATABLE READ snapshot, no row lock.
/// </summary>
public sealed class InvoiceReadService(MechwareDbContext db, CreditOptions credit) : IBuyerInvoiceReadService, IStaffInvoiceReadService
{
    private static KeyNotFoundException NotFound() => new("Invoice not found.");

    // ---- buyer ------------------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<InvoiceSummaryDto>> ListForOrderAsync(Guid userId, Guid orderId, CancellationToken ct = default) =>
        ReadSnapshot.RunAsync<IReadOnlyList<InvoiceSummaryDto>>(db, async () =>
        {
            await RequirePlacerAsync(userId, orderId, ct);
            var rows = await db.Invoices.AsNoTracking().Where(i => i.OrderId == orderId).OrderBy(i => i.IssuedAtUtc).ThenBy(i => i.Number).ToListAsync(ct);
            return await SummariesAsync(rows, ct);
        }, ct);

    public Task<BuyerInvoiceDetailDto> GetForOrderAsync(Guid userId, Guid orderId, Guid invoiceId, CancellationToken ct = default) =>
        ReadSnapshot.RunAsync(db, async () =>
        {
            await RequirePlacerAsync(userId, orderId, ct);
            return await DetailAsync(db.Invoices.Where(i => i.Id == invoiceId && i.OrderId == orderId), maskBin: true, ct);
        }, ct);

    public Task<PagedResult<InvoiceSummaryDto>> ListForCompanyAsync(Guid userId, Guid companyId, string? from, string? to, int page, int pageSize, CancellationToken ct = default) =>
        ReadSnapshot.RunAsync(db, async () =>
        {
            await RequireCompanyReaderAsync(userId, companyId, ct);
            var (fromUtc, toUtc) = ParseRange(from, to);
            (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, IBuyerInvoiceReadService.MaxPageSize));
            var q = db.Invoices.AsNoTracking().Where(i => i.CompanyId == companyId);
            if (fromUtc is { } f) q = q.Where(i => i.IssuedAtUtc >= f);
            if (toUtc is { } t) q = q.Where(i => i.IssuedAtUtc < t);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(i => i.IssuedAtUtc).ThenBy(i => i.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return new PagedResult<InvoiceSummaryDto>(await SummariesAsync(rows, ct), page, pageSize, total);
        }, ct);

    public Task<BuyerInvoiceDetailDto> GetForCompanyAsync(Guid userId, Guid companyId, Guid invoiceId, CancellationToken ct = default) =>
        ReadSnapshot.RunAsync(db, async () =>
        {
            await RequireCompanyReaderAsync(userId, companyId, ct);
            return await DetailAsync(db.Invoices.Where(i => i.Id == invoiceId && i.CompanyId == companyId), maskBin: true, ct);
        }, ct);

    private async Task RequirePlacerAsync(Guid userId, Guid orderId, CancellationToken ct)
    {
        if (!await db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId && o.UserId == userId, ct)) throw NotFound();
    }

    private async Task RequireCompanyReaderAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        if (!await BuyerCreditAccess.MayViewAsync(db, userId, companyId, credit, ct)) throw NotFound();
    }

    // ---- staff ------------------------------------------------------------------------------------------------------------

    public Task<PagedResult<StaffInvoiceListItemDto>> ListAsync(StaffInvoiceFilter f, CancellationToken ct = default)
    {
        var kind = ParseWire<InvoiceKind>(f.Kind, "kind");
        var mode = ParseWire<InvoicingMode>(f.Mode, "mode");
        var number = Prefix(f.Number, "number");
        var orderNumber = Prefix(f.OrderNumber, "orderNumber");
        var (fromUtc, toUtc) = ParseRange(f.From, f.To);
        var (page, pageSize) = (Math.Max(1, f.Page), Math.Clamp(f.PageSize, 1, IBuyerInvoiceReadService.MaxPageSize));

        return ReadSnapshot.RunAsync(db, async () =>
        {
            var q = db.Invoices.AsNoTracking().AsQueryable();
            if (kind is { } k) q = q.Where(i => i.Kind == k);
            if (mode is { } m) q = q.Where(i => i.Mode == m);
            if (number is not null) q = q.Where(i => i.Number.StartsWith(number));
            if (orderNumber is not null) q = q.Where(i => i.OrderNumber.StartsWith(orderNumber));
            if (f.CompanyId is { } c) q = q.Where(i => i.CompanyId == c);
            if (fromUtc is { } from) q = q.Where(i => i.IssuedAtUtc >= from);
            if (toUtc is { } to) q = q.Where(i => i.IssuedAtUtc < to);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(i => i.IssuedAtUtc).ThenBy(i => i.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var summaries = await SummariesAsync(rows, ct);
            return new PagedResult<StaffInvoiceListItemDto>([.. rows.Select((i, n) => new StaffInvoiceListItemDto(summaries[n], i.SellerId, i.CompanyId, i.Buyer.Name))], page, pageSize, total);
        }, ct);
    }

    public Task<StaffInvoiceDetailDto> GetAsync(Guid invoiceId, CancellationToken ct = default) =>
        ReadSnapshot.RunAsync(db, async () =>
        {
            var i = await db.Invoices.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == invoiceId, ct) ?? throw NotFound();
            return new StaffInvoiceDetailDto(await ViewAsync(i, maskBin: false, ct), i.Series.ToString(), i.FiscalYear, i.Serial, i.SellerId, i.CompanyId, i.SubOrderId,
                i.ShipmentId, i.ConsultantChecked, i.ContentHash, i.IssuedBy);
        }, ct);

    // ---- views (field by field) -------------------------------------------------------------------------------------------

    private async Task<BuyerInvoiceDetailDto> DetailAsync(IQueryable<Invoice> scoped, bool maskBin, CancellationToken ct) =>
        await ViewAsync(await scoped.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(ct) ?? throw NotFound(), maskBin, ct);

    private async Task<BuyerInvoiceDetailDto> ViewAsync(Invoice i, bool maskBin, CancellationToken ct) =>
        new((await SummariesAsync([i], ct))[0], i.Seller, new InvoiceBuyerDto(i.Buyer.Name, maskBin ? MaskBin(i.Buyer.Bin) : i.Buyer.Bin, i.Buyer.Address), i.Delivery,
            i.PurchaseOrderNumber, i.QuoteNumber,
            [.. i.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineDto(l.LineNo, Wire(l.Kind), l.Sku, l.PartNumber, l.DescriptionEn, l.DescriptionBn, l.Unit,
                l.Quantity, l.UnitPriceMinor, l.DisplayUnitPriceMinor, l.GrossMinor, l.DiscountMinor, l.NetMinor, l.SdRate, l.SdMinor, l.VatRate, l.VatMinor, l.TotalMinor))]);

    private async Task<List<InvoiceSummaryDto>> SummariesAsync(IReadOnlyList<Invoice> rows, CancellationToken ct)
    {
        var originalIds = rows.Where(r => r.OriginalInvoiceId != null).Select(r => r.OriginalInvoiceId!.Value).Distinct().ToList();
        var originals = originalIds.Count == 0 ? [] : await db.Invoices.AsNoTracking().Where(o => originalIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        return [.. rows.Select(i => new InvoiceSummaryDto(i.Id, i.Number, Wire(i.Kind), Wire(i.Mode), i.IssuedAtUtc, i.OrderId, i.OrderNumber,
            i.OriginalInvoiceId, i.OriginalInvoiceId is { } o ? originals.GetValueOrDefault(o) : null, i.Currency, i.NetMinor, i.SdMinor, i.VatMinor, i.TotalMinor))];
    }

    /// <summary>Last four digits only ("*********7890"): the whole BIN is on the PDF, not in a list or a JSON answer for a buyer.</summary>
    internal static string? MaskBin(string? bin) =>
        string.IsNullOrEmpty(bin) ? null : bin.Length <= 4 ? new string('*', bin.Length) : new string('*', bin.Length - 4) + bin[^4..];

    private static string? Prefix(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant();
        return t.Length > 40 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? throw new ValidationException(field, "Use letters, digits and dashes, at most 40 characters.") : t;
    }

    private static T? ParseWire<T>(string? text, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var value in Enum.GetValues<T>())
            if (Wire(value) == text.Trim().ToLowerInvariant()) return value;
        throw new ValidationException(field, "Unknown value: " + string.Join(", ", Enum.GetValues<T>().Select(v => Wire(v))) + ".");
    }

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());

    /// <summary>Dhaka calendar days (yyyy-MM-dd, inclusive) to the UTC range [from 00:00 +06, the day after "to" 00:00 +06).</summary>
    private static (DateTime? FromUtc, DateTime? ToUtc) ParseRange(string? from, string? to)
    {
        static DateTime? Day(string? text, string field, int add)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? DateTime.SpecifyKind(d.AddDays(add).ToDateTime(TimeOnly.MinValue) - TimeSpan.FromHours(6), DateTimeKind.Utc)
                : throw new ValidationException(field, "Use a date like 2026-11-21 (Dhaka calendar date).");
        }
        var (f, t) = (Day(from, "from", 0), Day(to, "to", 1));
        return f > t - TimeSpan.FromDays(1) ? throw new ValidationException("from", "'from' must not be after 'to'.") : (f, t);
    }
}
