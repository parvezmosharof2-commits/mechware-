using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;

namespace Mechware.IntegrationTests.Support;

/// <summary>Bare rows for credit ledger tests: the ledger only needs its orders to exist (foreign key), not their lines.</summary>
public static class CreditTestData
{
    public static async Task<Guid> InsertOrderAsync(MechwareDbContext db, Guid userId, Guid? companyId, long grandTotalMinor,
        bool onCredit = true, string status = "Confirmed", DateTime? confirmedAtUtc = null, DateTime? placedAtUtc = null)
    {
        var id = Guid.NewGuid();
        var placed = placedAtUtc ?? DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO orders (id, order_number, user_id, company_id, status, tax_mode, currency, subtotal, discount_total, tax_total,
                shipping_total, grand_total, ship_to, event_seq, placed_at, created_at, pays_on_credit, confirmed_at)
            VALUES ({id}, {OrderNumber.Generate(placed)}, {userId}, {companyId}, {status}, 'Exclusive', 'BDT', {grandTotalMinor}, 0, 0, 0,
                {grandTotalMinor}, '{{}}'::jsonb, 1, {placed}, {placed}, {onCredit}, {confirmedAtUtc})");
        return id;
    }

    /// <summary>An order event, as orders confirmed before E8.3 have (no <c>confirmed_at</c>, only the Confirmed event).</summary>
    public static Task AddEventAsync(MechwareDbContext db, Guid orderId, int seq, string from, string to, DateTime atUtc) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO order_events (id, order_id, seq, from_status, to_status, at, created_at)
            VALUES ({Guid.NewGuid()}, {orderId}, {seq}, {from}, {to}, {atUtc}, {atUtc})");
}
