using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;

namespace Mechware.Infrastructure.Credit;

/// <summary>
/// <see cref="IBuyerCreditService"/> (ADR-0010 §10, B3.9; threat model CS22-CS24). Reads only: no ledger line, no row lock, every answer from one
/// REPEATABLE READ snapshot (the B3.8 pattern, <see cref="CreditReadService.SnapshotAsync{T}"/>). The company id in the URL decides nothing by
/// itself: <see cref="BuyerCreditAccess"/> checks the caller's membership and role inside the same snapshot, and every query below is
/// filtered by the company that check approved. Buyer DTOs are built field by field; they never reuse the staff line (actor, reason, full reference).
/// </summary>
public sealed class BuyerCreditService(MechwareDbContext db, CreditOptions options, TimeProvider clock) : IBuyerCreditService
{
    public const int MaxPageSize = 100;
    private const int MaxPending = 50;

    public Task<BuyerCreditDto> GetAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default) =>
        CreditReadService.SnapshotAsync(db, async () =>
        {
            var company = await RequireAsync(actorUserId, companyId, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var today = CreditCalendar.DhakaDate(now);
            var open = await OpenOrdersAsync(companyId, ct);
            var aging = open.Aggregate(default(CreditAgingBuckets), (sum, o) => sum.Plus(CreditAging.Of(o.DueOn, o.Receivable, today)));
            DateOnly? oldest = open.Count == 0 ? null : open.Min(o => o.DueOn);
            var held = options.HoldDueBefore(now) is { } before && oldest < before && !company.CreditHoldLifted(now);   // the rule of the hold (B3.7)
            var pending = await db.CreditPayments.AsNoTracking().Where(p => p.CompanyId == companyId && p.Status == CreditPaymentStatus.Recorded)
                .OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).Take(MaxPending).ToListAsync(ct);
            return new BuyerCreditDto(companyId, CreditLedgerEntry.BdtCurrency, today, company.CreditTerms.ToString().ToLowerInvariant(), company.CreditLimitMinor,
                company.CreditUsedMinor, Math.Max(0, company.CreditLimitMinor - company.CreditUsedMinor), company.CreditOnAccountMinor, aging.Overdue, oldest, held, aging,
                pending.Select(p => new BuyerPendingPaymentDto(p.ReceivedOn, Wire(p.Method), p.AmountMinor, CreditBuyerView.ReferenceTail(p.Reference))).ToList());
        }, ct);

    public Task<CreditPage<BuyerOpenItemDto>> OpenItemsAsync(Guid actorUserId, Guid companyId, int page, int pageSize, CancellationToken ct = default) =>
        CreditReadService.SnapshotAsync(db, async () =>
        {
            await RequireAsync(actorUserId, companyId, ct);
            (page, pageSize) = Clamp(page, pageSize);
            var today = CreditCalendar.DhakaDate(clock.GetUtcNow().UtcDateTime);
            var open = await OpenOrdersAsync(companyId, ct);
            var rows = open.OrderBy(o => o.DueOn).ThenBy(o => o.OrderId).Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var ids = rows.Select(r => r.OrderId).ToList();
            var numbers = await db.Orders.AsNoTracking().Where(o => o.CompanyId == companyId && ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
            var charges = (await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == companyId && l.Kind == CreditLedgerKind.Charge && ids.Contains(l.OrderId!.Value))
                .Select(l => new { l.OrderId, l.ReceivableDeltaMinor }).ToListAsync(ct)).GroupBy(l => l.OrderId).ToDictionary(g => g.Key!.Value, g => g.Sum(l => l.ReceivableDeltaMinor));
            return new CreditPage<BuyerOpenItemDto>(rows.Select(r => new BuyerOpenItemDto(numbers.GetValueOrDefault(r.OrderId) ?? string.Empty, charges.GetValueOrDefault(r.OrderId), r.Receivable,
                r.DueOn, Math.Max(0, CreditAging.DaysPastDue(r.DueOn, today)),
                CreditBuyerView.OrderStatus(false, charges.GetValueOrDefault(r.OrderId), r.Receivable, r.DueOn, today))).ToList(), page, pageSize, open.Count);
        }, ct);

    public Task<BuyerStatementDto> StatementAsync(Guid actorUserId, Guid companyId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct = default) =>
        CreditReadService.SnapshotAsync(db, async () =>
        {
            var company = await RequireAsync(actorUserId, companyId, ct);
            if (from > to) throw new ValidationException("from", "The start day is after the end day.");
            (page, pageSize) = Clamp(page, pageSize);
            var query = db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == companyId && (from == null || l.OccurredOn >= from) && (to == null || l.OccurredOn <= to));
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(l => l.Seq).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var orderIds = rows.Where(r => r.OrderId is not null).Select(r => r.OrderId!.Value).Distinct().ToList();
            var paymentIds = rows.Where(r => r.PaymentId is not null).Select(r => r.PaymentId!.Value).Distinct().ToList();
            var numbers = await db.Orders.AsNoTracking().Where(o => o.CompanyId == companyId && orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
            var payments = await db.CreditPayments.AsNoTracking().Where(p => p.CompanyId == companyId && paymentIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Method, p.Reference }).ToDictionaryAsync(p => p.Id, ct);
            var invoiceNumbers = await db.NumbersOfAsync(numbers.Keys.ToList(), ct);
            var lines = rows.Select(l =>
            {
                var payment = l.PaymentId is { } id && payments.TryGetValue(id, out var p) ? p : null;
                return new BuyerStatementLineDto(l.Seq, l.OccurredOn, Wire(l.Kind), l.OrderId is { } o ? numbers.GetValueOrDefault(o) : null,
                    payment is null ? null : Wire(payment.Method), payment is null ? null : CreditBuyerView.ReferenceTail(payment.Reference),
                    l.ReceivableDeltaMinor, l.OnAccountDeltaMinor, l.ReceivableAfterMinor, l.OnAccountAfterMinor, l.DueOn,
                    l.OrderId is { } oi ? invoiceNumbers.GetValueOrDefault(oi) ?? [] : []);
            }).ToList();
            return new BuyerStatementDto(companyId, from, to, company.CreditUsedMinor, company.CreditOnAccountMinor, new CreditPage<BuyerStatementLineDto>(lines, page, pageSize, total));
        }, ct);

    /// <summary>The one answer for "no such company for you": unknown, not a member, wrong role, Pending or Rejected all look alike.</summary>
    private async Task<Company> RequireAsync(Guid actorUserId, Guid companyId, CancellationToken ct)
    {
        if (!await BuyerCreditAccess.MayViewAsync(db, actorUserId, companyId, options, ct)) throw new KeyNotFoundException("Company not found.");
        return await db.Companies.AsNoTracking().SingleAsync(c => c.Id == companyId, ct);
    }

    /// <summary>One row per order that still owes something: what is owed and the due date fixed at the charge (one grouped query, as in B3.8).</summary>
    private async Task<List<OpenOrder>> OpenOrdersAsync(Guid companyId, CancellationToken ct) =>
        (await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == companyId && l.OrderId != null)
            .GroupBy(l => l.OrderId).Where(g => g.Sum(l => l.ReceivableDeltaMinor) > 0)
            .Select(g => new { OrderId = g.Key, Receivable = g.Sum(l => l.ReceivableDeltaMinor), DueOn = g.Max(l => l.DueOn) }).ToListAsync(ct))
        .Select(g => new OpenOrder(g.OrderId!.Value, g.Receivable, g.DueOn!.Value)).ToList();

    private sealed record OpenOrder(Guid OrderId, long Receivable, DateOnly DueOn);

    private static (int Page, int PageSize) Clamp(int page, int pageSize) => (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());
}
