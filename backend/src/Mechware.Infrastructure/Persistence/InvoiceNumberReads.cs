using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Invoicing;

namespace Mechware.Infrastructure.Persistence;

/// <summary>E6.3c: the tax invoice numbers of an order for the staff and buyer order views (shared read, so Orders needs no Invoicing type).</summary>
public static class InvoiceNumberReads
{
    /// <summary>Both modes, oldest first; numbers only (no ids, no money).</summary>
    public static Task<List<string>> NumbersOfAsync(this MechwareDbContext db, Guid orderId, CancellationToken ct) =>
        db.Invoices.AsNoTracking().Where(i => i.OrderId == orderId && i.Kind == InvoiceKind.TaxInvoice)
            .OrderBy(i => i.IssuedAtUtc).ThenBy(i => i.Number).Select(i => i.Number).ToListAsync(ct);
}
