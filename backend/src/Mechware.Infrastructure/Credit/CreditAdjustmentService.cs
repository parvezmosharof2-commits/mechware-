using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Credit;

/// <summary>
/// ADR-0010 §8.3 (B3.6, threat model CS20, CS21). A request writes the adjustment and an audit row, nothing else. The approval is
/// one transaction with no outside call: <c>credit_adjustments</c> row (NO KEY UPDATE) → its source <c>credit_payments</c> row (NO KEY
/// UPDATE, the lock a bounce or a verification takes first, so a payout never races a bounce of the same money) → <c>companies</c>
/// (FOR UPDATE) → the order's or the source's lines → <see cref="CreditAdjustment.Posting"/> (every cap again, never clamped) →
/// <see cref="CreditLedger"/> → one audit row and one outbox event. A write-off never locks its order: its line takes FOR KEY SHARE
/// through the foreign key, which a cancel holding the order (NO KEY UPDATE) does not block (CS16).
/// </summary>
public sealed class CreditAdjustmentService(MechwareDbContext db, ICurrentUserService user, CreditOptions options, TimeProvider clock) : ICreditAdjustmentService
{
    public const string ApprovedEvent = "credit.adjustment_approved";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, CreditAdjustmentKind> Kinds =
        Enum.GetValues<CreditAdjustmentKind>().ToDictionary(k => Wire(k), StringComparer.Ordinal);

    public async Task<RequestedCreditAdjustment> RequestAsync(Guid companyId, RequestCreditAdjustmentRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        if (idempotencyKey is not { Length: >= 8 and <= CreditPayment.MaxIdempotencyKeyLength } || idempotencyKey.Any(c => c is <= ' ' or > '~'))
            throw new ValidationException("Idempotency-Key", $"The Idempotency-Key header is 8 to {CreditPayment.MaxIdempotencyKeyLength} printable ASCII characters.");
        if (request.Currency is not (null or CreditLedgerEntry.BdtCurrency)) throw new ValidationException("currency", "Credit adjustments are in BDT only.");
        var kind = Kinds.GetValueOrDefault(request.Kind ?? string.Empty, (CreditAdjustmentKind)0);
        if (kind == 0) throw new ValidationException("kind", $"Use one of: {string.Join(", ", Kinds.Keys)}.");
        // The id IS the idempotency memory: a hash of (person, key). A replay finds the row; another body under the key is 409.
        var asked = CreditAdjustment.Request(IdOf(actor, idempotencyKey), new CreditAdjustmentInput(companyId, kind, request.AmountMinor,
            request.Reason ?? string.Empty, request.OrderId, request.PaymentId, request.CreditNoteId, request.PayoutReference), actor, options.MaxPaymentMinor, now);
        if (await ReplayAsync(asked, ct) is { } replay) return replay;

        // A suspended company still owes and still holds its money; unknown, pending and rejected companies look the same.
        if (!await db.Companies.AnyAsync(c => c.Id == companyId && (c.Status == CompanyStatus.Active || c.Status == CompanyStatus.Suspended), ct))
            throw new KeyNotFoundException("Company not found.");
        var source = asked.PaymentId is { } paymentId ? await db.CreditPayments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId, ct) : null;
        if (asked.PaymentId is not null && source is null)   // another company's payment: Posting answers the same
            throw new ConflictException("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND", "This payment is not the company's.");
        // An early answer from the ledger as it stands; the approval checks every cap again under its locks.
        asked.Posting(await LinesAsync(asked, ct), source, options.PayoutAfterClearingDays,
            new CreditEventInfo(CreditCalendar.DhakaDate(now), Guid.Empty, actor, user.CorrelationId, asked.Reason));

        db.CreditAdjustments.Add(asked);
        db.AuditLogs.Add(Audit("credit.adjustment_requested", asked, null, new
        {
            asked.CompanyId,
            Kind = Wire(asked.Kind),
            asked.AmountMinor,
            asked.OrderId,
            asked.PaymentId,
            asked.CreditNoteId,
            asked.PayoutReference,
            asked.Reason
        }, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();   // a parallel replay inserted the same id first
            return await ReplayAsync(asked, ct) ?? throw new InvalidOperationException("The adjustment id collided without a row.", ex);
        }
        return new RequestedCreditAdjustment(ToDto(asked), false);
    }

    public async Task<ApprovedCreditAdjustment> ApproveAsync(Guid adjustmentId, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A second approver waits on this lock, then finds the adjustment approved: 409, never a second line.
        var adjustment = await LockAdjustmentAsync(adjustmentId, ct);
        var source = adjustment.PaymentId is { } paymentId
            ? (await db.CreditPayments.FromSqlInterpolated($"SELECT p.*, p.xmin FROM credit_payments p WHERE p.id = {paymentId} FOR NO KEY UPDATE")
                .ToListAsync(ct)).Single()
            : null;
        adjustment.Approve(actor, now, source);   // status, four eyes: not the requester, nor the source's recorder or verifier
        var company = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {adjustment.CompanyId} FOR UPDATE")
            .ToListAsync(ct)).Single();

