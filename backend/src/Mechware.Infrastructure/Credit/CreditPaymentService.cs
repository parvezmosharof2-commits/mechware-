using System.Security.Cryptography;
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
/// ADR-0010 §4-§7 (B3.4). Lock order of a decision: <c>credit_payments</c> row, then the <c>companies</c> row (§7; settlement never
/// locks an order). The verification reads the company's open receivables under the company lock, plans with the pure
/// <see cref="CreditAllocationPlanner"/>, posts through <see cref="CreditLedger"/> (the only way <c>credit_used</c> moves) and
/// commits the payment, the lines, the cached totals, one audit row and one outbox event together, with no outside call inside.
/// A bounce (§8.1, B3.5) takes the same locks and writes new <c>payment_reversed</c> lines (<see cref="CreditReversals.Bounce"/>).
/// </summary>
public sealed class CreditPaymentService(MechwareDbContext db, ICurrentUserService user, CreditOptions options, TimeProvider clock) : ICreditPaymentService
{
    public const string VerifiedEvent = "credit.payment_verified";
    public const string BouncedEvent = "credit.payment_bounced";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, CreditPaymentMethod> Methods =
        Enum.GetValues<CreditPaymentMethod>().ToDictionary(m => Wire(m), StringComparer.Ordinal);

    public async Task<RecordedCreditPayment> RecordAsync(Guid companyId, RecordCreditPaymentRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        if (string.IsNullOrEmpty(idempotencyKey)) throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { companyId, request }, Json)));
        if (await ReplayAsync(actor, idempotencyKey, hash, ct) is { } replay) return replay;

        // A suspended company still pays its debt (CS24); unknown, pending and rejected companies look the same.
        if (!await db.Companies.AnyAsync(c => c.Id == companyId && (c.Status == CompanyStatus.Active || c.Status == CompanyStatus.Suspended), ct))
            throw new KeyNotFoundException("Company not found.");
        if (request.Currency is not (null or CreditLedgerEntry.BdtCurrency)) throw new ValidationException("currency", "Credit payments are in BDT only.");
        var method = Methods.GetValueOrDefault(request.Method ?? string.Empty, (CreditPaymentMethod)0);
        if (method == 0) throw new ValidationException("method", $"Use one of: {string.Join(", ", Methods.Keys)}.");
        var input = new CreditPaymentInput(companyId, method, request.AmountMinor, request.Reference, request.DrawerBank,
            request.ReceivedOn ?? throw new ValidationException("receivedOn", "Give the day the money was received."), request.ChequeDate,
            request.LateEntryReason, request.Allocations?.Select(a => new CreditAllocationRequest(a.OrderId, a.AmountMinor)).ToList(), request.Note);
        var payment = CreditPayment.Record(Guid.NewGuid(), input, actor, idempotencyKey, hash, options.PaymentRules(), now);
        db.CreditPayments.Add(payment);
        db.AuditLogs.Add(Audit("credit.payment_recorded", payment, null,
            new { payment.CompanyId, Method = Wire(payment.Method), payment.AmountMinor, payment.ReceivedOn, payment.Allocations }, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            db.ChangeTracker.Clear();
            if (pg.ConstraintName == "ux_credit_payments_idempotency" && await ReplayAsync(actor, idempotencyKey, hash, ct) is { } raced) return raced;
            if (pg.ConstraintName is "ux_credit_payments_transfer_reference" or "ux_credit_payments_instrument_reference")
                throw new ConflictException("CREDIT_PAYMENT_DUPLICATE_REFERENCE",
                    "This reference is already recorded (for this or another company). Reject the wrong entry first if this one is right.");
            throw;
        }
        return new RecordedCreditPayment(ToDto(payment), false);
    }

    public async Task<bool> VerificationNeedsStepUpAsync(Guid paymentId, CancellationToken ct = default) =>
        options.StepUpOnEveryVerification
        || (await db.CreditPayments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => (long?)p.AmountMinor).SingleOrDefaultAsync(ct)
            ?? long.MaxValue) >= options.VerifyStepUpFromMinor;

    public async Task<VerifiedCreditPayment> VerifyAsync(Guid paymentId, VerifyCreditPaymentRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        var clearedOn = request.ClearedOn ?? throw new ValidationException("clearedOn", "Give the day the money cleared.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A second verifier waits on this lock, then finds the payment verified: 409, never a second release (CS1).
        var payment = await LockPaymentAsync(paymentId, ct);
        payment.Verify(actor, clearedOn, now);   // status, four eyes (CS2), cheque and clearing dates (CS11)
        var company = await LockCompanyAsync(payment.CompanyId, ct);

        // Every line of every order that still owes something (the per-order caps need all of an order's lines), read under the lock.
        var open = await db.CreditLedgerEntries.Where(l => l.CompanyId == company.Id && l.OrderId != null)
            .GroupBy(l => l.OrderId).Where(g => g.Sum(l => l.ReceivableDeltaMinor) > 0).Select(g => g.Key).ToListAsync(ct);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company.Id && open.Contains(l.OrderId)).ToListAsync(ct);
        var ledger = new CreditLedger(company, lines);
        var charges = lines.Where(l => l.Kind == CreditLedgerKind.Charge)
            .Select(l => new CreditOpenCharge(l.OrderId!.Value, ledger.BalanceOf(l.OrderId.Value).ReceivableMinor, l.DueOn!.Value, l.CreatedAtUtc));
        var plan = CreditAllocationPlanner.Plan(payment.AmountMinor, charges, payment.Allocations);   // CS6-CS8

        var before = new { company.CreditUsedMinor, company.CreditOnAccountMinor };
        var eventId = Guid.NewGuid();
        foreach (var posting in plan.Postings(payment.Id, clearedOn, eventId, actor, user.CorrelationId))
            db.CreditLedgerEntries.Add(ledger.Post(posting, now).Entry);
        db.AuditLogs.Add(Audit(VerifiedEvent, payment, before, new
        {
            EventId = eventId,
            payment.CompanyId,
            payment.AmountMinor,
            payment.ClearedOn,
            Applied = plan.Allocations,
            plan.OnAccountMinor,
            company.CreditUsedMinor,
            company.CreditOnAccountMinor
        }, now));
        db.OutboxMessages.Add(Outbox(VerifiedEvent, payment, eventId, now, new { EventId = eventId, PaymentId = payment.Id, payment.CompanyId, payment.AmountMinor, plan.OnAccountMinor }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new VerifiedCreditPayment(ToDto(payment), eventId, plan.Allocations, plan.OnAccountMinor, company.CreditUsedMinor, company.CreditOnAccountMinor);
    }

    public async Task<BouncedCreditPayment> BounceAsync(Guid paymentId, BounceCreditPaymentRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Same lock order as a verification: a bounce racing the verification of the same payment waits for it (CS12, CS16).
        var payment = await LockPaymentAsync(paymentId, ct);
        payment.Bounce(actor, request.Reason ?? string.Empty, now);   // verified only, so a second bounce is 409; never cash
        var company = await LockCompanyAsync(payment.CompanyId, ct);

        // The lines in this payment's name, and every line of the orders it reached (the per-order caps need them all).
        var orders = await db.CreditLedgerEntries.Where(l => l.CompanyId == company.Id && l.PaymentId == payment.Id && l.OrderId != null)
            .Select(l => l.OrderId).Distinct().ToListAsync(ct);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company.Id && (l.PaymentId == payment.Id || orders.Contains(l.OrderId)))
            .ToListAsync(ct);
        var ledger = new CreditLedger(company, lines);
        var before = new { company.CreditUsedMinor, company.CreditOnAccountMinor };
        var eventId = Guid.NewGuid();
        var postings = CreditReversals.Bounce(payment.Id, lines,
            new CreditEventInfo(CreditCalendar.DhakaDate(now), eventId, actor, user.CorrelationId, payment.DecisionReason!));
        foreach (var posting in postings)
            db.CreditLedgerEntries.Add(ledger.Post(posting, now).Entry);   // the old lines stay as they are: append-only

        var reversed = postings.Where(p => p.OrderId is not null).Select(p => new CreditAllocation(p.OrderId!.Value, p.ReceivableDeltaMinor)).ToList();
        var onAccount = -postings.Sum(p => p.OnAccountDeltaMinor);
        db.AuditLogs.Add(Audit(BouncedEvent, payment, before, new
        {
            EventId = eventId,
            payment.CompanyId,
            payment.AmountMinor,
            Reason = payment.DecisionReason,
            Reversed = reversed,
            OnAccountReversedMinor = onAccount,
            company.CreditUsedMinor,
            company.CreditOnAccountMinor
        }, now));
        db.OutboxMessages.Add(Outbox(BouncedEvent, payment, eventId, now, new { EventId = eventId, PaymentId = payment.Id, payment.CompanyId, payment.AmountMinor }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new BouncedCreditPayment(ToDto(payment), eventId, reversed, onAccount, company.CreditUsedMinor, company.CreditOnAccountMinor);
    }

    public async Task<CreditPaymentDto> RejectAsync(Guid paymentId, RejectCreditPaymentRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = await LockPaymentAsync(paymentId, ct);
        payment.Reject(actor, request.Reason ?? string.Empty, now);   // four eyes too; it frees the reference for a corrected entry
        db.AuditLogs.Add(Audit("credit.payment_rejected", payment, null, new { payment.CompanyId, payment.AmountMinor, Reason = payment.DecisionReason }, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(payment);
    }

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // NO KEY UPDATE, not UPDATE: it still queues a second verify/bounce of the payment, but lets a reduction that already holds the
    // company lock insert ledger lines naming this payment (their foreign key takes FOR KEY SHARE); FOR UPDATE deadlocks there (CS16).
    private async Task<CreditPayment> LockPaymentAsync(Guid paymentId, CancellationToken ct) =>
        (await db.CreditPayments.FromSqlInterpolated($"SELECT p.*, p.xmin FROM credit_payments p WHERE p.id = {paymentId} FOR NO KEY UPDATE").ToListAsync(ct))
        .SingleOrDefault() ?? throw new KeyNotFoundException("Payment not found.");

    private async Task<Company> LockCompanyAsync(Guid companyId, CancellationToken ct) =>
        (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {companyId} FOR UPDATE").ToListAsync(ct)).Single();

    private static OutboxMessage Outbox(string type, CreditPayment p, Guid eventId, DateTime now, object payload) => new()
    {
        Id = eventId,
        EventType = type,
        AggregateType = nameof(CreditPayment),
        AggregateId = p.Id,
        OccurredAtUtc = now,
        Payload = JsonSerializer.Serialize(payload, Json)
    };

    private async Task<RecordedCreditPayment?> ReplayAsync(Guid actor, string key, string hash, CancellationToken ct)
    {
        var existing = await db.CreditPayments.AsNoTracking().SingleOrDefaultAsync(p => p.RecordedBy == actor && p.IdempotencyKey == key, ct);
        if (existing is null) return null;
        if (existing.RequestHash != hash)
            throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different payment.");
        return new RecordedCreditPayment(ToDto(existing), true);
    }

    private AuditLog Audit(string action, CreditPayment p, object? before, object after, DateTime now) =>
        new(Guid.NewGuid(), Actor, user.IpAddress, action, "credit_payments", p.Id.ToString(),
            before is null ? null : JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json), user.CorrelationId, now);

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());

    private static CreditPaymentDto ToDto(CreditPayment p) => new(p.Id, p.CompanyId, Wire(p.Method), p.AmountMinor, p.Currency, p.Reference,
        p.DrawerBank, p.ReceivedOn, p.ChequeDate, p.LateEntryReason, p.Allocations, p.Note, Wire(p.Status), p.RecordedBy, p.CreatedAtUtc,
        p.VerifiedBy, p.VerifiedAtUtc, p.ClearedOn, p.RejectedBy, p.RejectedAtUtc, p.DecisionReason, p.BouncedBy, p.BouncedAtUtc);
}
