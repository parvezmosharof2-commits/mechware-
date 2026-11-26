using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.5b, threat model §১৪ IT3, IT4, IT9, IT15 (ADR-0012 §9.3, §10, §15), against a real database with every invoice, credit-cap and ledger guard
/// installed. Original (company, net basis): line 1 CBL-10 25 × net 308 625 VAT 23 147 (7.5 %); line 2 PLC-1200 2 × net 9 000 000 VAT 1 350 000;
/// line 3 delivery 12 000. A credit order is charged its grand total (= the invoice total, one seller).
/// </summary>
public class CreditNoteServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "cn-" + Guid.NewGuid().ToString("N");

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.65";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-e65b";
        public bool HasPermission(string permission) => true;
    }

    private sealed record World(Guid Seller, Guid Company, Invoice Original, InvoicingOptions Options)
    {
        public int Cable => Original.Lines.Single(l => l.Sku == "CBL-10").LineNo;
        public int Plc => Original.Lines.Single(l => l.Sku == "PLC-1200").LineNo;
    }

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private async Task<T> As<T>(Guid actor, World w, Func<CreditNoteService, Task<T>> act)
    {
        await using var db = fixture.CreateContext();
        var staff = new Staff(actor);
        return await act(new CreditNoteService(db, Allocator(db), new CreditLedgerService(db, staff, TimeProvider.System), w.Options, staff, TimeProvider.System));
    }

    private async Task<Guid> UserAsync()
    {
        await using var db = fixture.CreateContext();
        return await InvoiceTestIssues.UserAsync(db);
    }

    /// <summary>A seller and an active company (with Net-30 credit when <paramref name="credit"/>), then one invoiced order.</summary>
    private async Task<World> WorldAsync(bool credit = false)
    {
        await using var db = fixture.CreateContext();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        var company = await InvoiceTestIssues.ActiveCompanyAsync(db);
        if (credit)
        {
            company.SetCredit(CreditTerms.Net30, Money.FromMinorUnits(1_000_000_000));
            await db.SaveChangesAsync();
        }
        var w = new World(seller, company.Id, null!, InvoiceTestIssues.Options(seller));
        return w with { Original = await InvoicedOrderAsync(w, credit) };
    }

    /// <summary>Another shipped order of the world's company with its tax invoice (counter last); a credit order is charged its grand total.</summary>
    private async Task<Invoice> InvoicedOrderAsync(World w, bool charge)
    {
        await using var db = fixture.CreateContext();
        var company = await db.Companies.SingleAsync(c => c.Id == w.Company);
        await using var tx = await db.Database.BeginTransactionAsync();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, w.Seller, company);
        if (charge)
            db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order.Id, order.GrandTotalMinor, DateTime.UtcNow, DateTime.UtcNow).Entry);
        var invoice = InvoiceTestIssues.Build(order, shipment, w.Seller, await Allocator(db).TakeAsync(w.Seller, InvoiceSeries.TaxInvoice), company);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return invoice;
    }

    private static RequestCreditNoteRequest Ask(int lineNo, int quantity, long? net = null, string text = "Goods came back damaged in transit") =>
        new("goods_returned", text, [new CreditNoteLineInput(lineNo, quantity, net)]);

    private Task<Guid> RequestAsync(Guid asker, World w, RequestCreditNoteRequest body) =>
        As(asker, w, async s => (await s.RequestAsync(w.Original.Id, body, Key())).Request.Id);

    private Task<ApprovedCreditNote> ApproveAsync(Guid approver, World w, Guid request) => As(approver, w, s => s.ApproveAsync(request));

    /// <summary>The next credit-note serial of the seller, or null when no credit-note number was ever taken.</summary>
    private async Task<long?> CounterAsync(Guid seller)
    {
        await using var db = fixture.CreateContext();
        return await db.InvoiceNumberCounters.AsNoTracking().Where(c => c.SellerId == seller && c.Series == InvoiceSeries.CreditNote)
            .Select(c => (long?)c.NextSerial).SingleOrDefaultAsync();
    }

    private async Task<List<Invoice>> NotesOfAsync(Guid original)
    {
        await using var db = fixture.CreateContext();
        return await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.OriginalInvoiceId == original).ToListAsync();
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    /// <summary>Runs every action at once; each answers null (done) or the 409 code. Anything else (a DB error at COMMIT, a deadlock) fails the test.</summary>
    private static async Task<string?[]> AtOnceAsync(params Func<Task>[] actions)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = actions.Select(a => Task.Run(async () =>
        {
            gate.Wait();
            try
            {
                await a();
                return (string?)null;
            }
            catch (ConflictException e)
            {
                return e.Code;
            }
        })).ToList();
        gate.Set();
        var all = Task.WhenAll(tasks);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        return await all;
    }

    // ---- IT4 -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ARequestHasNoNumber_OnlyTheApprovalTakesOne()
    {
        var w = await WorldAsync();
        var (asker, approver) = (await UserAsync(), await UserAsync());
        var asked = await As(asker, w, s => s.RequestAsync(w.Original.Id, Ask(w.Plc, 1), Key()));
        (asked.Replayed, asked.Request.Status, asked.Request.IssuedNumber, asked.Request.Lines.Single().Quantity).Should().Be((false, "requested", null, 1));
        (await CounterAsync(w.Seller)).Should().BeNull("a request never touches the credit-note counter");
        (await NotesOfAsync(w.Original.Id)).Should().BeEmpty();

        var approved = await ApproveAsync(approver, w, asked.Request.Id);
        approved.Number.Should().EndWith("-000001").And.Contain("-CN-").And.StartWith(InvoiceTestIssues.Prefix(w.Seller) + "-");
        (approved.Request.Status, approved.Request.IssuedInvoiceId, approved.Request.IssuedNumber, approved.Request.DecidedBy)
            .Should().Be(("approved", approved.CreditNoteId, approved.Number, approver));
        (await CounterAsync(w.Seller)).Should().Be(2);
        var late = await UserAsync();
        (await CodeOf(() => ApproveAsync(late, w, asked.Request.Id))).Should().Be(CreditNoteRequest.NotRequested, "approved once");
        (await CounterAsync(w.Seller)).Should().Be(2);
    }

    [PostgresFact]
    public async Task ApproveCreditNote_ByTheRequester_IsRefused()
    {
        var w = await WorldAsync();
        var (asker, other) = (await UserAsync(), await UserAsync());
        var request = await RequestAsync(asker, w, Ask(w.Plc, 1));

        (await CodeOf(() => ApproveAsync(asker, w, request))).Should().Be(CreditNoteRequest.FourEyes);
        (await CodeOf(() => As(asker, w, s => s.RejectAsync(request, new("Asked by mistake, sorry"))))).Should().Be(CreditNoteRequest.FourEyes);
        (await CodeOf(() => As(other, w, s => s.WithdrawAsync(request)))).Should().Be(CreditNoteRequest.NotYours);
        (await CounterAsync(w.Seller)).Should().BeNull("a refused approval takes no number");
        (await NotesOfAsync(w.Original.Id)).Should().BeEmpty();

        (await As(asker, w, s => s.WithdrawAsync(request))).Status.Should().Be("withdrawn");
        (await CodeOf(() => ApproveAsync(other, w, request))).Should().Be(CreditNoteRequest.NotRequested);
        var second = await RequestAsync(asker, w, Ask(w.Plc, 1));
        await Assert.ThrowsAsync<ValidationException>(() => As(other, w, s => s.RejectAsync(second, new("no"))));
        var rejected = await As(other, w, s => s.RejectAsync(second, new("  Goods were never returned  ")));
        (rejected.Status, rejected.DecidedBy, rejected.DecisionReason).Should().Be(("rejected", other, "Goods were never returned"));
        (await CounterAsync(w.Seller)).Should().BeNull();

        await using var db = fixture.CreateContext();
        (await db.AuditLogs.Where(a => a.EntityId == request.ToString() || a.EntityId == second.ToString()).Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(["invoice.credit_requested", "invoice.credit_withdrawn", "invoice.credit_requested", "invoice.credit_rejected"]);
    }

    [PostgresFact]
    public async Task IssuedByEqualsRequestedBy_FailsAtDbLevel()
    {
        var w = await WorldAsync();
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var taken = await Allocator(db).TakeAsync(w.Seller, InvoiceSeries.CreditNote);
            // The SQL helper copies the original's issuer as the credit note's issuer; naming the same person as requester breaks the CHECK.
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => InvoiceTestIssues.CreditNoteAsync(db, w.Original.Id, taken, w.Original.IssuedBy));
            var pg = ex as PostgresException ?? (PostgresException)ex.InnerException!;
            (pg.SqlState, pg.ConstraintName).Should().Be((PostgresErrorCodes.CheckViolation, "ck_invoices_kind_shape"));
        }

        var asker = await UserAsync();
        var request = await RequestAsync(asker, w, Ask(w.Plc, 1));
        await using (var db = fixture.CreateContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE credit_note_requests SET status = 'Approved', decided_by = requested_by, decided_at = now(), issued_invoice_id = {w.Original.Id}
                 WHERE id = {request}
                """));
            ex.SqlState.Should().BeOneOf(PostgresErrorCodes.IntegrityConstraintViolation, PostgresErrorCodes.CheckViolation);
        }
    }

    // ---- IT3 -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ACreditNoteReferencesItsInvoice()
    {
        var w = await WorldAsync();   // not a credit order: the money is a refund due
        var (asker, approver) = (await UserAsync(), await UserAsync());
        var request = await RequestAsync(asker, w, new RequestCreditNoteRequest("price_reduction", "Agreed\tdiscount after\nlate delivery",
            [new CreditNoteLineInput(w.Plc, 0, 500_000), new CreditNoteLineInput(w.Cable, 25)]));
        var approved = await ApproveAsync(approver, w, request);

        var note = (await NotesOfAsync(w.Original.Id)).Single();
        var o = w.Original;
        (note.Id, note.Kind, note.Mode, note.Series, note.OriginalInvoiceId, note.ShipmentId).Should()
            .Be((approved.CreditNoteId, InvoiceKind.CreditNote, InvoicingMode.Live, InvoiceSeries.CreditNote, o.Id, (Guid?)null));
        (note.OrderId, note.SubOrderId, note.SellerId, note.CompanyId, note.OrderNumber, note.PurchaseOrderNumber, note.QuoteNumber)
            .Should().Be((o.OrderId, o.SubOrderId, o.SellerId, o.CompanyId, o.OrderNumber, o.PurchaseOrderNumber, o.QuoteNumber));
        (note.Seller, note.Buyer, note.Delivery).Should().Be((o.Seller, o.Buyer, o.Delivery), "the snapshot is the original's, never live data");
        (note.Reason, note.ReasonText, note.RequestedBy, note.IssuedBy).Should().Be(("PriceReduction", "Agreed discount after late delivery", asker, approver));
        var cable = o.Lines.Single(l => l.LineNo == w.Cable);
        note.Lines.OrderBy(l => l.LineNo).Select(l => (l.LineNo, l.OriginalLineId, l.OrderItemId, l.Quantity, l.NetMinor, l.VatMinor, l.VatRate)).Should().Equal(
            (1, (Guid?)cable.Id, cable.OrderItemId, 25, cable.NetMinor, cable.VatMinor, cable.VatRate),
            (2, o.Lines.Single(l => l.LineNo == w.Plc).Id, o.Lines.Single(l => l.LineNo == w.Plc).OrderItemId, 0, 500_000L, 75_000L, 0.15m));
        (note.TotalMinor, approved.TotalMinor, note.RefundDueMinor, approved.RefundDueMinor, approved.CreditReleasedMinor)
            .Should().Be((cable.TotalMinor + 575_000, note.TotalMinor, note.TotalMinor, note.TotalMinor, 0L));
        InvoiceContentHash.Matches(note, note.ContentHash).Should().BeTrue("the stored hash is the reloaded content's own, reason included");

        await using var db = fixture.CreateContext();
        (await db.CreditNoteRequests.AsNoTracking().SingleAsync(r => r.Id == request)).IssuedInvoiceId.Should().Be(note.Id);
        (await db.AuditLogs.CountAsync(a => a.Action == "invoice.credit_approved" && a.EntityId == request.ToString() && a.ActorId == approver)).Should().Be(1);
        (await db.OutboxMessages.CountAsync(m => m.EventType == CreditNoteService.IssuedEvent && m.AggregateId == note.Id)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TwoParallelCreditNotes_NeverTogetherExceedTheOriginal()
    {
        for (var round = 0; round < 4; round++)
        {
            // Two requests that fit alone (2 units, then 1 or 2 units of a 2-unit line), approved at once by two people.
            var w = await WorldAsync();
            var (a, b, x, y) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
            var (ra, rb) = (await RequestAsync(a, w, Ask(w.Plc, 2)), await RequestAsync(b, w, Ask(w.Plc, 1 + round % 2)));
            var codes = await AtOnceAsync(() => ApproveAsync(x, w, ra), () => ApproveAsync(y, w, rb));
            codes.Count(c => c is null).Should().Be(1, $"round {round}");
            codes.Single(c => c is not null).Should().Be(CreditNotePlanner.ExceedsOriginal, "the second plans again under the lock and is refused cleanly");
            var notes = await NotesOfAsync(w.Original.Id);
            notes.SelectMany(n => n.Lines).Sum(l => l.Quantity).Should().BeLessThanOrEqualTo(2);
            (await CounterAsync(w.Seller)).Should().Be(2, "exactly one number was used");
        }

        // Two that fit together both pass, and a fully credited line sums to the poisha.
        var both = await WorldAsync();
        var (c1, c2, d1, d2) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
        var (q1, q2) = (await RequestAsync(c1, both, Ask(both.Plc, 1)), await RequestAsync(c2, both, Ask(both.Plc, 1)));
        (await AtOnceAsync(() => ApproveAsync(d1, both, q1), () => ApproveAsync(d2, both, q2))).Should().AllSatisfy(c => c.Should().BeNull());
        var plc = both.Original.Lines.Single(l => l.LineNo == both.Plc);
        var credited = (await NotesOfAsync(both.Original.Id)).SelectMany(n => n.Lines).ToList();
        (credited.Sum(l => l.Quantity), credited.Sum(l => l.NetMinor), credited.Sum(l => l.VatMinor)).Should().Be((plc.Quantity, plc.NetMinor, plc.VatMinor));
    }

    [PostgresFact]
    public async Task AStaleRequest_IsRefusedAtApproval_WhenThePlanChanged()
    {
        // X asks net 308 610 of the cable line: VAT round(23 145.75) = 23 146. Two notes of 7 poisha (VAT 1 each) then leave VAT 23 145,
        // so planning X again caps its VAT: the plan differs from what the approver was shown → 409, nothing issued.
        var w = await WorldAsync();
        var cable = w.Original.Lines.Single(l => l.LineNo == w.Cable);
        (cable.NetMinor, cable.VatMinor, cable.VatRate).Should().Be((308_625L, 23_147L, 0.075m));
        var (asker, approver) = (await UserAsync(), await UserAsync());
        var x = await RequestAsync(asker, w, Ask(w.Cable, 0, 308_610));
        foreach (var y in new[] { await RequestAsync(asker, w, Ask(w.Cable, 0, 7)), await RequestAsync(asker, w, Ask(w.Cable, 0, 7)) })
            await ApproveAsync(approver, w, y);

        (await CodeOf(() => ApproveAsync(approver, w, x))).Should().Be(CreditNoteService.Changed);
        (await CounterAsync(w.Seller)).Should().Be(3, "only the two small notes took numbers");
        await using var db = fixture.CreateContext();
        (await db.CreditNoteRequests.AsNoTracking().SingleAsync(r => r.Id == x)).Status.Should().Be(CreditNoteRequestStatus.Requested);
    }

    [PostgresFact]
    public async Task Approval_WaitsForTheOriginalInvoiceLock_BeforeTheCounter()
    {
        // Deterministic half of §15: a transaction holding the original invoice row (and NOT the order) holds the approval back.
        var w = await WorldAsync();
        var request = await RequestAsync(await UserAsync(), w, Ask(w.Plc, 1));
        var approver = await UserAsync();
        await using var holder = fixture.CreateContext();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoices WHERE id = {w.Original.Id} FOR NO KEY UPDATE");
        var approval = ApproveAsync(approver, w, request);
        await Task.WhenAny(approval, Task.Delay(TimeSpan.FromSeconds(2)));
        approval.IsCompleted.Should().BeFalse("the approval waits for the original invoice's row lock");
        (await CounterAsync(w.Seller)).Should().BeNull("the counter is taken after that lock");
        await tx.RollbackAsync();
        (await approval).Number.Should().EndWith("-000001");
    }

    // ---- IT9 -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task CreditNoteRequestReplay_WithTheSameKey_CreatesOne_AndAnotherBodyIs409()
    {
        var w = await WorldAsync();
        var (asker, other) = (await UserAsync(), await UserAsync());
        var key = Key();
        var answers = new List<RequestedCreditNote>();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => As(asker, w, s => s.RequestAsync(w.Original.Id, Ask(w.Plc, 1), key)))).ToList();
        answers.AddRange(await Task.WhenAll(tasks));
        answers.Select(a => a.Request.Id).Distinct().Should().ContainSingle();
        answers.Count(a => !a.Replayed).Should().Be(1);
        (await As(asker, w, s => s.RequestAsync(w.Original.Id, Ask(w.Plc, 1), key))).Should().Match<RequestedCreditNote>(a => a.Replayed && a.Request.Id == answers[0].Request.Id);

        (await CodeOf(() => As(asker, w, s => s.RequestAsync(w.Original.Id, Ask(w.Plc, 2), key)))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await As(other, w, s => s.RequestAsync(w.Original.Id, Ask(w.Plc, 1), key))).Request.Id.Should().NotBe(answers[0].Request.Id, "a key is per person");
        await Assert.ThrowsAsync<ValidationException>(() => As(asker, w, s => s.RequestAsync(w.Original.Id, Ask(w.Plc, 1), "short")));
        await using var db = fixture.CreateContext();
        (await db.CreditNoteRequests.CountAsync(r => r.OriginalInvoiceId == w.Original.Id)).Should().Be(2);
    }

    // ---- IT3 + IT15: money ---------------------------------------------------------------------------------------------

    /// <summary>What a cancellation does to the ledger (E7.2's call): order row NO KEY UPDATE, reduce everything not yet reduced, commit.</summary>
    private async Task CancelReductionAsync(Guid orderId)
    {
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {orderId} FOR NO KEY UPDATE");
        await new CreditLedgerService(db, new Staff(Guid.NewGuid()), TimeProvider.System).ReduceChargeAsync(
            new CreditReduction(orderId, CreditReductionKind.OrderCancelled, null, $"cancel:{orderId:N}", null, null, "order cancelled by the buyer"));
        await tx.CommitAsync();
    }

    private async Task<List<CreditLedgerEntry>> OrderLinesAsync(Guid orderId)
    {
        await using var db = fixture.CreateContext();
        return await db.CreditLedgerEntries.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync();
    }

    [PostgresFact]
    public async Task CreditNoteAndCancellation_TogetherNeverExceedTheCharge()
    {
        for (var round = 0; round < 4; round++)
        {
            var w = await WorldAsync(credit: true);
            var (asker, approver) = (await UserAsync(), await UserAsync());
            var request = await RequestAsync(asker, w, Ask(w.Plc, 2));
            string?[] codes;
            if (round == 0)
            {
                // Cancellation first: nothing is left to reduce, so the approval is refused and takes no number.
                await CancelReductionAsync(w.Original.OrderId);
                codes = [null, await CodeOf(() => ApproveAsync(approver, w, request))];
            }
            else codes = await AtOnceAsync(() => CancelReductionAsync(w.Original.OrderId), () => ApproveAsync(approver, w, request));

            codes[0].Should().BeNull("the cancellation reduces whatever is left");
            var lines = await OrderLinesAsync(w.Original.OrderId);
            var charge = lines.Single(l => l.Kind == CreditLedgerKind.Charge).ReceivableDeltaMinor;
            lines.Sum(l => l.ReceivableDeltaMinor + l.OnAccountDeltaMinor).Should().Be(0, "together they release exactly the charge, never more");
            charge.Should().Be(w.Original.TotalMinor);
            var notes = await NotesOfAsync(w.Original.Id);
            if (codes[1] is null)
            {
                var note = notes.Single();
                note.RefundDueMinor.Should().Be(0, "a credit order's money moved in the ledger");
                lines.Where(l => l.CreditNoteId == note.Id).Sum(l => l.ReceivableDeltaMinor).Should().Be(-note.TotalMinor, "the ledger names the 6.7 it came from");
            }
            else
            {
                codes[1].Should().Be("CREDIT_REDUCTION_EXCEEDS_CHARGE", $"round {round}");
                notes.Should().BeEmpty("the 6.7 and its money commit together or not at all");
                (await CounterAsync(w.Seller)).Should().BeNull();
                lines.Should().NotContain(l => l.CreditNoteId != null);
            }
        }
    }

    [PostgresFact]
    public async Task CreditNoteApprovalAndPaymentVerify_InParallel_NeverDeadlock_AndTheLedgerAddsUp()
    {
        // 4 approvals (order → invoice → company → counter) and 4 verifications (payment → company → order KEY SHARE) of one company at once.
        var w = await WorldAsync(credit: true);
        var originals = new List<Invoice> { w.Original };
        for (var i = 0; i < 3; i++) originals.Add(await InvoicedOrderAsync(w, charge: true));
        var (asker, approver, recorder, verifier) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
        var requests = new List<Guid>();
        foreach (var o in originals) requests.Add(await RequestAsync(asker, w with { Original = o }, Ask(w.Plc, 1)));
        var today = CreditCalendar.DhakaDate(DateTime.UtcNow);
        var payments = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            await using var db = fixture.CreateContext();
            var service = new CreditPaymentService(db, new Staff(recorder), new CreditOptions(), TimeProvider.System);
            payments.Add((await service.RecordAsync(w.Company, new RecordCreditPaymentRequest("cash", 2_000_000, today), Key())).Payment.Id);
        }

        var work = requests.Select(r => (Func<Task>)(() => ApproveAsync(approver, w, r)))
            .Concat(payments.Select(p => (Func<Task>)(async () =>
            {
                await using var db = fixture.CreateContext();
                await new CreditPaymentService(db, new Staff(verifier), new CreditOptions(), TimeProvider.System).VerifyAsync(p, new VerifyCreditPaymentRequest(today));
            })));
        (await AtOnceAsync([.. work])).Should().AllSatisfy(c => c.Should().BeNull());

        await using var check = fixture.CreateContext();
        var company = await check.Companies.AsNoTracking().SingleAsync(c => c.Id == w.Company);
        var all = await check.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == w.Company).OrderBy(l => l.Seq).ToListAsync();
        all.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, all.Count).Select(i => (long)i));
        (company.CreditUsedMinor, company.CreditOnAccountMinor).Should().Be((all.Sum(l => l.ReceivableDeltaMinor), all.Sum(l => l.OnAccountDeltaMinor)));
        var ledger = new CreditLedger(company, all);
        foreach (var o in originals)
        {
            var b = ledger.BalanceOf(o.OrderId);
            (b.ReceivableMinor >= 0 && b.PaidMinor >= 0 && b.ReducedMinor <= b.ChargeMinor).Should().BeTrue($"order {o.OrderNumber}: {b}");
        }
        all.Count(l => l.CreditNoteId != null).Should().BeGreaterThanOrEqualTo(4);
        (await check.Invoices.CountAsync(i => i.Kind == InvoiceKind.CreditNote && i.SellerId == w.Seller)).Should().Be(4);
    }
}
