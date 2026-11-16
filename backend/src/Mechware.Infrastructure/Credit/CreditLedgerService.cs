using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Credit;

/// <summary>
/// ADR-0010 §8.2 (B3.5; threat model CS19): the reduction of a charge by a cancellation (E7.2) or a credit note (E6.5), atomic with
/// the caller's own change. It never opens a transaction, so cancelling and releasing the credit commit together or not at all.
/// </summary>
public sealed class CreditLedgerService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : ICreditLedger
{
    public const string ReducedEvent = "credit.charge_reduced";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CreditReductionResult?> ReduceChargeAsync(CreditReduction request, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("ReduceChargeAsync runs inside the caller's transaction (the cancel or the credit note), never its own.");
        var creditNote = request.Kind == CreditReductionKind.CreditNote;
        if (!Enum.IsDefined(request.Kind) || creditNote != request.CreditNoteId.HasValue || (creditNote && request.AmountMinor is null))
            throw new ValidationException("kind", "A credit note names itself and its amount; a cancellation names no credit note.");

        // Charge lines never change, so finding the company before its lock is safe; no charge = nothing to release.
        var companyId = await db.CreditLedgerEntries.Where(l => l.OrderId == request.OrderId && l.Kind == CreditLedgerKind.Charge)
            .Select(l => (Guid?)l.CompanyId).SingleOrDefaultAsync(ct);
        if (companyId is null) return null;
        var now = clock.GetUtcNow().UtcDateTime;
        var company = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {companyId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company.Id && l.OrderId == request.OrderId).ToListAsync(ct);
        var ledger = new CreditLedger(company, lines);
        var before = new { company.CreditUsedMinor, company.CreditOnAccountMinor };
        var eventId = Guid.NewGuid();
        var plan = CreditReversals.Reduce(request.OrderId, ledger.BalanceOf(request.OrderId), lines, request.AmountMinor, request.SourceKey,
            request.CreditNoteId, new CreditEventInfo(CreditCalendar.DhakaDate(now), eventId, request.ActorUserId, user.CorrelationId, request.Reason));
        var result = new CreditReductionResult(plan.ReceivableReleasedMinor, plan.MovedOnAccountMinor, plan.AlreadyApplied);
        if (plan.Postings.Count == 0) return result;

        foreach (var posting in plan.Postings)
            db.CreditLedgerEntries.Add(ledger.Post(posting, now).Entry);
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), request.ActorUserId, user.IpAddress, ReducedEvent, "orders", request.OrderId.ToString(),
            JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(new
            {
                EventId = eventId,
                CompanyId = company.Id,
                Kind = request.Kind.ToString(),
                request.SourceKey,
                request.CreditNoteId,
                request.Reason,
                result.ReceivableReleasedMinor,
                result.MovedOnAccountMinor,
                company.CreditUsedMinor,
                company.CreditOnAccountMinor
            }, Json), user.CorrelationId, now));
        await db.SaveChangesAsync(ct);
        return result;
    }
}
