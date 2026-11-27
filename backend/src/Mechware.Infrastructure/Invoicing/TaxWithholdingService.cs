using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// E6.6b (ADR-0012 §11, §15; threat model IT14). The Idempotency-Key needs no column: the entry's id is derived from (recorder, key), so a replay
/// finds it by primary key and a parallel twin hits that key. Lock order: <c>orders</c> → <c>invoices</c> (FOR NO KEY UPDATE; the caps are read
/// after it, so a credit note committed first is counted) → <c>tax_withholdings</c> → <c>companies</c> (ledger, FOR UPDATE). No counter.
/// </summary>
public sealed partial class TaxWithholdingService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : ITaxWithholdingService
{
    public const string Duplicate = "WITHHOLDING_CERTIFICATE_DUPLICATE";
    public const string NeedsCreditOrder = "WITHHOLDING_NEEDS_CREDIT_ORDER";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RecordedWithholding> RecordAsync(Guid invoiceId, RecordWithholdingRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (actor, now) = (Actor, Now);
        if (idempotencyKey is null || !KeyPattern().IsMatch(idempotencyKey))
            throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is 8 to 100 letters, digits, '_', ':' or '-'.");
        var kind = KindOf(request.Kind) ?? throw new ValidationException("kind", "Use vds or ait.");
        var (number, certificateKey) = TaxWithholding.NormaliseCertificate(request.CertificateNumber);
        var date = request.CertificateDate ?? throw new ValidationException("certificateDate", "Give the date printed on the certificate.");
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"withholding|{actor:N}|{idempotencyKey}")).AsSpan(0, 16));
        bool Same(TaxWithholding w) => w.InvoiceId == invoiceId && w.Kind == kind && w.CertificateKey == certificateKey && w.CertificateDate == date
            && w.AmountMinor == request.AmountMinor;
        if (await ReplayAsync(id, Same, ct) is { } replay) return new(replay, true);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orderId = await db.Invoices.Where(i => i.Id == invoiceId).Select(i => (Guid?)i.OrderId).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Invoice not found.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {orderId} FOR NO KEY UPDATE", ct);
        var invoice = (await db.Invoices.FromSqlInterpolated($"SELECT i.*, i.xmin FROM invoices i WHERE i.id = {invoiceId} FOR NO KEY UPDATE")
            .AsNoTracking().ToListAsync(ct)).Single();
        if (!await db.CreditLedgerEntries.AnyAsync(l => l.OrderId == orderId && l.Kind == CreditLedgerKind.Charge, ct))
            throw new ConflictException(NeedsCreditOrder, "Certificates settle what a credit order owes; this order was not on credit.");
        var withholding = TaxWithholding.Record(id, invoice, kind, number, date, request.AmountMinor, await CapsAsync(invoice, ct), actor, now);
        db.TaxWithholdings.Add(withholding);
        db.AuditLogs.Add(Audit("invoice.withholding_recorded", withholding, new
        {
            invoice.Number,
            withholding.OrderId,
            withholding.CompanyId,
            Kind = Wire(kind),
            withholding.CertificateNumber,
            withholding.CertificateDate,
            withholding.AmountMinor
        }, now));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            if (pg.ConstraintName == "ux_tax_withholdings_certificate")
                throw new ConflictException(Duplicate, "This certificate number is already recorded for this company. Reject the wrong entry first if this one is right.");
            return new(await ReplayAsync(id, Same, ct) ?? throw new InvalidOperationException("Unique violation without a replay.", ex), true);
        }
        return new(await GetAsync(id, ct), false);
    }

    public async Task<PagedResult<TaxWithholdingDto>> ListAsync(string? status, string? kind, Guid? invoiceId, int page, int pageSize, CancellationToken ct = default)
    {
        var wanted = status is null ? TaxWithholdingStatus.Recorded
            : Enum.GetValues<TaxWithholdingStatus>().Where(s => Wire(s) == status).DefaultIfEmpty((TaxWithholdingStatus)(-1)).First();
        if (!Enum.IsDefined(wanted)) throw new ValidationException("status", "Use recorded, verified or rejected.");
        var ofKind = kind is null ? (TaxWithholdingKind?)null : KindOf(kind) ?? throw new ValidationException("kind", "Use vds or ait.");
        (page, pageSize) = (Math.Max(page, 1), Math.Clamp(pageSize, 1, ITaxWithholdingService.MaxPageSize));
        var rows = db.TaxWithholdings.AsNoTracking().Where(w => w.Status == wanted);
        if (ofKind is { } k) rows = rows.Where(w => w.Kind == k);
        if (invoiceId is { } inv) rows = rows.Where(w => w.InvoiceId == inv);
        var total = await rows.CountAsync(ct);
        var items = await Dtos(rows.OrderBy(w => w.CreatedAtUtc).ThenBy(w => w.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return new(items, page, pageSize, total);
    }

    public async Task<TaxWithholdingDto> GetAsync(Guid withholdingId, CancellationToken ct = default) =>
        await Dtos(db.TaxWithholdings.AsNoTracking().Where(w => w.Id == withholdingId)).SingleOrDefaultAsync(ct) ?? throw NotFound();

    public async Task<TaxWithholdingDto> VerifyAsync(Guid withholdingId, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        var ids = await db.TaxWithholdings.AsNoTracking().Where(w => w.Id == withholdingId).Select(w => new { w.OrderId, w.InvoiceId }).SingleOrDefaultAsync(ct)
            ?? throw NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {ids.OrderId} FOR NO KEY UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoices WHERE id = {ids.InvoiceId} FOR NO KEY UPDATE", ct);
        var w = (await db.TaxWithholdings.FromSqlInterpolated($"SELECT w.*, w.xmin FROM tax_withholdings w WHERE w.id = {withholdingId} FOR NO KEY UPDATE")
            .ToListAsync(ct)).Single();
        var replay = w.Status == TaxWithholdingStatus.Verified && w.DecidedBy == actor;   // the same verifier again: no second line
        if (!replay) w.Verify(actor, now);   // four eyes and "still recorded" (IT14)

        var company = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {w.CompanyId} FOR UPDATE").ToListAsync(ct)).Single();
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company.Id && l.OrderId == w.OrderId).ToListAsync(ct);
        var before = new { company.CreditUsedMinor, company.CreditOnAccountMinor };
        var posted = new CreditLedger(company, lines).Post(w.SettlementPosting(), now);   // key withholding:{id}: a replay finds its line
        if (posted.AlreadyApplied != replay) throw new InvalidOperationException("A withholding's ledger line and its status disagree.");
        if (!replay)
        {
            db.CreditLedgerEntries.Add(posted.Entry);
            db.AuditLogs.Add(Audit("invoice.withholding_verified", w, new
            {
                w.OrderId,
                w.CompanyId,
                Kind = Wire(w.Kind),
                w.CertificateNumber,
                w.AmountMinor,
                LedgerEntryId = posted.Entry.Id,
                Before = before,
                company.CreditUsedMinor,
                company.CreditOnAccountMinor
            }, now));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await GetAsync(w.Id, ct);
    }

    public async Task<TaxWithholdingDto> RejectAsync(Guid withholdingId, RejectWithholdingRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = (await db.TaxWithholdings.FromSqlInterpolated($"SELECT w.*, w.xmin FROM tax_withholdings w WHERE w.id = {withholdingId} FOR NO KEY UPDATE")
            .ToListAsync(ct)).SingleOrDefault() ?? throw NotFound();
        w.Reject(actor, request?.Reason, now);
        db.AuditLogs.Add(Audit("invoice.withholding_rejected", w, new { w.OrderId, Kind = Wire(w.Kind), w.CertificateNumber, w.AmountMinor, w.DecisionReason }, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(w.Id, ct);
    }

    public async Task<CompanyWithholdingEntityDto> SetWithholdingEntityAsync(Guid companyId, SetWithholdingEntityRequest request, CancellationToken ct = default)
    {
        var value = request?.WithholdingEntity ?? throw new ValidationException("withholdingEntity", "Say true or false.");
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, ct) ?? throw new KeyNotFoundException("Company not found.");
        if (company.MarkWithholdingEntity(value))
        {
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), Actor, user.IpAddress, "company.withholding_entity_changed", "companies", company.Id.ToString(),
                JsonSerializer.Serialize(new { WithholdingEntity = !value }, Json), JsonSerializer.Serialize(new { WithholdingEntity = value }, Json),
                user.CorrelationId, Now));
            await db.SaveChangesAsync(ct);
        }
        return new(company.Id, company.WithholdingEntity);
    }

    /// <summary>What the invoice still has room for, read under its lock: its credit notes and the live (not rejected) certificates.</summary>
    private async Task<WithholdingCaps> CapsAsync(Invoice invoice, CancellationToken ct)
    {
        var credited = await db.Invoices.Where(c => c.OriginalInvoiceId == invoice.Id && c.Kind == InvoiceKind.CreditNote)
            .GroupBy(c => 1).Select(g => new { Net = g.Sum(c => c.NetMinor), Vat = g.Sum(c => c.VatMinor) }).SingleOrDefaultAsync(ct);
        var live = db.TaxWithholdings.Where(w => w.InvoiceId == invoice.Id && w.Status != TaxWithholdingStatus.Rejected);
        return new WithholdingCaps(invoice.NetMinor, invoice.VatMinor, credited?.Net ?? 0, credited?.Vat ?? 0,
            await live.Where(w => w.Kind == TaxWithholdingKind.Vds).SumAsync(w => w.AmountMinor, ct),
            await live.Where(w => w.Kind == TaxWithholdingKind.Ait).SumAsync(w => w.AmountMinor, ct));
    }

    private async Task<TaxWithholdingDto?> ReplayAsync(Guid id, Func<TaxWithholding, bool> same, CancellationToken ct)
    {
        var first = await db.TaxWithholdings.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct);
        if (first is null) return null;
        return same(first) ? await GetAsync(id, ct)
            : throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different certificate.");
    }

    private IQueryable<TaxWithholdingDto> Dtos(IQueryable<TaxWithholding> rows) =>
        from w in rows
        join i in db.Invoices on w.InvoiceId equals i.Id
        join c in db.Companies on w.CompanyId equals c.Id
        select new TaxWithholdingDto(w.Id, w.Kind == TaxWithholdingKind.Vds ? "vds" : "ait", w.InvoiceId, i.Number, w.OrderId, w.CompanyId, c.LegalName,
            w.CertificateNumber, w.CertificateDate, w.AmountMinor,
            w.Status == TaxWithholdingStatus.Recorded ? "recorded" : w.Status == TaxWithholdingStatus.Verified ? "verified" : "rejected",
            w.RecordedBy, w.CreatedAtUtc, w.DecidedBy, w.DecidedAtUtc, w.DecisionReason);

    private static TaxWithholdingKind? KindOf(string? wire) => wire switch { "vds" => TaxWithholdingKind.Vds, "ait" => TaxWithholdingKind.Ait, _ => null };

    private AuditLog Audit(string action, TaxWithholding w, object after, DateTime at) =>
        new(Guid.NewGuid(), Actor, user.IpAddress, action, "tax_withholdings", w.Id.ToString(), null, JsonSerializer.Serialize(after, Json), user.CorrelationId, at);

    private static KeyNotFoundException NotFound() => new("Withholding certificate not found.");

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static string Wire<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z0-9_:-]{8,100}$")]
    private static partial Regex KeyPattern();
}
