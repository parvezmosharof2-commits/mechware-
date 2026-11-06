using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Application.Companies;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Security;
using ShopVerse.Infrastructure.Workers;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Companies;

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
            var options = new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(new AuditLogSaveChangesInterceptor(new FixedUser(owner))).Options;
            await using var audited = new ShopVerseDbContext(options);
            created = await new CompanyService(audited).RegisterAsync(owner, new RegisterCompanyRequest("Audit Mills Ltd", "TL-AUD-9", bin, "987654321098", null));
        }

        await using var check = fixture.CreateContext();
        var rows = await check.AuditLogs.Where(a => a.EntityId == created.Id.ToString()).Select(a => a.NewValuesJson).ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().AllSatisfy(json => json.Should().NotContain(bin).And.NotContain("987654321098").And.NotContain("TL-AUD-9"));
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
            (await dup.Should().ThrowAsync<ShopVerse.Domain.Common.Exceptions.ConflictException>()).Which.Code.Should().Be("COMPANY_BIN_EXISTS");
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
            (await dup.Should().ThrowAsync<ShopVerse.Domain.Common.Exceptions.ConflictException>()).Which.Code.Should().Be("COMPANY_BIN_EXISTS");
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

    private async Task ExecAsync(string sql, Guid id, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", id);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> BinIndexAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Companies.AsNoTracking().Where(c => c.Id == id).Select(c => EF.Property<string?>(c, CompanyBinIndex.Property)).SingleAsync();
    }

    private async Task<(Guid Owner, Guid Id, string Bin)> RegisterAsync(string name, string? tin = null)
    {
        var owner = await TestUsers.CreateAsync(fixture);
        string bin = NextBin();
        await using var db = fixture.CreateContext();
        var id = (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest(name, "TL-" + bin[^6..], bin, tin, null))).Id;
        return (owner, id, bin);
    }

    [PostgresFact]
    public async Task AMissingFingerprint_IsRestoredByAnySave_EvenOneThatDoesNotTouchTheBin()
    {
        // Between deploying encryption and the backfill finishing, ordinary saves (an account manager assigned, a status
        // change) must not leave the BIN without a fingerprint, or it would be open to a second registration.
        var (owner, id, bin) = await RegisterAsync("Unindexed Ltd");
        await ExecAsync("UPDATE companies SET bin_index = NULL WHERE id = @id", id);

        await using (var db = fixture.CreateContext())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == id);
            company.AssignAccountManager(owner);
            await db.SaveChangesAsync();
        }

        (await BinIndexAsync(id)).Should().Be(FieldEncryption.Current.BlindIndex(bin));
    }

    [PostgresFact]
    public async Task TheBackfill_RestoresAMissingFingerprint_OnARowWhoseValuesAreAlreadyCurrent()
    {
        var (_, id, bin) = await RegisterAsync("Fingerprintless Ltd");
        await ExecAsync("UPDATE companies SET bin_index = NULL WHERE id = @id", id);

        await using (var db = fixture.CreateContext())
            await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None);

        (await BinIndexAsync(id)).Should().Be(FieldEncryption.Current.BlindIndex(bin));
    }

    [PostgresFact]
    public async Task TheBackfill_EncryptsAPlainTin_EvenWhenTheBinAndLicenceAreAlreadyCurrent()
    {
        var (owner, id, _) = await RegisterAsync("Plain Tin Ltd", "444455556666");
        await ExecAsync("UPDATE companies SET tin = '444455556666' WHERE id = @id", id);

        await using (var db = fixture.CreateContext())
            await FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None);

        (await RawAsync(id)).Tin.Should().StartWith(FieldEncryption.Current.CurrentPrefix).And.NotContain("444455556666");
        await using (var db = fixture.CreateContext())
            (await new CompanyService(db).GetAsync(owner, id)).Tin.Should().Be("444455556666");
    }

    [PostgresFact]
    public async Task TheBackfill_Stops_WhenARowDoesNotReachTheCurrentKey_InsteadOfLoopingForEver()
    {
        // A trigger that undoes the encryption of this one row stands in for any defect that keeps a save from sticking.
        var (_, id, _) = await RegisterAsync("Stubborn Ltd", "777788889999");
        string fn = $"keep_tin_plain_{id:N}";
        await ExecAsync($"""
            CREATE FUNCTION {fn}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.tin := '777788889999'; RETURN NEW; END $$;
            CREATE TRIGGER {fn} BEFORE UPDATE ON companies FOR EACH ROW WHEN (NEW.id = '{id}') EXECUTE FUNCTION {fn}();
            UPDATE companies SET tin = '777788889999' WHERE id = @id;
            """, id);
        try
        {
            await using var db = fixture.CreateContext();
            Func<Task> run = () => FieldEncryptionBackfillWorker.RunAsync(db, CancellationToken.None);
            (await run.Should().ThrowAsync<InvalidOperationException>().WaitAsync(TimeSpan.FromSeconds(30)))
                .WithMessage($"*{id}*");
        }
        finally
        {
            await ExecAsync($"DROP TRIGGER {fn} ON companies; DROP FUNCTION {fn}(); UPDATE companies SET tin = NULL WHERE id = @id;", id);
        }
    }

    private sealed class FixedUser(Guid id) : ShopVerse.Application.Identity.ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.1";
        public string? UserAgent => null;
        public string? CorrelationId => null;
        public bool HasPermission(string permission) => false;
    }
}
