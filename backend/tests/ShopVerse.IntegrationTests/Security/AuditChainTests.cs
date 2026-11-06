using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Security;

/// <summary>
/// Threat model v1, H3 (brief §6.11): the audit log is append-only for the application, and sealed rows form a hash chain,
/// so even someone who can bypass the trigger (a database owner) cannot change, remove or reorder history unnoticed.
/// Each test gets its own database, because the chain is global.
/// </summary>
public sealed class AuditChainTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private async Task<List<Guid>> WriteAsync(int count)
    {
        await using var db = _db.CreateContext();
        var ids = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            db.AuditLogs.Add(new AuditLog(id, Guid.NewGuid(), "203.0.113.7", "company.credit_changed", "companies", Guid.NewGuid().ToString(),
                "{\"limit\":0}", $"{{\"limit\":{(i + 1) * 1000}}}", null, DateTime.UtcNow.AddMinutes(-5).AddMilliseconds(i)));
            ids.Add(id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<T> Chain<T>(Func<AuditChain, Task<T>> act)
    {
        await using var db = _db.CreateContext();
        return await act(new AuditChain(db));
    }

    /// <summary>Plain SQL as written (no format placeholders, so JSON braces are fine).</summary>
    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task SealedRows_FormAnIntactChain_AndSealingContinuesIt()
    {
        await WriteAsync(3);
        (await Chain(c => c.SealAsync(100, TimeSpan.Zero))).Should().Be(3);
        await WriteAsync(2);
        (await Chain(c => c.SealAsync(100, TimeSpan.Zero))).Should().Be(2);

        var status = await Chain(c => c.VerifyAsync());
        status.Intact.Should().BeTrue();
        status.Sealed.Should().Be(5);
        status.Unsealed.Should().Be(0);
        status.HeadHash.Should().MatchRegex("^[0-9a-f]{64}$");
        (await Chain(c => c.HeadAsync())).Should().Be((5L, status.HeadHash), "the cheap head lookup agrees with the full check");
    }

    [PostgresFact]
    public async Task RowsYoungerThanTheSettleTime_WaitForTheNextRun()
    {
        await using (var db = _db.CreateContext())
        {
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), null, null, "x", "y", "z", null, null, null, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        (await Chain(c => c.SealAsync(100, TimeSpan.FromMinutes(1)))).Should().Be(0, "it may belong to a transaction that has not committed yet");
        (await Chain(c => c.VerifyAsync())).Unsealed.Should().Be(1);
    }

    [PostgresTheory]
    [InlineData("UPDATE audit_logs SET new_values = '{\"limit\":999999}' WHERE chain_position IS NULL", "audit rows cannot change")]
    [InlineData("UPDATE audit_logs SET action = 'nothing.happened' WHERE chain_position IS NULL", "audit rows cannot change")]
    [InlineData("UPDATE audit_logs SET action = 'nothing.happened' WHERE chain_position IS NOT NULL", "a sealed audit row cannot change")]
    [InlineData("DELETE FROM audit_logs WHERE chain_position IS NULL", "audit_logs is append-only")]
    [InlineData("DELETE FROM audit_logs WHERE chain_position IS NOT NULL", "audit_logs is append-only")]
    [InlineData("TRUNCATE audit_logs", "audit_logs is append-only")]
    public async Task TheApplicationCannotChangeOrRemoveAuditRows_SealedOrNot(string sql, string refusal)
    {
        await WriteAsync(2);
        await Chain(c => c.SealAsync(1, TimeSpan.Zero));   // one sealed, one not

        Func<Task> act = () => SqlAsync(sql);
        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be(refusal);
    }

    [PostgresFact]
    public async Task ASealedRowCannotBeResealed()
    {
        await WriteAsync(1);
        await Chain(c => c.SealAsync(10, TimeSpan.Zero));
        Func<Task> act = () => SqlAsync("UPDATE audit_logs SET hash = 'forged', prev_hash = 'forged'");
        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("sealed");
    }

    /// <summary>A database owner can switch the trigger off; the chain still gives them away.</summary>
    [PostgresTheory]
    [InlineData("UPDATE audit_logs SET new_values = '{\"limit\":1}' WHERE chain_position = 3", 3)]
    [InlineData("DELETE FROM audit_logs WHERE chain_position = 2", 2)]
    [InlineData("UPDATE audit_logs SET created_at = created_at - interval '1 day' WHERE chain_position = 4", 4)]
    [InlineData("UPDATE audit_logs SET chain_position = 99 WHERE chain_position = 5", 5)]
    public async Task TamperingBehindTheTriggersBack_IsDetected_WhereItHappened(string tamper, long expectedBreak)
    {
        await WriteAsync(5);
        await Chain(c => c.SealAsync(100, TimeSpan.Zero));
        (await Chain(c => c.VerifyAsync())).Intact.Should().BeTrue();

        await SqlAsync($"ALTER TABLE audit_logs DISABLE TRIGGER USER; {tamper}; ALTER TABLE audit_logs ENABLE TRIGGER USER;");

        var status = await Chain(c => c.VerifyAsync());
        status.Intact.Should().BeFalse();
        status.FirstBrokenPosition.Should().Be(expectedBreak);
    }

    [PostgresFact]
    public async Task RewritingTheWholeTail_ChangesTheHead_SoTheLoggedAnchorNoLongerMatches()
    {
        await WriteAsync(3);
        await Chain(c => c.SealAsync(100, TimeSpan.Zero));
        string anchoredHead = (await Chain(c => c.VerifyAsync())).HeadHash!;

        // An owner rewrites the last row and recomputes its hash consistently: the chain verifies again,
        // but its head is no longer the one the worker already logged outside the database.
        await SqlAsync("""
            ALTER TABLE audit_logs DISABLE TRIGGER USER;
            UPDATE audit_logs SET new_values = '{"limit":1}' WHERE chain_position = 3;
            UPDATE audit_logs a SET hash = audit_row_hash(a.prev_hash, a) WHERE chain_position = 3;
            ALTER TABLE audit_logs ENABLE TRIGGER USER;
            """);

        var status = await Chain(c => c.VerifyAsync());
        status.Intact.Should().BeTrue();
        status.HeadHash.Should().NotBe(anchoredHead, "the external anchor is what catches a consistent rewrite");
    }

    [PostgresFact]
    public async Task ConcurrentSealers_NeverDuplicateOrSkipAPosition()
    {
        await WriteAsync(200);
        var counts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Chain(c => c.SealAsync(40, TimeSpan.Zero)))));
        counts.Sum().Should().Be(200);

        var status = await Chain(c => c.VerifyAsync());
        status.Intact.Should().BeTrue();
        status.Sealed.Should().Be(200);
    }

    [PostgresFact]
    public async Task TheHashDoesNotDependOnTheSessionTimeZone()
    {
        await WriteAsync(2);
        await SqlAsync("SET TIME ZONE 'Asia/Dhaka'; SELECT seal_audit_logs(100, interval '0');");
        await using var db = _db.CreateContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'America/Los_Angeles'");
        (await new AuditChain(db).VerifyAsync()).Intact.Should().BeTrue();
    }
}