        var lines = await LinesAsync(adjustment, ct);   // read under the company lock: every writer of these lines holds it
        var ledger = new CreditLedger(company, lines);
        var before = new { company.CreditUsedMinor, company.CreditOnAccountMinor };
        var eventId = Guid.NewGuid();
        var posted = ledger.Post(adjustment.Posting(lines, source, options.PayoutAfterClearingDays,
            new CreditEventInfo(CreditCalendar.DhakaDate(now), eventId, actor, user.CorrelationId, adjustment.Reason)), now);
        if (posted.AlreadyApplied) throw new InvalidOperationException($"Adjustment {adjustment.Id} already has its line but is still requested.");
        var line = posted.Entry;
        db.CreditLedgerEntries.Add(line);   // a new line; the old ones stay as they are (append-only)

        db.AuditLogs.Add(Audit("credit.adjustment_approved", adjustment, before, new
        {
            EventId = eventId,
            adjustment.CompanyId,
            Kind = Wire(adjustment.Kind),
            adjustment.AmountMinor,
            line.ReceivableDeltaMinor,
            line.OnAccountDeltaMinor,
            company.CreditUsedMinor,
            company.CreditOnAccountMinor
        }, now));
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            EventType = ApprovedEvent,
            AggregateType = nameof(CreditAdjustment),
            AggregateId = adjustment.Id,
            OccurredAtUtc = now,
            Payload = JsonSerializer.Serialize(new { EventId = eventId, AdjustmentId = adjustment.Id, adjustment.CompanyId, Kind = Wire(adjustment.Kind), adjustment.AmountMinor }, Json)
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ApprovedCreditAdjustment(ToDto(adjustment), eventId, line.ReceivableDeltaMinor, line.OnAccountDeltaMinor,
            company.CreditUsedMinor, company.CreditOnAccountMinor);
    }

    public async Task<CreditAdjustmentDto> RejectAsync(Guid adjustmentId, RejectCreditAdjustmentRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var adjustment = await LockAdjustmentAsync(adjustmentId, ct);
        adjustment.Reject(actor, request.Reason ?? string.Empty, now);   // the requester may withdraw: nothing moves
        db.AuditLogs.Add(Audit("credit.adjustment_rejected", adjustment, null, new { adjustment.CompanyId, Reason = adjustment.DecisionReason }, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(adjustment);
    }

    /// <summary>The company's lines of the adjustment's order (write-off) or of its one source (correction, payout).</summary>
    private Task<List<CreditLedgerEntry>> LinesAsync(CreditAdjustment a, CancellationToken ct)
    {
        var (company, order, payment, note) = (a.CompanyId, a.OrderId, a.PaymentId, a.CreditNoteId);
        var lines = db.CreditLedgerEntries.Where(l => l.CompanyId == company);
        return (order is not null ? lines.Where(l => l.OrderId == order) : lines.Where(l => l.PaymentId == payment && l.CreditNoteId == note)).ToListAsync(ct);
    }

    private async Task<RequestedCreditAdjustment?> ReplayAsync(CreditAdjustment asked, CancellationToken ct)
    {
        var e = await db.CreditAdjustments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == asked.Id, ct);
        if (e is null) return null;
        return (e.RequestedBy, e.CompanyId, e.Kind, e.AmountMinor, e.OrderId, e.PaymentId, e.CreditNoteId, e.PayoutReference, e.Reason)
            == (asked.RequestedBy, asked.CompanyId, asked.Kind, asked.AmountMinor, asked.OrderId, asked.PaymentId, asked.CreditNoteId, asked.PayoutReference, asked.Reason)
            ? new RequestedCreditAdjustment(ToDto(e), true)
            : throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different adjustment.");
    }

    /// <summary>A UUID (version 8) from SHA-256 of (person, key): the same request always gets the same id.</summary>
    private static Guid IdOf(Guid actor, string key)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"credit-adjustment|{actor:N}|{key}"));
        (h[7], h[8]) = ((byte)((h[7] & 0x0F) | 0x80), (byte)((h[8] & 0x3F) | 0x80));
        return new Guid(h.AsSpan(0, 16));
    }

    // NO KEY UPDATE: one decision at a time, while the ledger line of this approval (its foreign key) takes FOR KEY SHARE on it.
    private async Task<CreditAdjustment> LockAdjustmentAsync(Guid id, CancellationToken ct) =>
        (await db.CreditAdjustments.FromSqlInterpolated($"SELECT a.*, a.xmin FROM credit_adjustments a WHERE a.id = {id} FOR NO KEY UPDATE").ToListAsync(ct))
        .SingleOrDefault() ?? throw new KeyNotFoundException("Adjustment not found.");

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private AuditLog Audit(string action, CreditAdjustment a, object? before, object after, DateTime now) =>
        new(Guid.NewGuid(), Actor, user.IpAddress, action, "credit_adjustments", a.Id.ToString(),
            before is null ? null : JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json), user.CorrelationId, now);

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());

    private static CreditAdjustmentDto ToDto(CreditAdjustment a) => new(a.Id, a.CompanyId, Wire(a.Kind), a.AmountMinor, a.Currency, a.OrderId, a.PaymentId,
        a.CreditNoteId, a.PayoutReference, a.Reason, Wire(a.Status), a.RequestedBy, a.CreatedAtUtc, a.ApprovedBy, a.ApprovedAtUtc, a.RejectedBy,
        a.RejectedAtUtc, a.DecisionReason);
}
