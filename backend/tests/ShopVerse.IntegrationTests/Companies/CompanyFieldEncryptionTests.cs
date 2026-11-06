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
