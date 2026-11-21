using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Invoicing;
using Mechware.Domain.Companies;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.3b, threat model §১৪ IT2, IT1 (COMMIT check and uniques), IT9 (one invoice per shipment): the invoice tables, their CHECKs,
/// the insert-only guards, the one-time PDF record and the deferred counter ↔ invoice check. Each test has its own seller.
/// </summary>
[Collection(ExclusiveTimingCollection.Name)]
public class InvoicePersistenceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string LastMigrationBefore = "20261121005810_AddReturnDecisions";
    private const string Violation = PostgresErrorCodes.IntegrityConstraintViolation;

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private static PostgresException Pg(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException ?? throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL error, got {ex}");

    private async Task<Guid> SellerAsync()
    {
        await using var db = fixture.CreateContext();
        return await InvoiceTestIssues.SellerAsync(db);
    }

    /// <summary>Ship + issue in one transaction (order → counter → invoice → COMMIT), as E6.3c will.</summary>
    private async Task<Invoice> IssueAsync(Guid seller, bool company = false)
    {
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var buyer = company ? await InvoiceTestIssues.ActiveCompanyAsync(db) : null;
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, buyer);
        var taken = await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller, taken, buyer);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return invoice;
    }

    private async Task<PostgresException> RefusedAsync(FormattableString sql)
    {
        await using var db = fixture.CreateContext();
        return Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(sql)));
    }

    private async Task<string> RowAsync(Guid invoiceId)
    {
        await using var db = fixture.CreateContext();
        return (await db.Database.SqlQuery<string>($"""
            SELECT (SELECT to_jsonb(i)::text FROM invoices i WHERE i.id = {invoiceId})
                || (SELECT jsonb_agg(to_jsonb(l) ORDER BY l.line_no)::text FROM invoice_lines l WHERE l.invoice_id = {invoiceId}) AS "Value"
            """).ToListAsync()).Single();
    }

    // ---- round trip ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnIssuedInvoice_RoundTrips_AndItsContentHashStillMatches()
    {
        var seller = await SellerAsync();
        foreach (var company in new[] { true, false })
        {
            var issued = await IssueAsync(seller, company);
            await using var db = fixture.CreateContext();
            var loaded = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.Id == issued.Id);

            loaded.ContentHash.Should().Be(issued.ContentHash);
            InvoiceContentHash.Compute(loaded).Should().Be(issued.ContentHash, "every hashed field survives the database unchanged");
            InvoiceContentHash.Matches(loaded, issued.ContentHash).Should().BeTrue();
            loaded.Buyer.Should().Be(issued.Buyer);
            loaded.Lines.Should().HaveCount(issued.Lines.Count);
            loaded.TotalMinor.Should().Be(issued.TotalMinor).And.Be(loaded.Lines.Sum(l => l.TotalMinor));

            var raw = (await db.Database.SqlQuery<string>($"SELECT buyer_snapshot::text AS \"Value\" FROM invoices WHERE id = {issued.Id}").ToListAsync()).Single();
            if (issued.Buyer.Bin is { } bin) raw.Should().NotContain(bin, "the buyer BIN is encrypted at rest, as in companies");
        }
    }

    // ---- IT2: nothing changes after issue -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnIssuedInvoiceCannotBeChanged()
    {
        var invoice = await IssueAsync(await SellerAsync());
        var before = await RowAsync(invoice.Id);
        await using (var db = fixture.CreateContext())
        {
            var loaded = await db.Invoices.Include(i => i.Lines).SingleAsync(i => i.Id == invoice.Id);
            db.Entry(loaded).Property(i => i.NetMinor).CurrentValue += 1;
            db.Entry(loaded).Property(i => i.TotalMinor).CurrentValue += 1;
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync())).SqlState.Should().Be(Violation);
        }
        await using (var db = fixture.CreateContext())
        {
            var line = await db.InvoiceLines.FirstAsync(l => l.InvoiceId == invoice.Id);
            db.Entry(line).Property(l => l.DescriptionEn).CurrentValue = "Something else";
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync())).SqlState.Should().Be(Violation);
        }
        (await RowAsync(invoice.Id)).Should().Be(before);
    }

    [PostgresFact]
    public async Task UpdatingAnInvoiceOrLine_FailsAtDbLevel()
    {
        var invoice = await IssueAsync(await SellerAsync());
        var before = await RowAsync(invoice.Id);
        var id = invoice.Id;
        var refused = new (string Why, FormattableString Sql)[]
        {
            ("money, kept consistent", $"UPDATE invoices SET net_minor = net_minor + 1, total_minor = total_minor + 1 WHERE id = {id}"),
            ("VAT moved to net", $"UPDATE invoices SET net_minor = net_minor + vat_minor, vat_minor = 0 WHERE id = {id}"),
            ("number", $"UPDATE invoices SET number = replace(number, '-000', '-900') WHERE id = {id}"),
            ("buyer snapshot", $"UPDATE invoices SET buyer_snapshot = jsonb_set(buyer_snapshot, '{{name}}', '\"Someone else\"') WHERE id = {id}"),
            ("content hash", $"UPDATE invoices SET content_hash = repeat('0', 64) WHERE id = {id}"),
            ("issue instant", $"UPDATE invoices SET issued_at = issued_at - interval '1 day' WHERE id = {id}"),
            ("consultant flag", $"UPDATE invoices SET consultant_checked = NOT consultant_checked WHERE id = {id}"),
            ("issuer", $"UPDATE invoices SET issued_by = (SELECT u.id FROM users u WHERE u.id <> invoices.issued_by LIMIT 1) WHERE id = {id}"),
            ("a PDF together with money", $"UPDATE invoices SET pdf_en_key = 'k', pdf_en_sha256 = repeat('a', 64), vat_minor = vat_minor + 1, total_minor = total_minor + 1 WHERE id = {id}"),
            ("line money", $"UPDATE invoice_lines SET net_minor = net_minor + 1, total_minor = total_minor + 1 WHERE invoice_id = {id}"),
            ("line text", $"UPDATE invoice_lines SET description_bn = 'x' WHERE invoice_id = {id}"),
            ("line no-op", $"UPDATE invoice_lines SET quantity = quantity WHERE invoice_id = {id}")
        };
        foreach (var (why, sql) in refused)
            (await RefusedAsync(sql)).SqlState.Should().Be(Violation, why);
        (await RowAsync(id)).Should().Be(before, "no refused statement changed a byte");
    }

    [PostgresFact]
    public async Task DeletingOrTruncatingInvoices_FailsAtDbLevel()
    {
        var invoice = await IssueAsync(await SellerAsync());
        var id = invoice.Id;
        foreach (var (why, sql) in new (string, FormattableString)[]
                 {
                     ("delete an invoice", $"DELETE FROM invoices WHERE id = {id}"),
                     ("delete a line", $"DELETE FROM invoice_lines WHERE invoice_id = {id}"),
                     ("truncate invoices", $"TRUNCATE invoices CASCADE"),
                     ("truncate lines", $"TRUNCATE invoice_lines")
                 })
            (await RefusedAsync(sql)).SqlState.Should().Be(Violation, why);
        (await RefusedAsync($"DELETE FROM orders WHERE id = {invoice.OrderId}")).SqlState
            .Should().Be(PostgresErrorCodes.ForeignKeyViolation, "an order with an invoice cannot be deleted (RESTRICT)");
        await using var db = fixture.CreateContext();
        (await db.InvoiceLines.CountAsync(l => l.InvoiceId == id)).Should().Be(invoice.Lines.Count);
    }

    [PostgresFact]
    public async Task ThePdfColumns_CanBeWrittenOnceOnly()
    {
        var invoice = await IssueAsync(await SellerAsync());
        var id = invoice.Id;
        var (en, bn) = (new string('e', 64), new string('b', 64));
        await using var db = fixture.CreateContext();
        (await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE invoices SET pdf_en_key = {"invoices/" + id.ToString("N") + "/en.pdf"}, pdf_en_sha256 = {en} WHERE id = {id} AND pdf_en_key IS NULL")).Should().Be(1);

        foreach (var (why, sql) in new (string, FormattableString)[]
                 {
                     ("the SHA a second time", $"UPDATE invoices SET pdf_en_sha256 = {new string('f', 64)} WHERE id = {id}"),
                     ("the key a second time", $"UPDATE invoices SET pdf_en_key = 'elsewhere.pdf' WHERE id = {id}"),
                     ("back to null", $"UPDATE invoices SET pdf_en_key = NULL, pdf_en_sha256 = NULL WHERE id = {id}")
                 })
            (await RefusedAsync(sql)).SqlState.Should().Be(Violation, why);
        (await RefusedAsync($"UPDATE invoices SET pdf_bn_key = 'k' WHERE id = {id}")).SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation, "key and SHA are written together");
        (await RefusedAsync($"UPDATE invoices SET pdf_bn_key = 'k', pdf_bn_sha256 = 'NOT-A-SHA' WHERE id = {id}")).SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);

        (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE invoices SET pdf_bn_key = 'bn.pdf', pdf_bn_sha256 = {bn} WHERE id = {id}")).Should().Be(1,
            "the other locale is still null, so its first write is allowed");
        (await RefusedAsync($"UPDATE invoices SET pdf_bn_sha256 = {en} WHERE id = {id}")).SqlState.Should().Be(Violation);
        var shas = (await db.Database.SqlQuery<string>($"SELECT pdf_en_sha256 || pdf_bn_sha256 AS \"Value\" FROM invoices WHERE id = {id}").ToListAsync()).Single();
        shas.Should().Be(en + bn);
    }

    // ---- IT1 / IT9: uniques -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task InvoiceNumbers_AreUniquePerSeriesAndYear_AtDbLevel()
    {
        var seller = await SellerAsync();
        var other = await SellerAsync();
        var first = await IssueAsync(seller);
        // Another seller printing the same number; the same seller, series, year and serial under another prefix.
        foreach (var (by, prefix, index) in new[] { (other, InvoiceTestIssues.Prefix(seller), "ux_invoices_number"), (seller, "XY", "ux_invoices_serial") })
        {
            await using var db = fixture.CreateContext();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, by);
            var taken = new TakenInvoiceNumber(by, first.Series, first.FiscalYear, first.Serial, first.IssuedAtUtc);
            db.Invoices.Add(InvoiceTestIssues.Build(order, shipment, by, taken, prefix: prefix));
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync()));
            pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation, index);
            pg.ConstraintName.Should().Be(index);
        }
    }

    [PostgresFact]
    public async Task ASecondLiveInvoiceForAShipment_FailsAtDbLevel()
    {
        var seller = await SellerAsync();
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller);
        db.Invoices.Add(InvoiceTestIssues.Build(order, shipment, seller, await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice)));
        await db.SaveChangesAsync();
        db.Invoices.Add(InvoiceTestIssues.Build(order, shipment, seller, await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice)));
        var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync()));
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("ux_invoices_one_per_shipment");
    }

    // ---- IT1: every counter step has exactly its invoice, checked at COMMIT ------------------------------------------------

    [PostgresFact]
    public async Task ACounterChangeWithoutItsInvoice_FailsAtCommit()
    {
        var seller = await SellerAsync();
        var first = await IssueAsync(seller);

        // (a) a number taken and committed without an invoice; (b) two taken, only the first invoiced; (c) the invoice carries another instant.
        foreach (var variant in new[] { "none", "second missing", "other instant" })
        {
            await using var db = fixture.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller);
            var taken = await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice);
            if (variant == "second missing")
            {
                db.Invoices.Add(InvoiceTestIssues.Build(order, shipment, seller, taken));
                await db.SaveChangesAsync();
                await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice);
            }
            else if (variant == "other instant")
            {
                db.Invoices.Add(InvoiceTestIssues.Build(order, shipment, seller, taken with { IssuedAtUtc = taken.IssuedAtUtc.AddTicks(-10) }));
                await db.SaveChangesAsync();
            }
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync()));
            pg.SqlState.Should().Be(Violation, variant);
            pg.MessageText.Should().StartWith("invoice_number_counters:").And.Contain("needs exactly its invoice");
        }

        await using (var db = fixture.CreateContext())
            (await db.InvoiceNumberCounters.AsNoTracking().SingleAsync(c => c.SellerId == seller && c.Series == InvoiceSeries.TaxInvoice))
                .NextSerial.Should().Be(first.Serial + 1, "every refused commit gave its serial back");
        (await IssueAsync(seller)).Serial.Should().Be(first.Serial + 1);
    }

    [PostgresFact]
    public async Task AnInvoiceWithoutACounterStep_FailsAtCommit()
    {
        var seller = await SellerAsync();
        var first = await IssueAsync(seller);
        var fy = first.FiscalYear;
        foreach (var (why, taken) in new[]
                 {
                     ("the next serial, never taken", new TakenInvoiceNumber(seller, InvoiceSeries.TaxInvoice, fy, first.Serial + 1, DateTime.UtcNow)),
                     ("a serial far ahead", new TakenInvoiceNumber(seller, InvoiceSeries.TaxInvoice, fy, 500, DateTime.UtcNow)),
                     ("another fiscal year", new TakenInvoiceNumber(seller, InvoiceSeries.TaxInvoice, fy + 1, 1, new DateTime(fy + 1, 7, 2, 0, 0, 0, DateTimeKind.Utc))),
                     ("a specimen series without its counter", new TakenInvoiceNumber(seller, InvoiceSeries.SpecimenTaxInvoice, fy, 1, DateTime.UtcNow))
                 })
        {
            await using var db = fixture.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller);
            db.Invoices.Add(InvoiceTestIssues.Build(order, shipment, seller, taken));
            await db.SaveChangesAsync();
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync()));
            pg.SqlState.Should().Be(Violation, why);
            pg.MessageText.Should().Contain("has no counter step behind it", why);
        }
        await using var check = fixture.CreateContext();
        (await check.Invoices.CountAsync(i => i.SellerId == seller)).Should().Be(1);
    }

    [PostgresFact]
    public async Task ALineAddedToAnIssuedInvoice_FailsAtCommit()
    {
        var invoice = await IssueAsync(await SellerAsync());
        var id = invoice.Id;
        var pg = await RefusedAsync($"""
            INSERT INTO invoice_lines (id, invoice_id, line_no, kind, sku, description_en, description_bn, unit, quantity, unit_price_minor,
                gross_minor, discount_minor, net_minor, sd_rate, sd_minor, vat_rate, vat_minor, total_minor, created_at)
            VALUES ({Guid.NewGuid()}, {id}, {invoice.Lines.Count + 1}, 'Goods', 'X-1', 'Extra', 'Extra', 'pcs', 1, 100, 100, 0, 100, 0, 0, 0, 0, 100, now())
            """);
        pg.SqlState.Should().Be(Violation);
        pg.MessageText.Should().Contain("lines 1..n whose sums are its totals");
    }

    // ---- CHECKs -------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task TheInvoiceChecks_RefuseBadShapes()
    {
        var seller = await SellerAsync();
        var first = await IssueAsync(seller);
        var taken = new TakenInvoiceNumber(seller, InvoiceSeries.TaxInvoice, first.FiscalYear, first.Serial + 1, DateTime.UtcNow);
        var variants = new (string Check, Action<MechwareDbContext, Invoice> Spoil)[]
        {
            ("ck_invoices_number", (db, i) => db.Entry(i).Property(x => x.Number).CurrentValue = first.Number),
            ("ck_invoices_number", (db, i) => db.Entry(i).Property(x => x.Number).CurrentValue = "SPEC" + i.Number[i.Number.IndexOf('-')..]),
            ("ck_invoices_number", (db, i) => db.Entry(i).Property(x => x.Number).CurrentValue = i.Number.Replace("-INV-", "-CN-")),
            ("ck_invoices_kind_series", (db, i) => db.Entry(i).Property(x => x.Mode).CurrentValue = InvoicingMode.Specimen),
            ("ck_invoices_money", (db, i) => db.Entry(i).Property(x => x.TotalMinor).CurrentValue += 1),
            ("ck_invoices_money", (db, i) => db.Entry(i).Property(x => x.VatMinor).CurrentValue = -1),
            ("ck_invoices_hash", (db, i) => db.Entry(i).Property(x => x.ContentHash).CurrentValue = i.ContentHash.ToUpperInvariant()),
            ("ck_invoices_kind_shape", (db, i) => db.Entry(i).Property(x => x.OriginalInvoiceId).CurrentValue = first.Id),
            ("ck_invoices_kind_shape", (db, i) => db.Entry(i).Property<string?>("Reason").CurrentValue = "Return"),
            ("ck_invoices_links", (db, i) => db.Entry(i).Property(x => x.ReplacementNo).CurrentValue = 1),
            ("ck_invoices_pdf", (db, i) => db.Entry(i).Property<string?>("PdfEnKey").CurrentValue = "en.pdf"),
            ("ck_invoice_lines_money", (db, i) => db.Entry(i.Lines[0]).Property(x => x.TotalMinor).CurrentValue += 1),
            ("ck_invoice_lines_unit_price", (db, i) => db.Entry(i.Lines[0]).Property(x => x.UnitPriceMinor).CurrentValue = 5),
            ("ck_invoice_lines_shape", (db, i) => db.Entry(i.Lines[^1]).Property(x => x.Quantity).CurrentValue = 2)
        };
        foreach (var (check, spoil) in variants)
        {
            await using var db = fixture.CreateContext();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller);
            var invoice = InvoiceTestIssues.Build(order, shipment, seller, taken);
            db.Invoices.Add(invoice);
            spoil(db, invoice);
            var pg = Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync()));
            pg.SqlState.Should().Be(PostgresErrorCodes.CheckViolation, check);
            pg.ConstraintName.Should().Be(check);
        }
    }

    [PostgresFact]
    public async Task AnInvoice_MustNameItsOwnOrdersSubOrderShipmentAndCompany()
    {
        var seller = await SellerAsync();
        var other = await SellerAsync();
        var first = await IssueAsync(seller);
        var taken = new TakenInvoiceNumber(seller, InvoiceSeries.TaxInvoice, first.FiscalYear, first.Serial + 1, DateTime.UtcNow);
        await using var db = fixture.CreateContext();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller);
        var (_, otherShipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller);
        var (otherOrder, _) = await InvoiceTestIssues.ShippedOrderAsync(db, other);
        Company company = await InvoiceTestIssues.ActiveCompanyAsync(db);
        foreach (var (why, spoil) in new (string, Action<Invoice>)[]
                 {
                     ("a shipment of another order", i => db.Entry(i).Property(x => x.ShipmentId).CurrentValue = otherShipment.Id),
                     ("a sub-order of another seller", i => db.Entry(i).Property(x => x.SubOrderId).CurrentValue = otherOrder.SubOrders.Single().Id),
                     ("a company that is not the order's", i => db.Entry(i).Property(x => x.CompanyId).CurrentValue = company.Id),
                     ("another order number", i => db.Entry(i).Property(x => x.OrderNumber).CurrentValue = otherOrder.OrderNumber)
                 })
        {
            var invoice = InvoiceTestIssues.Build(order, shipment, seller, taken);
            db.Invoices.Add(invoice);
            spoil(invoice);
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync())).SqlState.Should().Be(Violation, why);
            db.ChangeTracker.Clear();
        }
    }

    // ---- Migration AddInvoices: Up, Down, Up --------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Migration_AddInvoices_GoesUpDownAndUpAgain()
    {
        var name = "mw_e63b_" + Guid.NewGuid().ToString("N");
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
            (await ObjectsAsync(ctx)).Should().Be((2, 7, 4), "Up: two tables, 4 guards + 3 COMMIT checks, 4 functions");

            await ctx.GetService<IMigrator>().MigrateAsync(LastMigrationBefore);
            (await ObjectsAsync(ctx)).Should().Be((0, 0, 0), "Down removes the tables, the triggers (also the one on the counter) and the functions");
            (await ctx.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname LIKE 'trg_invoice_number_counters%'").ToListAsync())
                .Single().Should().Be(2, "the E6.2 counter guard stays");

            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            (await ObjectsAsync(ctx)).Should().Be((2, 7, 4), "Up again");
            await ctx.Database.ExecuteSqlRawAsync(InvoiceSql.Install); // a re-run changes nothing
            (await ObjectsAsync(ctx)).Should().Be((2, 7, 4));
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync("TRUNCATE invoices CASCADE"))).SqlState
                .Should().Be(Violation, "the migrated guard is live");
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

    private static async Task<(int Tables, int Triggers, int Functions)> ObjectsAsync(MechwareDbContext ctx)
    {
        var row = (await ctx.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM pg_tables WHERE tablename IN ('invoices', 'invoice_lines')) * 100
                  + (SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_invoices_guard', 'trg_invoices_no_truncate', 'trg_invoice_lines_guard',
                        'trg_invoice_lines_no_truncate', 'trg_invoices_commit_check', 'trg_invoice_lines_commit_check', 'trg_invoice_counter_step_check')) * 10
                  + (SELECT count(*) FROM pg_proc WHERE proname IN ('invoices_guard', 'invoice_lines_guard', 'invoice_counter_step_check', 'invoices_commit_check')))::int AS "Value"
            """).ToListAsync()).Single();
        return (row / 100, row / 10 % 10, row % 10);
    }
}
