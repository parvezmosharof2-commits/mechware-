using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using Spec = Mechware.IntegrationTests.Invoicing.InvoiceTestIssues.CreditSpec;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.6b, threat model IT14, ADR-0012 §11, §15: the withholding service against a real database (caps at COMMIT, guard, ledger guards installed).
/// Invoice: PLC-1200 2 × net 9 000 000 VAT 1 350 000; CBL-10 25 × net 308 625 VAT 23 147; delivery 12 000. A credit order is charged its grand total.
/// </summary>
public class TaxWithholdingServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "wh-" + Guid.NewGuid().ToString("N");

    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.66";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-e66b";
        public bool HasPermission(string permission) => true;
    }

    private sealed record World(Guid Seller, Guid Company, Invoice Invoice, Guid Recorder, Guid Checker);

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private async Task<T> As<T>(Guid actor, Func<TaxWithholdingService, Task<T>> act)
    {
        await using var db = fixture.CreateContext();
        return await act(new TaxWithholdingService(db, new Staff(actor), TimeProvider.System));
    }

    private async Task<World> WorldAsync(bool credit = true)
    {
        await using var db = fixture.CreateContext();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        var company = await InvoiceTestIssues.ActiveCompanyAsync(db);
        if (credit) company.SetCredit(CreditTerms.Net30, Money.FromMinorUnits(1_000_000_000));
        await db.SaveChangesAsync();
        var invoice = await InvoicedOrderAsync(seller, company.Id, credit);
        return new World(seller, company.Id, invoice, await InvoiceTestIssues.UserAsync(db), await InvoiceTestIssues.UserAsync(db));
    }

    private async Task<Invoice> InvoicedOrderAsync(Guid seller, Guid companyId, bool charge)
    {
        await using var db = fixture.CreateContext();
        var company = await db.Companies.SingleAsync(c => c.Id == companyId);
        await using var tx = await db.Database.BeginTransactionAsync();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
        if (charge)
            db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order.Id, order.GrandTotalMinor, DateTime.UtcNow, DateTime.UtcNow).Entry);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller, await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return invoice;
    }

    private static RecordWithholdingRequest Vds(string number, long amountMinor) => new("vds", number, Today, amountMinor);

    private Task<RecordedWithholding> RecordAsync(Guid actor, Guid invoiceId, RecordWithholdingRequest body, string? key = null) =>
        As(actor, s => s.RecordAsync(invoiceId, body, key ?? Key()));

    private async Task<(List<CreditLedgerEntry> Lines, Company Company)> LedgerAsync(World w)
    {
        await using var db = fixture.CreateContext();
        return (await db.CreditLedgerEntries.AsNoTracking().Where(l => l.OrderId == w.Invoice.OrderId).OrderBy(l => l.Seq).ToListAsync(),
            await db.Companies.AsNoTracking().SingleAsync(c => c.Id == w.Company));
    }

    [PostgresFact]
    public async Task VerifyWithholding_ByTheRecorder_IsRefused()
    {
        var w = await WorldAsync();
        var recorded = (await RecordAsync(w.Recorder, w.Invoice.Id, Vds("VDS-2026/0001", 100_000))).Withholding;

        var verify = () => As(w.Recorder, s => s.VerifyAsync(recorded.Id));
        (await verify.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.FourEyes);
        var reject = () => As(w.Recorder, s => s.RejectAsync(recorded.Id, new RejectWithholdingRequest("Typed against the wrong invoice")));
        (await reject.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.FourEyes);
        (await As(w.Checker, s => s.GetAsync(recorded.Id))).Status.Should().Be("recorded");
        (await LedgerAsync(w)).Lines.Should().ContainSingle(l => l.Kind == CreditLedgerKind.Charge, "nothing was posted");

        // Another person rejects with a reason; the number is free again for a corrected entry.
        var noReason = () => As(w.Checker, s => s.RejectAsync(recorded.Id, new RejectWithholdingRequest("no")));
        await noReason.Should().ThrowAsync<ValidationException>();
        var rejected = await As(w.Checker, s => s.RejectAsync(recorded.Id, new RejectWithholdingRequest("Amount differs from the paper certificate")));
        (rejected.Status, rejected.DecidedBy).Should().Be(("rejected", w.Checker));
        (await RecordAsync(w.Recorder, w.Invoice.Id, Vds("vds 2026 0001", 90_000))).Withholding.Status.Should().Be("recorded");

        // Only orders on credit have a receivable to settle.
        var cash = await WorldAsync(credit: false);
        var onCash = () => RecordAsync(cash.Recorder, cash.Invoice.Id, Vds("VDS-CASH-1", 1_000));
        (await onCash.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholdingService.NeedsCreditOrder);
    }

    [PostgresFact]
    public async Task AVerifiedWithholding_SettlesTheReceivable_Once()
    {
        var w = await WorldAsync();
        var (before, companyBefore) = await LedgerAsync(w);
        var charge = before.Single(l => l.Kind == CreditLedgerKind.Charge).ReceivableDeltaMinor;
        const long amount = 500_000;

        var key = Key();
        var first = await RecordAsync(w.Recorder, w.Invoice.Id, Vds("VDS-2026/0042", amount), key);
        first.Replayed.Should().BeFalse();
        var replayed = await RecordAsync(w.Recorder, w.Invoice.Id, Vds("vds 2026/0042", amount), key);
        (replayed.Replayed, replayed.Withholding.Id).Should().Be((true, first.Withholding.Id), "same person, key and facts");
        var otherBody = () => RecordAsync(w.Recorder, w.Invoice.Id, Vds("VDS-2026/0042", amount + 1), key);
        (await otherBody.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("IDEMPOTENCY_KEY_REUSED");
        var id = first.Withholding.Id;

        var verified = await As(w.Checker, s => s.VerifyAsync(id));
        (verified.Status, verified.DecidedBy).Should().Be(("verified", w.Checker));
        var again = await As(w.Checker, s => s.VerifyAsync(id));   // a replay (double click, retry after a timeout)
        (again.Status, again.DecidedAtUtc).Should().Be(("verified", verified.DecidedAtUtc));
        var third = () => As(Guid.NewGuid(), s => s.VerifyAsync(id));
        (await third.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.NotRecorded);
        var lateReject = () => As(Guid.NewGuid(), s => s.RejectAsync(id, new RejectWithholdingRequest("Changed my mind about it")));
        (await lateReject.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.NotRecorded);

        var (after, company) = await LedgerAsync(w);
        var line = after.Should().ContainSingle(l => l.Kind == CreditLedgerKind.WithholdingApplied, "one line, whatever the replays").Subject;
        (line.ReceivableDeltaMinor, line.OnAccountDeltaMinor, line.WithholdingId, line.IdempotencyKey, line.ActorUserId)
            .Should().Be((-amount, 0L, id, CreditLedgerKeys.Withholding(id), w.Checker));
        after.Sum(l => l.ReceivableDeltaMinor).Should().Be(charge - amount, "the order now owes the invoice less the certificate");
        company.CreditUsedMinor.Should().Be(companyBefore.CreditUsedMinor - amount);
        await using var db = fixture.CreateContext();
        (await db.AuditLogs.CountAsync(a => a.Action == "invoice.withholding_verified" && a.EntityId == id.ToString())).Should().Be(1);
    }

    [PostgresFact]
    public async Task TheSameCertificateNumber_OfTheSameCompany_Is409()
    {
        var w = await WorldAsync();
        var second = await InvoicedOrderAsync(w.Seller, w.Company, charge: true);
        await RecordAsync(w.Recorder, w.Invoice.Id, Vds("VDS-2026/0777", 10_000));

        var duplicate = () => RecordAsync(w.Checker, second.Id, Vds("vds 2026 0777", 10_000));
        (await duplicate.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholdingService.Duplicate,
            "one certificate per issuing company and kind, whatever invoice it is typed against");
        (await RecordAsync(w.Checker, second.Id, new RecordWithholdingRequest("ait", "VDS-2026/0777", Today, 10_000))).Withholding.Kind
            .Should().Be("ait", "another kind is another certificate");
    }

    [PostgresFact]
    public async Task TheCap_IsReadUnderTheInvoiceLock_WhileACreditNoteRuns()
    {
        var w = await WorldAsync();
        var plc = w.Invoice.Lines.Single(l => l.Sku == "PLC-1200").LineNo;
        await using var other = fixture.CreateContext();
        await using var tx = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoices WHERE id = {w.Invoice.Id} FOR NO KEY UPDATE");

        var full = RecordAsync(w.Recorder, w.Invoice.Id, Vds("VDS-2026/0900", w.Invoice.VatMinor));   // the whole VAT, read before the credit note = room
        for (var i = 0; i < 100 && !await WaitingOnALockAsync(); i++) await Task.Delay(50);
        (await WaitingOnALockAsync()).Should().BeTrue("the record waits for the invoice the credit note holds");
        full.IsCompleted.Should().BeFalse();

        await InvoiceTestIssues.CreditNoteAsync(other, w.Invoice.Id, await Allocator(other).TakeAsync(w.Seller, InvoiceSeries.CreditNote), w.Checker,
            [new Spec(plc, 1, 4_500_000, 675_000)]);
        await tx.CommitAsync();

        (await full.Awaiting(t => t).Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.ExceedsVat,
            "the caps are read after the lock, so the committed credit note counts");
        (await RecordAsync(w.Recorder, w.Invoice.Id, Vds("VDS-2026/0900", w.Invoice.VatMinor - 675_000))).Withholding.AmountMinor
            .Should().Be(w.Invoice.VatMinor - 675_000);
    }

    private async Task<bool> WaitingOnALockAsync()
    {
        await using var db = fixture.CreateContext();
        return (await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").ToListAsync()).Single() > 0;
    }
}
