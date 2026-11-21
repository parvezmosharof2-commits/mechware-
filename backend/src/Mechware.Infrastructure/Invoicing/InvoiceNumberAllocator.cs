using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mechware.Application.Invoicing;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// ADR-0012 §4 steps 1–5 inside the caller's transaction (threat model IT1, IT10, IT11):
/// <list type="number">
/// <item>create the series row once (year 0, next 1; <c>ON CONFLICT DO NOTHING</c>);</item>
/// <item>lock it <c>FOR UPDATE</c>: parallel issuers of the series wait here, and READ COMMITTED re-reads the committed row;</item>
/// <item>read <c>clock_timestamp()</c> AFTER the lock (never <c>now()</c>, the transaction start, and never an app clock) as the
/// issue instant; the fiscal year comes from its Dhaka date;</item>
/// <item>decide the serial (<see cref="InvoiceNumberCounter.Take"/>);</item>
/// <item>write the row with a plain UPDATE, so a ROLLBACK gives the serial back.</item>
/// </list>
/// Raw SQL only and untracked, so the caller's own SaveChanges never writes the counter a second time. The clock call is left
/// unqualified on purpose (tests pin the instant by a schema earlier on the search path).
/// </summary>
public sealed class InvoiceNumberAllocator(MechwareDbContext db, ILogger<InvoiceNumberAllocator> logger) : IInvoiceNumberAllocator
{
    public async Task<TakenInvoiceNumber> TakeAsync(Guid sellerId, InvoiceSeries series, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An invoice number is taken inside the issuing transaction, as its last lock, never in its own.");
        if (!Enum.IsDefined(series)) throw new ArgumentOutOfRangeException(nameof(series));
        var name = series.ToString();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO invoice_number_counters (seller_id, series, fiscal_year, next_serial, last_issued_at)
            VALUES ({sellerId}, {name}, 0, 1, NULL) ON CONFLICT (seller_id, series) DO NOTHING
            """, ct);
        var counter = (await db.InvoiceNumberCounters.FromSqlInterpolated(
                $"SELECT * FROM invoice_number_counters WHERE seller_id = {sellerId} AND series = {name} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct)).Single();
        var issuedAt = (await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();

        var lastIssuedAt = counter.LastIssuedAtUtc;
        if (counter.Take(issuedAt) is not { } taken)
        {
            var behind = new InvoiceClockBehindException(series, issuedAt, lastIssuedAt);
            logger.LogError(behind, "ALERT {Code}: seller {SellerId} series {Series}", InvoiceClockBehindException.Code, sellerId, name);
            throw behind;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE invoice_number_counters SET fiscal_year = {counter.Year}, next_serial = {counter.NextSerial}, last_issued_at = {issuedAt}
             WHERE seller_id = {sellerId} AND series = {name}
            """, ct);
        return new TakenInvoiceNumber(sellerId, series, taken.FiscalYear, taken.Serial, issuedAt);
    }
}
