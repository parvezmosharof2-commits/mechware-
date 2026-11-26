using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// E6.3c, ADR-0012 §2, §3, §15, threat model IT7, IT9, IT13, IT15. Runs inside the ship transaction after the order lock:
/// <list type="number">
/// <item>reads the buyer (company + default delivery site as billing address, or the person's name) and the quote number, and in Live
/// mode checks the three refusals BEFORE any counter is touched;</item>
/// <item>takes <c>FOR KEY SHARE</c> on the rows the invoice's foreign keys point at (sub-orders, sellers, company) and writes the
/// shipment and the order's move, so the inserts' own locks are already held;</item>
/// <item>takes one number per issuing seller (seller id order) as the LAST lock, builds each invoice with the database instant
/// <c>taken.IssuedAtUtc</c> (never an app clock: the COMMIT check compares it with the counter), and inserts invoice, lines, audit
/// <c>invoice.issued</c> and outbox <c>InvoiceIssuedEvent</c>. The caller commits; nothing slow runs here.</item>
/// </list>
/// Specimen issues only for the configured <c>IssuerSellerId</c> when one is set (every seller otherwise); Live refuses any other seller.
/// </summary>
public sealed class InvoiceIssuer(
    MechwareDbContext db, IInvoiceNumberAllocator allocator, InvoicingOptions options, ICurrentUserService user, TimeProvider clock,
    ILogger<InvoiceIssuer> logger) : IInvoiceIssuer
{
    public const string IssuedEvent = "InvoiceIssuedEvent";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<string>> IssueAsync(Order order, Shipment shipment, Guid issuedBy, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A tax invoice is issued inside the ship transaction, never in its own.");
        var mode = options.EffectiveMode;
        if (mode == InvoicingMode.Off)
        {
            logger.LogWarning("Invoicing is Off: order {OrderNumber} ships without a tax invoice (ADR-0012 §3).", order.OrderNumber);
            return [];
        }

        Company? company = null;
        CompanySite? site = null;
        string? buyerName = null;
        if (order.CompanyId is { } companyId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {companyId} FOR KEY SHARE", ct);
            company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
            site = await db.CompanySites.AsNoTracking().Where(s => s.CompanyId == companyId && s.IsDefaultDelivery).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        }
        else
        {
            var person = await db.Users.AsNoTracking().Where(u => u.Id == order.UserId).Select(u => new { u.FullName, u.Email }).FirstAsync(ct);
            buyerName = string.IsNullOrWhiteSpace(person.FullName) ? person.Email : person.FullName;
        }

        var subs = order.SubOrders
            .Where(s => mode == InvoicingMode.Live || options.IssuerSellerId is not { } issuer || s.SellerId == issuer)
            .OrderBy(s => s.SellerId).ToList();
        if (mode == InvoicingMode.Live)
            foreach (var sub in subs) InvoiceSnapshotBuilder.EnsureLiveIssuable(options, sub.SellerId, order, company);   // 409, no counter touched
        if (subs.Count == 0)
        {
            logger.LogInformation("Specimen invoicing: order {OrderNumber} has no sub-order of the issuer seller.", order.OrderNumber);
            return [];
        }

        var quoteNumber = order.QuoteId is { } quoteId
            ? await db.Quotes.AsNoTracking().Where(q => q.Id == quoteId).Select(q => q.Number).FirstOrDefaultAsync(ct) : null;
        foreach (var sub in subs) await db.Entry(sub).Collection(s => s.Items).LoadAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM sub_orders so JOIN sellers se ON se.id = so.seller_id WHERE so.order_id = {order.Id} FOR KEY SHARE", ct);
        await db.SaveChangesAsync(ct);   // the order's move, the shipment, their audit + outbox: inserted before the counter (§15)

        var series = mode == InvoicingMode.Live ? InvoiceSeries.TaxInvoice : InvoiceSeries.SpecimenTaxInvoice;
        var numbers = new List<string>();
        foreach (var sub in subs)
        {
            var taken = await allocator.TakeAsync(sub.SellerId, series, ct);   // the LAST lock; only inserts follow
            var invoice = InvoiceSnapshotBuilder.BuildTaxInvoice(new InvoiceIssue(Guid.NewGuid(), order, sub.SellerId, shipment, company, site, buyerName,
                quoteNumber, taken.Series, taken.FiscalYear, taken.Serial, taken.IssuedAtUtc, issuedBy), options);
            db.Invoices.Add(invoice);
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), issuedBy, user.IpAddress, "invoice.issued", "invoices", invoice.Id.ToString(), null,
                JsonSerializer.Serialize(new
                {
                    invoice.Number,
                    Mode = mode.ToString(),
                    order.OrderNumber,
                    OrderId = order.Id,
                    ShipmentId = shipment.Id,
                    invoice.SellerId,
                    invoice.TotalMinor,
                    invoice.ContentHash
                }, Json), user.CorrelationId, taken.IssuedAtUtc));
            var eventId = Guid.NewGuid();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = eventId,
                EventType = IssuedEvent,
                AggregateType = nameof(Invoice),
                AggregateId = invoice.Id,
                OccurredAtUtc = taken.IssuedAtUtc,
                Payload = JsonSerializer.Serialize(new { EventId = eventId, InvoiceId = invoice.Id, OrderId = order.Id, invoice.Number, Mode = mode.ToString() }, Json)
            });
            numbers.Add(invoice.Number);
        }

        await db.SaveChangesAsync(ct);   // invoices + lines + audit + outbox; the deferred counter <-> invoice checks run at the caller's COMMIT
        return numbers;
    }

    public async Task RecordRefusalAsync(Guid orderId, string orderNumber, ConflictException refusal, CancellationToken ct = default)
    {
        // The ship transaction is gone (rolled back), so this SaveChanges commits on its own: the refusal stays on record although the
        // shipment, the order's move and any counter step did not happen.
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("The refusal is recorded after the ship transaction rolled back, never inside it.");
        db.ChangeTracker.Clear();
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), user.UserId, user.IpAddress, "invoice.issue_refused", "orders", orderId.ToString(), null,
            JsonSerializer.Serialize(new { refusal.Code, Reason = refusal.Message, OrderNumber = orderNumber, Mode = options.EffectiveMode.ToString() }, Json),
            user.CorrelationId, clock.GetUtcNow().UtcDateTime));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(e, "Could not record the invoice refusal {Code} of order {OrderNumber}; the ship was refused anyway.", refusal.Code, orderNumber);
        }
    }
}
