using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Companies;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;
using Mechware.Infrastructure.Workers;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Companies;

/// <summary>
/// Threat model v1, H4: BIN, TIN and trade-licence number are encrypted in the database and never copied in clear into the
/// audit log; the application still reads them, and BIN uniqueness still holds.
/// </summary>
public class CompanyFieldEncryptionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _seq = 5_000_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _seq) + 5_000_000_000_000L).ToString("D13");

    private async Task<(string Bin, string? Tin, string Licence)> RawAsync(Guid company)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT bin, tin, trade_licence_no FROM companies WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", company);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2));
    }

    [PostgresFact]
    public async Task TheDatabaseHoldsCiphertext_TheAppReadsThePlainValues()
    {
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        CompanyDto created;
        await using (var db = fixture.CreateContext())
            created = await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Cipher Mills Ltd", "TL-2026-77", bin, "123456789012", null));

        var raw = await RawAsync(created.Id);
        raw.Bin.Should().StartWith(FieldProtector.Prefix).And.NotContain(bin);
        raw.Tin.Should().StartWith(FieldProtector.Prefix).And.NotContain("123456789012");
        raw.Licence.Should().StartWith(FieldProtector.Prefix).And.NotContain("TL-2026-77");

        await using (var db = fixture.CreateContext())
        {
            var read = await new CompanyService(db).GetAsync(owner, created.Id);
            (read.Bin, read.Tin, read.TradeLicenceNo).Should().Be((bin, "123456789012", "TL-2026-77"));
        }
    }

    [PostgresFact]
    public async Task ALicenceNumberThatLooksEncrypted_IsStillEncrypted_AndReadsBack()
    {
        // Typed by a user: it must not be taken for ciphertext, stored in clear and then break every read of the company.
        var owner = await TestUsers.CreateAsync(fixture);
        const string sneaky = "enc:v2:00000000:AAAA";
        CompanyDto created;
        await using (var db = fixture.CreateContext())
            created = await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Sneaky Ltd", sneaky, NextBin(), null, null));

        (await RawAsync(created.Id)).Licence.Should().StartWith(FieldEncryption.Current.CurrentPrefix).And.NotBe(sneaky);
        await using (var db = fixture.CreateContext())
            (await new CompanyService(db).GetAsync(owner, created.Id)).TradeLicenceNo.Should().Be(sneaky);
    }

    [PostgresFact]
    public async Task TheAuditLog_DoesNotCopyTheIdentifiersInClear()
    {
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        CompanyDto created;
        await using (var db = fixture.CreateContext())
        {
            var options = new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(new AuditLogSaveChangesInterceptor(new FixedUser(owner))).Options;
            await using var audited = new MechwareDbContext(options);
            created = await new CompanyService(audited).RegisterAsync(owner, new RegisterCompanyRequest("Audit Mills Ltd", "TL-AUD-9", bin, "987654321098", null));
        }

        await using var check = fixture.CreateContext();
        var rows = await check.AuditLogs.Where(a => a.EntityId == created.Id.ToString()).Select(a => a.NewValuesJson).ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().AllSatisfy(json => json.Should().NotContain(bin).And.NotContain("987654321098").And.NotContain("TL-AUD-9"));

        // Nor the BIN's fingerprint: whoever reads the audit log could match it against the companies table (or a later
        // leak of the index key) and link entries to a BIN.
        string fingerprint = CompanyBinIndex.Of(bin);
        rows.Should().AllSatisfy(json => json.Should().NotContain(fingerprint));
    }

    [PostgresFact]
    public async Task RowsWrittenBeforeEncryption_StayReadable_AndTheBackfillEncryptsAndIndexesThem()
    {
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        Guid id;
        await using (var db = fixture.CreateContext())
            id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Legacy Ltd", "TL-OLD-1", bin, null, null))).Id;

        // As it was before this change: plain values, no fingerprint.
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE companies SET bin = @bin, trade_licence_no = 'TL-OLD-1', bin_index = NULL WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("bin", bin);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = fixture.CreateContext())
            (await new CompanyService(db).GetAsync(owner, id)).Bin.Should().Be(bin, "a value written before encryption reads as it is");

        await using (var db = fixture.CreateContext())
            (await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None)).Should().BeGreaterThan(0);

        var raw = await RawAsync(id);
        raw.Bin.Should().StartWith(FieldProtector.Prefix);
        raw.Licence.Should().StartWith(FieldProtector.Prefix);

        // The fingerprint is back, so the BIN is protected from a second registration again.
        var other = await TestUsers.CreateAsync(fixture);
        await using (var db = fixture.CreateContext())
        {
            Func<Task> dup = () => new CompanyService(db).RegisterAsync(other, new RegisterCompanyRequest("Copycat Ltd", "TL-X", bin, null, null));
            (await dup.Should().ThrowAsync<Mechware.Domain.Common.Exceptions.ConflictException>()).Which.Code.Should().Be("COMPANY_BIN_EXISTS");
        }
    }

    [PostgresFact]
    public async Task AfterAKeyRotation_RowsUnderTheOldKey_StayReadable_KeepTheirBin_AndTheBackfillMovesThemToTheNewKey()
    {
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        Guid id;
        await using (var db = fixture.CreateContext())
            id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Rotated Ltd", "TL-ROT-1", bin, "111122223333", null))).Id;

        // As written before the rotation: everything under what is now the previous key.
        var old = new FieldProtector(TestEncryption.PreviousKey);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE companies SET bin = @bin, tin = @tin, trade_licence_no = @lic, bin_index = @idx WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("bin", old.Protect(bin));
            cmd.Parameters.AddWithValue("tin", old.Protect("111122223333"));
            cmd.Parameters.AddWithValue("lic", old.Protect("TL-ROT-1"));
            cmd.Parameters.AddWithValue("idx", old.BlindIndex(bin));
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var read = await new CompanyService(db).GetAsync(owner, id);
            (read.Bin, read.Tin, read.TradeLicenceNo).Should().Be((bin, "111122223333", "TL-ROT-1"));
        }

        // Its BIN is still taken, although its fingerprint is under the old key.
        var other = await TestUsers.CreateAsync(fixture);
        await using (var db = fixture.CreateContext())
        {
            Func<Task> dup = () => new CompanyService(db).RegisterAsync(other, new RegisterCompanyRequest("Copycat Ltd", "TL-Y", bin, null, null));
            (await dup.Should().ThrowAsync<Mechware.Domain.Common.Exceptions.ConflictException>()).Which.Code.Should().Be("COMPANY_BIN_EXISTS");
        }

        await using (var db = fixture.CreateContext())
            (await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None)).Should().BeGreaterThan(0);

        var raw = await RawAsync(id);
        var current = FieldEncryption.Current;
        raw.Bin.Should().StartWith(current.CurrentPrefix);
        raw.Tin.Should().StartWith(current.CurrentPrefix);
        raw.Licence.Should().StartWith(current.CurrentPrefix);
        new FieldProtector(TestEncryption.Key).Unprotect(raw.Bin).Should().Be(bin, "the old key is no longer needed for this row");
        await using (var db = fixture.CreateContext())
            (await db.Companies.AsNoTracking().Where(c => c.Id == id).Select(c => EF.Property<string>(c, CompanyBinIndex.Property)).SingleAsync())
                .Should().Be(current.BlindIndex(bin));
    }

    [PostgresFact]
    public async Task TheBackfill_StopsInsteadOfLooping_WhenARowCannotBeRewritten()
    {
        var owner = await TestUsers.CreateAsync(fixture);
        Guid id;
        await using (var db = fixture.CreateContext())
            id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Stuck Ltd", "TL-STUCK", NextBin(), null, null))).Id;

        // The row reads as written before encryption, and a trigger (this row only) undoes every rewrite of it.
        string trigger = "stuck_" + id.ToString("N")[..12];
        await ExecAsync($"""
            UPDATE companies SET trade_licence_no = 'TL-STUCK' WHERE id = '{id}';
            CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN NEW.trade_licence_no := OLD.trade_licence_no; RETURN NEW; END $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE ON companies FOR EACH ROW WHEN (OLD.id = '{id}') EXECUTE FUNCTION {trigger}();
            """);
        try
        {
            await using var db = fixture.CreateContext();
            var run = () => FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None);
            (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*could not be re-encrypted*");
        }
        finally
        {
            await ExecAsync($"DROP TRIGGER {trigger} ON companies; DROP FUNCTION {trigger}(); " +
                            $"DELETE FROM company_members WHERE company_id = '{id}'; DELETE FROM companies WHERE id = '{id}';");
        }
    }

    [PostgresFact]
    public async Task OneStuckRow_DoesNotHoldBackTheOthers()
    {
        // Two rows written before encryption; the one that comes first (lower id) cannot be rewritten.
        var owner = await TestUsers.CreateAsync(fixture);
        var ids = new List<Guid>();
        for (int i = 0; i < 2; i++)
            await using (var db = fixture.CreateContext())
                ids.Add((await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Queue Ltd " + i, "TL-Q" + i, NextBin(), null, null))).Id);
        ids.Sort();
        var (stuck, waiting) = (ids[0], ids[1]);
        string trigger = "stuck_" + stuck.ToString("N")[..12];
        await ExecAsync($"""
            UPDATE companies SET trade_licence_no = 'TL-PLAIN' WHERE id IN ('{stuck}', '{waiting}');
            CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN NEW.trade_licence_no := OLD.trade_licence_no; RETURN NEW; END $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE ON companies FOR EACH ROW WHEN (OLD.id = '{stuck}') EXECUTE FUNCTION {trigger}();
            """);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                // One row per batch, so the stuck row is met before the other one.
                var run = () => FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None, batchSize: 1);
                (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*could not be re-encrypted*");
            }
            (await RawAsync(waiting)).Licence.Should().StartWith(FieldEncryption.Current.CurrentPrefix, "the stuck row must not hold the others back");
        }
        finally
        {
            await ExecAsync($"DROP TRIGGER {trigger} ON companies; DROP FUNCTION {trigger}(); " +
                            $"DELETE FROM company_members WHERE company_id IN ('{stuck}', '{waiting}'); DELETE FROM companies WHERE id IN ('{stuck}', '{waiting}');");
        }
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FixedUser(Guid id) : Mechware.Application.Identity.ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.1";
        public string? UserAgent => null;
        public string? CorrelationId => null;
        public bool HasPermission(string permission) => false;
    }

    [PostgresFact]
    public async Task ARowMissingItsFingerprint_GetsItOnTheNextSave_EvenWhenTheBinIsUnchanged()
    {
        // Without a fingerprint the BIN is invisible to the duplicate check; any save of the row must restore it, not only
        // the start-up backfill.
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        Guid id;
        await using (var db = fixture.CreateContext())
            id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Gap Ltd", "TL-GAP-1", bin, null, null))).Id;
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET bin_index = NULL WHERE id = {id}");

        await using (var db = fixture.CreateContext())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == id);
            db.Entry(company).Property(c => c.LegalName).IsModified = true;   // some other change; the BIN is untouched
            await db.SaveChangesAsync();
        }

        var other = await TestUsers.CreateAsync(fixture);
        await using (var db = fixture.CreateContext())
        {
            Func<Task> dup = () => new CompanyService(db).RegisterAsync(other, new RegisterCompanyRequest("Copycat Ltd", "TL-Y", bin, null, null));
            (await dup.Should().ThrowAsync<Mechware.Domain.Common.Exceptions.ConflictException>()).Which.Code.Should().Be("COMPANY_BIN_EXISTS");
        }
    }

    [PostgresFact]
    public async Task TheBackfill_RestoresAMissingFingerprint_OnARowThatIsAlreadyEncrypted()
    {
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        Guid id;
        await using (var db = fixture.CreateContext())
            id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Index Gap Ltd", "TL-IG-1", bin, null, null))).Id;
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET bin_index = NULL WHERE id = {id}");
        (await RawAsync(id)).Bin.Should().StartWith(FieldEncryption.Current.CurrentPrefix, "only the fingerprint is missing");

        await using (var db = fixture.CreateContext())
            await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None);

        await using var check = fixture.CreateContext();
        (await check.Companies.Where(c => c.Id == id).Select(c => EF.Property<string?>(c, CompanyBinIndex.Property)).SingleAsync())
            .Should().Be(CompanyBinIndex.Of(bin));
    }

    [PostgresFact]
    public async Task TheBackfill_KeepsGoingPastTheFirstBatch()
    {
        var ids = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var owner = await TestUsers.CreateAsync(fixture);   // one account may hold at most 2 pending registrations
            await using var db = fixture.CreateContext();
            ids.Add((await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest($"Batch {i} Ltd", $"TL-B-{i}", NextBin(), null, null))).Id);
        }
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET bin_index = NULL WHERE id = ANY({ids.ToArray()})");

        await using (var db = fixture.CreateContext())
            (await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None, batchSize: 2)).Should().BeGreaterThanOrEqualTo(3);

        await using var check = fixture.CreateContext();
        (await check.Companies.CountAsync(c => ids.Contains(c.Id) && EF.Property<string?>(c, CompanyBinIndex.Property) == null)).Should().Be(0);
    }

    [PostgresTheory]
    [InlineData("tin")]
    [InlineData("trade_licence_no")]
    public async Task TheBackfill_FindsARow_WhenOnlyOneIdentifierIsStillInClear(string column)
    {
        // Each column is checked on its own: a row whose BIN is already encrypted can still hold a clear TIN or licence.
        var owner = await TestUsers.CreateAsync(fixture);
        Guid id;
        await using (var db = fixture.CreateContext())
            id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("One Column Ltd", "TL-OC-1", NextBin(), "123456789012", null))).Id;
        string clear = column == "tin" ? "123456789012" : "TL-OC-1";
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand($"UPDATE companies SET {column} = @v WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("v", clear);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = fixture.CreateContext())
            await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None);

        var raw = await RawAsync(id);
        (column == "tin" ? raw.Tin : raw.Licence).Should().StartWith(FieldEncryption.Current.CurrentPrefix).And.NotContain(clear);
        await using var check = fixture.CreateContext();
        var company = await new CompanyService(check).GetAsync(owner, id);
        (company.Tin, company.TradeLicenceNo).Should().Be(("123456789012", "TL-OC-1"));
    }
}
