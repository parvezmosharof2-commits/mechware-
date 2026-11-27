using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using Spec = Mechware.IntegrationTests.Invoicing.InvoiceTestIssues.CreditSpec;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.6a, threat model IT14, ADR-0012 §11, §16: <c>tax_withholdings</c> (VDS/AIT certificates), its caps at COMMIT (<see cref="TaxWithholdingSql"/>),
/// one certificate number per issuing company and kind, the guard, section (c) of the 9.1 file, and migration <c>AddTaxWithholdings</c>. There is
/// no service before E6.6b: certificates go through the domain and EF; "stale caps" stand for a service that read the caps wrongly, so only
/// the database is left to refuse.
/// </summary>
public class TaxWithholdingPersistenceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string LastMigrationBefore = "20261127014031_AddStaffRoleChanges";
    private static readonly WithholdingCaps NoCheck = new(long.MaxValue / 4, long.MaxValue / 4, 0, 0, 0, 0);

    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private static PostgresException Pg(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException ?? throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL error, got {ex}");

    private sealed record Setup(Invoice Invoice, Company Company, Guid Seller, Guid Recorder, Guid Checker);

    /// <summary>A Live 6.3 of a company: PLC-1200 2 × net 9 000 000 VAT 1 350 000; CBL-10 25 × net 308 625 VAT 23 147; delivery 12 000.</summary>
    private async Task<Setup> InvoiceAsync(Company? company = null)
    {
        await using var db = fixture.CreateContext();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        company ??= await InvoiceTestIssues.ActiveCompanyAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller, await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new Setup(invoice, company, seller, await InvoiceTestIssues.UserAsync(db), await InvoiceTestIssues.UserAsync(db));
    }

    /// <summary>What E6.6b will read under the invoice lock: credit notes of the invoice and its live (not rejected) certificates.</summary>
    private async Task<WithholdingCaps> CapsAsync(Invoice invoice)
    {
        await using var db = fixture.CreateContext();
        var credited = await db.Invoices.Where(c => c.OriginalInvoiceId == invoice.Id && c.Kind == InvoiceKind.CreditNote)
            .GroupBy(c => 1).Select(g => new { Net = g.Sum(c => c.NetMinor), Vat = g.Sum(c => c.VatMinor) }).SingleOrDefaultAsync();
        var live = db.TaxWithholdings.Where(w => w.InvoiceId == invoice.Id && w.Status != TaxWithholdingStatus.Rejected);
        return new WithholdingCaps(invoice.NetMinor, invoice.VatMinor, credited?.Net ?? 0, credited?.Vat ?? 0,
            await live.Where(w => w.Kind == TaxWithholdingKind.Vds).SumAsync(w => w.AmountMinor),
            await live.Where(w => w.Kind == TaxWithholdingKind.Ait).SumAsync(w => w.AmountMinor));
    }

    /// <summary>Records one certificate in its own transaction. Null = committed; otherwise the PostgreSQL error (constraint, unique key or COMMIT check).</summary>
    private async Task<PostgresException?> RecordAsync(Setup s, TaxWithholdingKind kind, string number, long amountMinor, WithholdingCaps? caps = null,
        Invoice? invoice = null)
    {
        var target = invoice ?? s.Invoice;
        var withholding = TaxWithholding.Record(Guid.NewGuid(), target, kind, number, Today, amountMinor, caps ?? await CapsAsync(target), s.Recorder, DateTime.UtcNow);
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.TaxWithholdings.Add(withholding);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return null;
        }
        catch (Exception ex)
        {
            return Pg(ex);
        }
    }

    private static string Cert() => "VDS-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    [PostgresFact]
    public async Task VdsCannotExceedTheInvoiceVat()
    {
        var s = await InvoiceAsync();
        var vat = s.Invoice.VatMinor;

        var over = async () => await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), vat + 1);
        (await over.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.ExceedsVat, "the domain refuses with clean caps");

        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), vat - 100)).Should().BeNull();
        var second = async () => await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), 101);
        (await second.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.ExceedsVat, "earlier certificates count");
        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), 100)).Should().BeNull("exactly the VAT fits");

        var stale = new WithholdingCaps(s.Invoice.NetMinor, vat, 0, 0, 0, 0);
        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), 1, stale))!.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation,
            "the COMMIT check counts every recorded certificate, whatever the service read");
        (await CapsAsync(s.Invoice)).WithheldVdsMinor.Should().Be(vat);
    }

    [PostgresFact]
    public async Task AitCannotExceedTheInvoiceNet()
    {
        var s = await InvoiceAsync();
        var (net, vat) = (s.Invoice.NetMinor, s.Invoice.VatMinor);

        var over = async () => await RecordAsync(s, TaxWithholdingKind.Ait, "AIT 1", net + 1);
        (await over.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.ExceedsNet);
        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), vat)).Should().BeNull("VDS takes nothing from the AIT room");
        (await RecordAsync(s, TaxWithholdingKind.Ait, "AIT 1", net)).Should().BeNull("AIT is capped by the value excl. VAT, not the total");

        (await RecordAsync(s, TaxWithholdingKind.Ait, "AIT 2", 1, NoCheck))!.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), 1, NoCheck))!.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
    }

    [PostgresFact]
    public async Task TheCaps_LeaveOutWhatACreditNoteTook()
    {
        var s = await InvoiceAsync();
        var plc = s.Invoice.Lines.Single(l => l.Sku == "PLC-1200");
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await InvoiceTestIssues.CreditNoteAsync(db, s.Invoice.Id, await Allocator(db).TakeAsync(s.Seller, InvoiceSeries.CreditNote), s.Recorder,
                [new Spec(plc.LineNo, 1, 4_500_000, 675_000)]);
            await tx.CommitAsync();
        }
        var (vatLeft, netLeft) = (s.Invoice.VatMinor - 675_000, s.Invoice.NetMinor - 4_500_000);
        var caps = await CapsAsync(s.Invoice);
        (caps.Left(TaxWithholdingKind.Vds), caps.Left(TaxWithholdingKind.Ait)).Should().Be((vatLeft, netLeft));

        var domain = async () => await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), vatLeft + 1);
        (await domain.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TaxWithholding.ExceedsVat);

        // A service that forgot the credit note: the database still refuses at COMMIT.
        var ignoringCredit = new WithholdingCaps(s.Invoice.NetMinor, s.Invoice.VatMinor, 0, 0, 0, 0);
        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), vatLeft + 1, ignoringCredit))!.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
        (await RecordAsync(s, TaxWithholdingKind.Ait, "AIT 9", netLeft + 1, ignoringCredit))!.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
        (await RecordAsync(s, TaxWithholdingKind.Vds, Cert(), vatLeft)).Should().BeNull();
        (await RecordAsync(s, TaxWithholdingKind.Ait, "AIT 9", netLeft)).Should().BeNull();
    }

    [PostgresFact]
    public async Task TheSameCertificateNumber_CannotBeRecordedTwice()
    {
        var first = await InvoiceAsync();
        var second = await InvoiceAsync(first.Company);
        var other = await InvoiceAsync();
        Guid firstId;
        await using (var db = fixture.CreateContext())
        {
            var w = TaxWithholding.Record(Guid.NewGuid(), first.Invoice, TaxWithholdingKind.Vds, "VDS-2026/0012", Today, 1_000, await CapsAsync(first.Invoice),
                first.Recorder, DateTime.UtcNow);
            db.TaxWithholdings.Add(w);
            await db.SaveChangesAsync();
            firstId = w.Id;
        }

        (await RecordAsync(second, TaxWithholdingKind.Vds, "vds 2026 0012", 1_000))!.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation,
            "one certificate per issuing company, whatever invoice it is typed against, spaces and punctuation aside");
        (await RecordAsync(first, TaxWithholdingKind.Vds, "VDS2026-0012", 1))!.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        (await RecordAsync(second, TaxWithholdingKind.Ait, "VDS-2026/0012", 1_000)).Should().BeNull("an AIT certificate is another document");
        (await RecordAsync(other, TaxWithholdingKind.Vds, "VDS-2026/0012", 1_000)).Should().BeNull("another company's number space");

        await using (var db = fixture.CreateContext())
        {
            var w = await db.TaxWithholdings.SingleAsync(x => x.Id == firstId);
            w.Reject(first.Checker, "Typed against the wrong invoice", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        (await RecordAsync(second, TaxWithholdingKind.Vds, "VDS-2026/0012", 1_000)).Should().BeNull("a rejected entry frees its number");
    }

    [PostgresFact]
    public async Task ACertificate_MovesOnce_AndItsFactsNeverChange()
    {
        var s = await InvoiceAsync();
        var w = TaxWithholding.Record(Guid.NewGuid(), s.Invoice, TaxWithholdingKind.Vds, Cert(), Today, 5_000, await CapsAsync(s.Invoice), s.Recorder, DateTime.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.TaxWithholdings.Add(w);
            await db.SaveChangesAsync();
        }

        async Task<string?> SqlAsync(FormattableString sql)
        {
            await using var db = fixture.CreateContext();
            try { await db.Database.ExecuteSqlInterpolatedAsync(sql); return null; }
            catch (Exception ex) { return Pg(ex).SqlState; }
        }
        const string Refused = PostgresErrorCodes.IntegrityConstraintViolation;
        (await SqlAsync($"UPDATE tax_withholdings SET amount_minor = 4000 WHERE id = {w.Id}")).Should().Be(Refused);
        (await SqlAsync($"UPDATE tax_withholdings SET status = 'Verified', decided_by = {s.Checker}, decided_at = now(), amount_minor = 4000 WHERE id = {w.Id}"))
            .Should().Be(Refused, "the decision cannot carry a changed amount");
        (await SqlAsync($"UPDATE tax_withholdings SET status = 'Verified', decided_by = {s.Recorder}, decided_at = now() WHERE id = {w.Id}"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "four eyes");
        (await SqlAsync($"DELETE FROM tax_withholdings WHERE id = {w.Id}")).Should().Be(Refused);

        await using (var db = fixture.CreateContext())
        {
            var row = await db.TaxWithholdings.SingleAsync(x => x.Id == w.Id);
            var self = () => row.Verify(s.Recorder, DateTime.UtcNow);
            self.Should().Throw<ConflictException>().Which.Code.Should().Be(TaxWithholding.FourEyes);
            row.Verify(s.Checker, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        (await SqlAsync($"UPDATE tax_withholdings SET status = 'Rejected', decision_reason = 'changed my mind' WHERE id = {w.Id}")).Should().Be(Refused);
    }

    [PostgresFact]
    public async Task VerifiedVdsCertificates_FillSectionC_OfTheirMonth()
    {
        var s = await InvoiceAsync();
        var (verified, recorded, ait) = (Cert(), Cert(), "AIT-77");
        await using (var db = fixture.CreateContext())
        {
            var caps = await CapsAsync(s.Invoice);
            var v = TaxWithholding.Record(Guid.NewGuid(), s.Invoice, TaxWithholdingKind.Vds, verified, Today, 7_000, caps, s.Recorder, DateTime.UtcNow);
            var r = TaxWithholding.Record(Guid.NewGuid(), s.Invoice, TaxWithholdingKind.Vds, recorded, Today, 3_000, caps, s.Recorder, DateTime.UtcNow);
            var a = TaxWithholding.Record(Guid.NewGuid(), s.Invoice, TaxWithholdingKind.Ait, ait, Today, 9_000, caps, s.Recorder, DateTime.UtcNow);
            db.TaxWithholdings.AddRange(v, r, a);
            await db.SaveChangesAsync();
            v.Verify(s.Checker, DateTime.UtcNow);
            a.Verify(s.Checker, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var month = VatMonth.Of(DateTime.UtcNow).Label;
        var service = new VatMonthExportService(read, new Staff(s.Checker), new InvoicingOptions(), TimeProvider.System);
        var report = await service.PreviewAsync(month);
        report.WithholdingsAvailable.Should().BeTrue();
        var row = report.Vds.Should().ContainSingle("only verified VDS: not the recorded one, not AIT").Subject;
        (row.CertificateNumber, row.InvoiceNumber, row.AmountMinor, row.CertificateDate).Should().Be((verified, s.Invoice.Number, 7_000L, Today));
        row.BuyerBin.Should().NotBe(s.Invoice.Buyer.Bin, "the preview masks the BIN");

        var csv = (await service.CsvAsync(month)).Content;
        csv.Should().Contain($"\"{verified}\"").And.Contain($"\"{s.Invoice.Buyer.Bin}\"").And.NotContain(recorded).And.NotContain(ait)
            .And.Contain("\"total\",\"1\",\"\",\"\",\"\",\"\",\"7000\",\"70.00\"");
        (await service.PreviewAsync(VatMonth.Of(DateTime.UtcNow.AddMonths(-1)).Label)).Vds.Should().BeEmpty("another month");
    }

    [PostgresFact]
    public async Task Migration_AddTaxWithholdings_GoesUpDownAndUpAgain()
    {
        var name = "mw_e66a_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(TestInfra.AdminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        var cs = MechwareDatabase.WithDefaults(new NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = name }.ConnectionString);
        try
        {
            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            await using var ctx = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(cs).Options);
            (await ObjectsAsync(ctx)).Should().Be((1, 3, 2, 1), "Up: the table, 3 triggers, 2 functions, the ledger column");

            await ctx.GetService<IMigrator>().MigrateAsync(LastMigrationBefore);
            (await ObjectsAsync(ctx)).Should().Be((0, 0, 0, 0), "Down removes the table, its triggers and functions, the ledger column");

            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            await ctx.Database.ExecuteSqlRawAsync(TaxWithholdingSql.Install); // a re-run changes nothing
            (await ObjectsAsync(ctx)).Should().Be((1, 3, 2, 1), "Up again");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(TestInfra.AdminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(int Tables, int Triggers, int Functions, int LedgerColumns)> ObjectsAsync(MechwareDbContext ctx)
    {
        async Task<int> CountAsync(FormattableString sql) => (await ctx.Database.SqlQuery<int>(sql).ToListAsync()).Single();
        return (await CountAsync($"SELECT count(*)::int AS \"Value\" FROM pg_class WHERE relname = 'tax_withholdings' AND relkind = 'r'"),
            await CountAsync($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname LIKE 'trg_tax_withholdings%'"),
            await CountAsync($"SELECT count(*)::int AS \"Value\" FROM pg_proc WHERE proname LIKE 'tax_withholdings%'"),
            await CountAsync($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'credit_ledger_entries' AND column_name = 'withholding_id'"));
    }

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "127.0.0.1";
        public string? UserAgent => null;
        public string? CorrelationId => null;
        public bool HasPermission(string permission) => true;
    }
}
