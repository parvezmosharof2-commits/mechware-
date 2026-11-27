using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Catalog;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// E6.5b (ADR-0012 §9.3, §10, §15; threat model IT3, IT4, IT9, IT15). The request plans against what is left without a lock (the approval plans
/// again); the approval is ONE transaction in the global lock order: <c>orders</c> → the original <c>invoices</c> row (both FOR NO KEY UPDATE: credit
/// lines and ledger lines reference them by foreign key) → the request (FOR UPDATE) → plan again, 409 when it differs → <see cref="ICreditLedger.ReduceChargeAsync"/>
/// (<c>companies</c>) → the counter (LAST) → the 6.7, the request's move, audit and outbox → COMMIT. A paid order that is not on credit records
/// <c>refund_due_minor</c>; no gateway money moves here. Nothing slow runs inside (the PDF renders from the outbox). F2.3b: a request made for a
/// return (source key <c>return:{id:N}</c>) also locks that return after the request and moves it Inspected → CreditNoted before the COMMIT.
/// </summary>
public sealed partial class CreditNoteService(
    MechwareDbContext db, IInvoiceNumberAllocator allocator, ICreditLedger ledger, InvoicingOptions options, ICurrentUserService user, TimeProvider clock)
    : ICreditNoteService
{
    public const string IssuedEvent = "CreditNoteIssuedEvent";
    public const string Changed = "CREDIT_NOTE_CHANGED";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RequestedCreditNote> RequestAsync(Guid invoiceId, RequestCreditNoteRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        ArgumentNullException.ThrowIfNull(request);
        if (idempotencyKey is null || !KeyPattern().IsMatch(idempotencyKey))
            throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is 8 to 100 letters, digits, '_', ':' or '-'.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { invoiceId, request }, Json))));
        if (await ReplayAsync(actor, idempotencyKey, hash, ct) is { } replay) return new(replay, true);

        var original = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException("Invoice not found.");
        var reason = Enum.GetValues<CreditNoteReason>().Where(r => Wire(r) == request.Reason).DefaultIfEmpty((CreditNoteReason)(-1)).First();
        var replace = request.Replace == true;
        if (replace && reason != CreditNoteReason.Error)
            throw new ValidationException("replace", "A replacement invoice follows a full credit note with reason error.");
        var credited = await CreditedAsync(original.Id, ct);
        var asked = replace && request.Lines is null or []
            ? EverythingLeft(original, credited)   // E6.5c: no lines = credit all that is left
            : request.Lines?.Select((l, n) => new CreditLineRequest(
                original.Lines.SingleOrDefault(o => o.LineNo == l.LineNo)?.Id ?? throw new ValidationException($"lines[{n}].lineNo", "This is not a line of the invoice."),
                l.Quantity, l.NetMinor)).ToList();
        var plan = CreditNotePlanner.Plan(original, credited, asked, reason, request.ReasonText, now, options.CreditNoteMaxAgeDays);
        if (replace) await EnsureFullyCreditedAsync(original, plan, ct);

        var asking = CreditNoteRequest.Create(Guid.NewGuid(), original, plan, actor, idempotencyKey, hash, null, now, replace);   // no number: only the approval takes one
        db.CreditNoteRequests.Add(asking);
        db.AuditLogs.Add(Audit("invoice.credit_requested", asking.Id, new { original.Number, asking.OrderId, Reason = Wire(plan.Reason), asking.TotalMinor }, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();   // the same key in parallel: the first one won
            return new(await ReplayAsync(actor, idempotencyKey, hash, ct) ?? throw new InvalidOperationException("Unique violation without a replay.", ex), true);
        }
        return new((await DtosAsync([asking], ct))[0], false);
    }

    public async Task<CreditNoteRequestDto> RequestForReturnAsync(Guid returnId, Guid invoiceId, IReadOnlyList<ReturnCreditLine> lines,
        CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        ArgumentNullException.ThrowIfNull(lines);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("RequestForReturnAsync runs inside the caller's transaction (the return's lock), never its own.");
        var source = $"return:{returnId:N}";
        // The return is the key (ADR-0012 §10): asked again, the open or approved request answers; the caller's return lock queues a parallel
        // ask behind it. F2.3c: a rejected or withdrawn one no longer counts (partial unique index), so the return may ask again.
        if (await db.CreditNoteRequests.AsNoTracking()
                .SingleOrDefaultAsync(r => r.SourceKey == source && (r.Status == CreditNoteRequestStatus.Requested || r.Status == CreditNoteRequestStatus.Approved), ct)
            is { } first)
            return (await DtosAsync([first], ct))[0];
        var earlier = await db.CreditNoteRequests.CountAsync(r => r.SourceKey == source, ct);
        var key = earlier == 0 ? source : $"{source}:{earlier + 1}";   // (requested_by, idempotency_key) stays unique across the asks

        var original = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException("Invoice not found.");
        var asked = lines.Where(l => l.Quantity > 0).Select((l, n) => new CreditLineRequest(
            original.Lines.SingleOrDefault(o => o.Kind == InvoiceLineKind.Goods && o.OrderItemId == l.OrderItemId)?.Id
                ?? throw new ValidationException($"lines[{n}].orderItemId", "This order line is not on the invoice."), l.Quantity, null)).ToList();
        var plan = CreditNotePlanner.Plan(original, await CreditedAsync(original.Id, ct), asked, CreditNoteReason.GoodsReturned,
            $"Goods returned and accepted at inspection (return {returnId:N})", now, options.CreditNoteMaxAgeDays);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { invoiceId, source, lines }, Json))));
        var asking = CreditNoteRequest.Create(Guid.NewGuid(), original, plan, actor, key, hash, source, now);
        db.CreditNoteRequests.Add(asking);
        db.AuditLogs.Add(Audit("invoice.credit_requested", asking.Id,
            new { original.Number, asking.OrderId, Reason = Wire(plan.Reason), asking.TotalMinor, ReturnId = returnId }, now));
        await db.SaveChangesAsync(ct);   // in the caller's transaction: its COMMIT decides
        return (await DtosAsync([asking], ct))[0];
    }

    public async Task<PagedResult<CreditNoteRequestDto>> ListAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var wanted = status is null ? CreditNoteRequestStatus.Requested
            : Enum.GetValues<CreditNoteRequestStatus>().Where(s => Wire(s) == status).DefaultIfEmpty((CreditNoteRequestStatus)(-1)).First();
        if (!Enum.IsDefined(wanted)) throw new ValidationException("status", "Use requested, approved, rejected or withdrawn.");
        (page, pageSize) = (Math.Max(page, 1), Math.Clamp(pageSize, 1, ICreditNoteService.MaxPageSize));
        var rows = db.CreditNoteRequests.AsNoTracking().Where(r => r.Status == wanted);
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(await DtosAsync(items, ct), page, pageSize, total);
    }

    public async Task<CreditNoteRequestDto> GetAsync(Guid requestId, CancellationToken ct = default) =>
        (await DtosAsync([await db.CreditNoteRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, ct) ?? throw NotFound()], ct))[0];

    public async Task<ApprovedCreditNote> ApproveAsync(Guid requestId, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        db.ChangeTracker.Clear();   // ReduceChargeAsync must read the company fresh under its own lock (B3.5 watch item)
        var ids = await db.CreditNoteRequests.Where(r => r.Id == requestId).Select(r => new { r.OrderId, r.OriginalInvoiceId }).SingleOrDefaultAsync(ct)
            ?? throw NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {ids.OrderId} FOR NO KEY UPDATE", ct);
        // Parallel approvals of one invoice queue here, so each plans with the credit lines the previous one committed (IT3).
        var original = (await db.Invoices.FromSqlInterpolated($"SELECT i.*, i.xmin FROM invoices i WHERE i.id = {ids.OriginalInvoiceId} FOR NO KEY UPDATE")
            .AsNoTracking().Include(i => i.Lines).ToListAsync(ct)).Single();
        var request = (await db.CreditNoteRequests.FromSqlInterpolated($"SELECT r.*, r.xmin FROM credit_note_requests r WHERE r.id = {requestId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        request.EnsureDecidableBy(actor);   // four eyes before anything is counted or numbered (IT4)
        // F2.3b: the return of a return's credit note, locked after the request (payments → orders → invoices → … → return_requests).
        var returned = request.SourceKey is { } key && key.StartsWith("return:", StringComparison.Ordinal)
            ? (await db.ReturnRequests.FromSqlInterpolated(
                    $"SELECT r.*, r.xmin FROM return_requests r WHERE r.id = {Guid.ParseExact(key["return:".Length..], "N")} FOR NO KEY UPDATE")
                .ToListAsync(ct)).Single()
            : null;

        var plan = CreditNotePlanner.Plan(original, await CreditedAsync(original.Id, ct),
            [.. request.Lines.Select(l => new CreditLineRequest(l.OriginalLineId, l.Quantity, l.NetMinor))], request.Reason, request.ReasonText, now,
            options.CreditNoteMaxAgeDays);
        if (!plan.Lines.Select(l => new CreditNoteRequestLine(l.OriginalLineId, l.Quantity, l.NetMinor, l.VatMinor)).SequenceEqual(request.Lines))
            throw new ConflictException(Changed, "Another credit note of this invoice was issued since this request; reject it and ask again.");

        if (request.IssuesReplacement) await EnsureFullyCreditedAsync(original, plan, ct);   // again under the lock (§9.4)

        var id = Guid.NewGuid();
        // A credit order owes less (or its paid part goes on account) in THIS transaction; null = not on credit: the money is a refund due.
        var money = await ledger.ReduceChargeAsync(new CreditReduction(original.OrderId, CreditReductionKind.CreditNote, plan.TotalMinor,
            $"creditnote:{id:N}", id, actor, plan.ReasonText), ct);
        var refundDue = money is null ? plan.TotalMinor : 0;
        // E6.5c: the buyer is read again and the replacement's key rows share-locked BEFORE any counter (counters are the LAST locks).
        var buyer = request.IssuesReplacement ? await ReplacementBuyerAsync(original, ct) : null;
        var taken = await allocator.TakeAsync(original.SellerId,
            original.Mode == InvoicingMode.Live ? InvoiceSeries.CreditNote : InvoiceSeries.SpecimenCreditNote, ct);   // the LAST lock: only inserts follow
        var note = Invoice.CreditNote(id, original, plan, taken.Series, taken.FiscalYear, taken.Serial, taken.IssuedAtUtc, request.RequestedBy, actor,
            refundDue, options.ConsultantChecked);
        db.Invoices.Add(note);
        request.Approve(actor, id, now);
        var replacement = buyer is null ? null : await ReplaceAsync(original, buyer, actor, ct);   // E6.5c: its counter after the 6.7's
        if (returned is not null)
        {
            returned.MarkCreditNoted(actor, now);   // F2.3b: the return moves with its 6.7, in THIS transaction (four eyes again in the domain)
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, "return.credit_noted", "return_requests", returned.Id.ToString(),
                JsonSerializer.Serialize(new { Status = "Inspected" }, Json),
                JsonSerializer.Serialize(new { Status = returned.Status.ToString(), CreditNoteId = id, RequestId = request.Id, returned.OrderId }, Json),
                user.CorrelationId, now));
        }
        var (released, onAccount) = (money?.ReceivableReleasedMinor ?? 0, money?.MovedOnAccountMinor ?? 0);
        db.AuditLogs.Add(Audit("invoice.credit_approved", request.Id, new
        {
            note.Number,
            CreditNoteId = id,
            OriginalNumber = original.Number,
            note.OrderId,
            note.TotalMinor,
            RefundDueMinor = refundDue,
            CreditReleasedMinor = released,
            MovedOnAccountMinor = onAccount
        }, taken.IssuedAtUtc));
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = IssuedEvent,
            AggregateType = nameof(Invoice),
            AggregateId = id,
            OccurredAtUtc = taken.IssuedAtUtc,
            Payload = JsonSerializer.Serialize(new { InvoiceId = id, OriginalInvoiceId = original.Id, note.OrderId, note.Number, RequestId = request.Id }, Json)
        });
        await db.SaveChangesAsync(ct);   // the deferred caps, counter, line and replacement checks run at COMMIT
        await tx.CommitAsync(ct);
        return new((await DtosAsync([request], ct))[0], id, note.Number, note.TotalMinor, refundDue, released, onAccount, replacement?.Id, replacement?.Number);
    }

    public Task<CreditNoteRequestDto> RejectAsync(Guid requestId, RejectCreditNoteRequest request, CancellationToken ct = default) =>
        DecideAsync(requestId, "invoice.credit_rejected", r => r.Reject(Actor, request?.Reason, Now), ct);

    public Task<CreditNoteRequestDto> WithdrawAsync(Guid requestId, CancellationToken ct = default) =>
        DecideAsync(requestId, "invoice.credit_withdrawn", r => r.Withdraw(Actor, Now), ct);

    private async Task<CreditNoteRequestDto> DecideAsync(Guid requestId, string action, Action<CreditNoteRequest> decide, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = (await db.CreditNoteRequests.FromSqlInterpolated($"SELECT r.*, r.xmin FROM credit_note_requests r WHERE r.id = {requestId} FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault() ?? throw NotFound();
        decide(request);   // nothing is issued and nothing moves
        db.AuditLogs.Add(Audit(action, request.Id, new { request.OriginalInvoiceId, request.OrderId, request.TotalMinor, request.DecisionReason }, Now));
        if (request.SourceKey is { } key && key.StartsWith("return:", StringComparison.Ordinal))
        {
            // F2.3c: the return goes back to Inspected without a settlement (lock order credit_note_requests -> return_requests, as the approval).
            var returned = (await db.ReturnRequests.FromSqlInterpolated(
                    $"SELECT r.*, r.xmin FROM return_requests r WHERE r.id = {Guid.ParseExact(key["return:".Length..], "N")} FOR NO KEY UPDATE")
                .ToListAsync(ct)).Single();
            returned.ReleaseMoneyBack(ReturnSettlement.CreditNote, Now);
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), Actor, user.IpAddress, ReleasedAction, "return_requests", returned.Id.ToString(),
                JsonSerializer.Serialize(new { Status = "Inspected", Settlement = nameof(ReturnSettlement.CreditNote), CreditNoteRequestId = request.Id }, Json),
                JsonSerializer.Serialize(new { Status = "Inspected", Settlement = (string?)null, Via = action, request.DecisionReason, returned.OrderId }, Json),
                user.CorrelationId, Now));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync([request], ct))[0];
    }

    /// <summary>F2.3c audit action: an inspected return's asked money did not go out, so it waits at Inspected to be asked again.</summary>
    public const string ReleasedAction = "return.money_back_released";

    public const string NeedsFullCredit = "INVOICE_REPLACEMENT_NEEDS_FULL_CREDIT";

    /// <summary>E6.5c: every line with value left, its units and value left (the planner then takes exactly the VAT left).</summary>
    private static List<CreditLineRequest> EverythingLeft(Invoice original, Dictionary<Guid, CreditedSoFar> credited) =>
        [.. original.Lines.Select(l => (Line: l, Done: credited.GetValueOrDefault(l.Id) ?? new CreditedSoFar(0, 0, 0)))
            .Where(x => x.Line.NetMinor - x.Done.NetMinor > 0)
            .Select(x => new CreditLineRequest(x.Line.Id, x.Line.Quantity - x.Done.Quantity, x.Line.NetMinor - x.Done.NetMinor))];

    /// <summary>E6.5c (ADR-0012 §9.4): a replacement needs the old 6.3 fully credited: its earlier credit notes plus this one sum to its total.</summary>
    private async Task EnsureFullyCreditedAsync(Invoice original, CreditNotePlan plan, CancellationToken ct)
    {
        var before = await db.Invoices.AsNoTracking().Where(c => c.OriginalInvoiceId == original.Id && c.Kind == InvoiceKind.CreditNote)
            .SumAsync(c => (long?)c.TotalMinor, ct) ?? 0;
        if (before + plan.TotalMinor != original.TotalMinor)
            throw new ConflictException(NeedsFullCredit, "A replacement invoice needs the old one fully credited; credit every line in full.");
    }

    /// <summary>
    /// E6.5c, ADR-0012 §9.4: the replacement 6.3 of <paramref name="previous"/>, issued by the credit note's approver in its transaction: the buyer
    /// read again (company + default delivery site, or the person's name; Live refuses an invalid BIN), the same lines, the next replacement
    /// number, a new tax-invoice number (counter after the credit-note counter: both LAST), audit <c>invoice.replaced</c> and the PDF event.
    /// </summary>
    private async Task<InvoiceBuyerSnapshot> ReplacementBuyerAsync(Invoice previous, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == previous.OrderId, ct);
        Company? company = null;
        CompanySite? site = null;
        string? name = null;
        if (order.CompanyId is { } companyId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {companyId} FOR KEY SHARE", ct);
            company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
            site = await db.CompanySites.AsNoTracking().Where(s => s.CompanyId == companyId && s.IsDefaultDelivery).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        }
        else
        {
            var person = await db.Users.AsNoTracking().Where(u => u.Id == order.UserId).Select(u => new { u.FullName, u.Email }).FirstAsync(ct);
            name = string.IsNullOrWhiteSpace(person.FullName) ? person.Email : person.FullName;
        }
        var buyer = InvoiceSnapshotBuilder.ReplacementBuyer(order, company, site, name, previous.Mode);   // 409 before any counter
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM shipments sh JOIN sub_orders so ON so.id = {previous.SubOrderId} WHERE sh.id = {previous.ShipmentId} FOR KEY SHARE", ct);
        return buyer;
    }

    private async Task<Invoice> ReplaceAsync(Invoice previous, InvoiceBuyerSnapshot buyer, Guid actor, CancellationToken ct)
    {
        var taken = await allocator.TakeAsync(previous.SellerId,
            previous.Mode == InvoicingMode.Live ? InvoiceSeries.TaxInvoice : InvoiceSeries.SpecimenTaxInvoice, ct);
        var replacement = Invoice.Replacement(Guid.NewGuid(), previous, buyer, taken.Series, taken.FiscalYear, taken.Serial, taken.IssuedAtUtc, actor,
            options.ConsultantChecked);
        db.Invoices.Add(replacement);
        db.Entry(replacement).Property("ReplacesInvoiceId").CurrentValue = previous.Id;
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, "invoice.replaced", "invoices", replacement.Id.ToString(), null,
            JsonSerializer.Serialize(new
            {
                replacement.Number,
                ReplacesNumber = previous.Number,
                ReplacesInvoiceId = previous.Id,
                replacement.ReplacementNo,
                replacement.OrderId,
                replacement.TotalMinor,
                replacement.ContentHash
            }, Json), user.CorrelationId, taken.IssuedAtUtc));
        var eventId = Guid.NewGuid();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            EventType = InvoiceIssuer.IssuedEvent,
            AggregateType = nameof(Invoice),
            AggregateId = replacement.Id,
            OccurredAtUtc = taken.IssuedAtUtc,
            Payload = JsonSerializer.Serialize(new { EventId = eventId, InvoiceId = replacement.Id, replacement.OrderId, replacement.Number, Mode = replacement.Mode.ToString() }, Json)
        });
        return replacement;
    }

    /// <summary>Σ quantity, value and VAT of the issued credit lines per original line (committed rows; under the approval's locks nothing else adds any).</summary>
    private async Task<Dictionary<Guid, CreditedSoFar>> CreditedAsync(Guid originalId, CancellationToken ct) =>
        (await db.InvoiceLines.AsNoTracking()
            .Where(l => l.OriginalLineId != null && db.Invoices.Any(c => c.Id == l.InvoiceId && c.OriginalInvoiceId == originalId && c.Kind == InvoiceKind.CreditNote))
            .GroupBy(l => l.OriginalLineId!.Value)
            .Select(g => new { g.Key, Quantity = g.Sum(l => l.Quantity), Net = g.Sum(l => l.NetMinor), Vat = g.Sum(l => l.VatMinor) }).ToListAsync(ct))
        .ToDictionary(x => x.Key, x => new CreditedSoFar(x.Quantity, x.Net, x.Vat));

    private async Task<CreditNoteRequestDto?> ReplayAsync(Guid actor, string key, string hash, CancellationToken ct)
    {
        var first = await db.CreditNoteRequests.AsNoTracking().SingleOrDefaultAsync(r => r.RequestedBy == actor && r.IdempotencyKey == key, ct);
        if (first is null) return null;
        return first.RequestHash == hash
            ? (await DtosAsync([first], ct))[0]
            : throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different credit-note request.");
    }

    private async Task<List<CreditNoteRequestDto>> DtosAsync(IReadOnlyList<CreditNoteRequest> requests, CancellationToken ct)
    {
        var ids = requests.Select(r => r.OriginalInvoiceId).Distinct().ToList();
        var originals = await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var issuedIds = requests.Where(r => r.IssuedInvoiceId is not null).Select(r => r.IssuedInvoiceId!.Value).ToList();
        var issued = await db.Invoices.AsNoTracking().Where(i => issuedIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        return [.. requests.Select(r =>
        {
            var o = originals[r.OriginalInvoiceId];
            return new CreditNoteRequestDto(r.Id, o.Id, o.Number, r.OrderId, o.OrderNumber, Wire(r.Status), Wire(r.Reason), r.ReasonText,
                [.. r.Lines.Select(l => (Asked: l, Line: o.Lines.Single(x => x.Id == l.OriginalLineId))).Select(x => new CreditNoteRequestLineDto(x.Line.LineNo,
                    Wire(x.Line.Kind), x.Line.Sku, x.Line.DescriptionEn, x.Asked.Quantity, x.Asked.NetMinor, x.Asked.VatMinor, x.Asked.NetMinor + x.Asked.VatMinor))],
                r.TotalMinor, r.RequestedBy, r.CreatedAtUtc, r.DecidedBy, r.DecidedAtUtc, r.DecisionReason, r.IssuedInvoiceId,
                r.IssuedInvoiceId is { } i ? issued.GetValueOrDefault(i) : null, r.IssuesReplacement);
        })];
    }

    private AuditLog Audit(string action, Guid requestId, object after, DateTime at) =>
        new(Guid.NewGuid(), Actor, user.IpAddress, action, "credit_note_requests", requestId.ToString(), null, JsonSerializer.Serialize(after, Json),
            user.CorrelationId, at);

    private static KeyNotFoundException NotFound() => new("Credit-note request not found.");

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());

    [GeneratedRegex("^[A-Za-z0-9_:-]{8,100}$")]
    private static partial Regex KeyPattern();
}
