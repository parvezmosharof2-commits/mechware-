using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using Spec = Mechware.IntegrationTests.Invoicing.InvoiceTestIssues.CreditSpec;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.5a, threat model §১৪ IT3 + IT17, ADR-0012 §9.2, §16: the deferred credit-cap trigger (Σ credit lines ≤ the original line, at COMMIT),
/// the <c>credit_note_requests</c> table and its guard, and migration <c>AddCreditNoteRequests</c>. Credit notes are written by SQL (no
/// issuing service before E6.5b) inside a real counter step. Original: PLC-1200 2 × net 9 000 000 VAT 1 350 000; CBL-10 25 × net 308 625
/// VAT 23 147; delivery 12 000.
/// </summary>
public class CreditNotePersistenceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string LastMigrationBefore = "20261121043907_AddInvoices";
    private const string Violation = PostgresErrorCodes.IntegrityConstraintViolation;

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private static PostgresException Pg(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException ?? throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL error, got {ex}");

    private async Task<(Guid Seller, Invoice Original, Guid Requester)> OriginalAsync()
    {
        await using var db = fixture.CreateContext();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        var company = await InvoiceTestIssues.ActiveCompanyAsync(db);
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller, await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (seller, invoice, await InvoiceTestIssues.UserAsync(db));
    }

    /// <summary>One credit note in its own transaction: the counter step, the SQL inserts (accepted at once), then COMMIT. Null = committed.</summary>
    private async Task<PostgresException?> CreditAsync(Guid seller, Guid original, Guid requester, params Spec[] lines)
    {
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await InvoiceTestIssues.CreditNoteAsync(db, original, await Allocator(db).TakeAsync(seller, InvoiceSeries.CreditNote), requester, lines);
        try
        {
            await tx.CommitAsync();
            return null;
        }
        catch (Exception ex)
        {
            return Pg(ex);
        }
    }

    private async Task<long> CreditedNetAsync(Guid original)
    {
        await using var db = fixture.CreateContext();
        return await db.Invoices.Where(i => i.OriginalInvoiceId == original).SumAsync(i => i.NetMinor);
    }

    [PostgresFact]
    public async Task CreditLinesOverTheOriginalLine_FailAtCommit()
    {
        var (seller, original, requester) = await OriginalAsync();
        var (plc, cable) = (original.Lines.Single(l => l.Sku == "PLC-1200"), original.Lines.Single(l => l.Sku == "CBL-10"));
        foreach (var (why, lines) in new (string, Spec[])[]
        {
            ("quantity over", [new(plc.LineNo, plc.Quantity + 1, 100, 15)]),
            ("value over", [new(cable.LineNo, 1, cable.NetMinor + 1, 0)]),
            ("VAT over while quantity and value fit", [new(cable.LineNo, 1, 100, cable.VatMinor + 1)]),
            ("one good line, one over", [new(plc.LineNo, 1, 100, 15), new(cable.LineNo, 0, 100, cable.VatMinor + 1)])
        })
        {
            var pg = await CreditAsync(seller, original.Id, requester, lines);
            pg.Should().NotBeNull(why);
            (pg!.SqlState, pg.MessageText).Should().Match<(string, string)>(x => x.Item1 == Violation && x.Item2.StartsWith("invoice_lines: credit notes of line"), why);
        }
        (await CreditedNetAsync(original.Id)).Should().Be(0, "nothing of a refused credit note stays");

        // Two credit notes, each within the line, together over it: the second fails at its COMMIT.
        (await CreditAsync(seller, original.Id, requester, new Spec(plc.LineNo, 1, plc.NetMinor / 2, plc.VatMinor / 2))).Should().BeNull();
        (await CreditAsync(seller, original.Id, requester, new Spec(plc.LineNo, 1, plc.NetMinor / 2 + 1, plc.VatMinor / 2))).Should().NotBeNull();
        (await CreditAsync(seller, original.Id, requester, new Spec(plc.LineNo, 1, plc.NetMinor / 2, plc.VatMinor / 2 + 1))).Should().NotBeNull();
        (await CreditAsync(seller, original.Id, requester, new Spec(plc.LineNo, 2, plc.NetMinor / 2, plc.VatMinor / 2))).Should().NotBeNull();
        (await CreditAsync(seller, original.Id, requester, new Spec(plc.LineNo, 1, plc.NetMinor - plc.NetMinor / 2, plc.VatMinor - plc.VatMinor / 2)))
            .Should().BeNull("exactly the rest fits: the line is fully credited");
        (await CreditAsync(seller, original.Id, requester, new Spec(plc.LineNo, 0, 1, 0))).Should().NotBeNull("a fully credited line takes nothing more");
        (await CreditedNetAsync(original.Id)).Should().Be(plc.NetMinor);

        // A credit line must credit a line of its own original invoice.
        var (otherSeller, other, _) = await OriginalAsync();
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await InvoiceTestIssues.CreditNoteAsync(db, other.Id, await Allocator(db).TakeAsync(otherSeller, InvoiceSeries.CreditNote), requester,
                [new Spec(2, 1, 100, 0)], linesFrom: original.Id);
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync()));
            (pg.SqlState, pg.MessageText).Should().Match<(string, string)>(x => x.Item1 == Violation && x.Item2.Contains("credits a line of its original invoice"));
        }
    }

    private static FormattableString InsertRequest(Guid id, Guid original, Guid order, Guid requester, string reasonText) => $"""
        INSERT INTO credit_note_requests (id, original_invoice_id, order_id, status, reason, reason_text, lines, total_minor, requested_by,
            idempotency_key, request_hash, created_at, updated_at)
        VALUES ({id}, {original}, {order}, 'Requested', 'Error', {reasonText}, jsonb_build_array(jsonb_build_object('quantity', 1)), 100,
            {requester}, {"key-" + id.ToString("N")}, repeat('a', 64), now(), now())
        """;

    [PostgresFact]
    public async Task ACreditNoteOfACreditNote_IsRefused()
    {
        var (seller, original, requester) = await OriginalAsync();
        (await CreditAsync(seller, original.Id, requester, new Spec(1, 1, 100, 15))).Should().BeNull();
        Guid creditNote;
        await using (var db = fixture.CreateContext()) creditNote = await db.Invoices.Where(i => i.OriginalInvoiceId == original.Id).Select(i => i.Id).SingleAsync();

        await using (var db = fixture.CreateContext())
        {
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                InsertRequest(Guid.NewGuid(), creditNote, original.OrderId, requester, "Credit the credit note"))));
            (pg.SqlState, pg.MessageText).Should().Be((Violation, "credit_note_requests: a new request asks to credit a tax invoice of its order"));
        }
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var taken = await Allocator(db).TakeAsync(seller, InvoiceSeries.CreditNote);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => InvoiceTestIssues.CreditNoteAsync(db, creditNote, taken, requester, 50));
            Pg(refused).MessageText.Should().Be("invoices: a credit note names a tax invoice of the same seller, order and mode");
        }
    }

    /// <summary>IT17: the reason text is cleaned before it is stored, comes back as stored, and the table refuses a dirty one written around the domain.</summary>
    [PostgresFact]
    public async Task CreditNoteReason_IsStoredAsPlainText_AndControlCharactersAreStripped()
    {
        var (_, original, requester) = await OriginalAsync();
        var plan = CreditNotePlanner.Plan(original, new Dictionary<Guid, CreditedSoFar>(), [new CreditLineRequest(original.Lines[0].Id, 1)],
            CreditNoteReason.Error, "  <script>alert(1)</script>\u0000\r\n‮wrong​ BIN\t ", DateTime.UtcNow, 180);
        var request = CreditNoteRequest.Create(Guid.NewGuid(), original, plan, requester, "idem-" + Guid.NewGuid().ToString("N"), new string('b', 64),
            "return:" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.CreditNoteRequests.Add(request);
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var stored = await db.CreditNoteRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
            stored.ReasonText.Should().Be("<script>alert(1)</script> wrong BIN");
            (stored.Status, stored.Reason, stored.TotalMinor, stored.OrderId).Should().Be((CreditNoteRequestStatus.Requested, CreditNoteReason.Error, plan.TotalMinor,
                original.OrderId));
            stored.Lines.Should().Equal(new CreditNoteRequestLine(original.Lines[0].Id, 1, plan.Lines[0].NetMinor, plan.Lines[0].VatMinor));
        }
        foreach (var dirty in new[] { "bell\u0007 inside", "two\nlines here", " padded text", "double  space", "tiny" })
        {
            await using var db = fixture.CreateContext();
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                InsertRequest(Guid.NewGuid(), original.Id, original.OrderId, requester, dirty))));
            (pg.SqlState, pg.ConstraintName).Should().Be((PostgresErrorCodes.CheckViolation, "ck_credit_note_requests_reason"), dirty);
        }
    }

    /// <summary>§9.3 at DB level: one move out of Requested; the asked content never changes; an approval names the 6.7 it issued; no delete.</summary>
    [PostgresFact]
    public async Task ACreditNoteRequest_MovesOnce_AndKeepsWhatWasAsked_AtDbLevel()
    {
        var (seller, original, requester) = await OriginalAsync();
        var id = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync(InsertRequest(id, original.Id, original.OrderId, requester, "Wrong price"));
        (await CreditAsync(seller, original.Id, requester, new Spec(1, 1, 100, 15))).Should().BeNull();
        Guid creditNote;
        await using (var db = fixture.CreateContext()) creditNote = await db.Invoices.Where(i => i.OriginalInvoiceId == original.Id).Select(i => i.Id).SingleAsync();

        foreach (var (why, sql) in new (string, FormattableString)[]
        {
            ("asked lines changed", $"UPDATE credit_note_requests SET lines = lines || lines WHERE id = {id}"),
            ("total changed", $"UPDATE credit_note_requests SET total_minor = 99 WHERE id = {id}"),
            ("approved naming the tax invoice",
                $"UPDATE credit_note_requests SET status = 'Approved', decided_by = {original.IssuedBy}, decided_at = now(), issued_invoice_id = {original.Id} WHERE id = {id}"),
            ("approved by someone who did not issue it",
                $"UPDATE credit_note_requests SET status = 'Approved', decided_by = {requester}, decided_at = now(), issued_invoice_id = {creditNote} WHERE id = {id}"),
            ("deleted", $"DELETE FROM credit_note_requests WHERE id = {id}")
        })
        {
            await using var db = fixture.CreateContext();
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(sql))).SqlState
                .Should().BeOneOf([Violation, PostgresErrorCodes.CheckViolation], why);
        }
        await using (var db = fixture.CreateContext())
        {
            (await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE credit_note_requests SET status = 'Approved', decided_by = {original.IssuedBy}, decided_at = now(), issued_invoice_id = {creditNote}, updated_at = now() WHERE id = {id}"))
                .Should().Be(1, "the approver issued the credit note that names the request's original");
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE credit_note_requests SET status = 'Withdrawn', decided_by = requested_by, issued_invoice_id = NULL WHERE id = {id}"))).SqlState.Should().Be(Violation);
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE credit_note_requests CASCADE"))).SqlState.Should().Be(Violation);
        }
    }

    [PostgresFact]
    public async Task Migration_AddCreditNoteRequests_GoesUpDownAndUpAgain()
    {
        var name = "mw_e65a_" + Guid.NewGuid().ToString("N");
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
            (await ObjectsAsync(ctx)).Should().Be((1, 3, 2, 1), "Up: the table, 3 triggers, 2 functions, the widened line CHECK");

            await ctx.GetService<IMigrator>().MigrateAsync(LastMigrationBefore);
            (await ObjectsAsync(ctx)).Should().Be((0, 0, 0, 0), "Down removes the table, the triggers (also the one on invoice_lines), the functions");
            (await ctx.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname LIKE 'trg_invoice%'").ToListAsync())
                .Single().Should().Be(9, "the 7 E6.3b invoice triggers and the 2 E6.2 counter guards stay");

            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            (await ObjectsAsync(ctx)).Should().Be((1, 3, 2, 1), "Up again");
            await ctx.Database.ExecuteSqlRawAsync(CreditNoteSql.Install); // a re-run changes nothing
            (await ObjectsAsync(ctx)).Should().Be((1, 3, 2, 1));
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

    private static async Task<(int Tables, int Triggers, int Functions, int WideCheck)> ObjectsAsync(MechwareDbContext ctx)
    {
        var row = (await ctx.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM pg_tables WHERE tablename = 'credit_note_requests') * 1000
                  + (SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_invoice_credit_caps_check', 'trg_credit_note_requests_guard',
                        'trg_credit_note_requests_no_truncate')) * 100
                  + (SELECT count(*) FROM pg_proc WHERE proname IN ('invoice_credit_caps_check', 'credit_note_requests_guard')) * 10
                  + (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_invoice_lines_shape' AND pg_get_constraintdef(oid) LIKE '%original_line_id%'))::int AS "Value"
            """).ToListAsync()).Single();
        return (row / 1000, row / 100 % 10, row / 10 % 10, row % 10);
    }
}
