using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>Threat model §১২ CS13, CS14, CS18 (backstop): what the database itself refuses (CreditLedgerSql + CHECKs).</summary>
public class CreditLedgerDatabaseTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 3_100_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
    private static readonly DateOnly Today = CreditCalendar.DhakaDate(DateTime.UtcNow);

    private async Task<(Guid Company, Guid User)> CompanyAsync(long limitMinor = 10_000_000)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var svc = new CompanyService(db);
        var id = (await svc.RegisterAsync(admin, new RegisterCompanyRequest("Ledger Mills Ltd", "TL-L-1", NextBin(), null, null))).Id;
        await svc.AdminActivateAsync(id);
        await svc.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return (id, admin);
    }

    /// <summary>A charge stored the way OrderService stores it: the line and the company in one transaction.</summary>
    private async Task<CreditLedgerEntry> ChargeAsync(Guid company, Guid user, long amountMinor)
    {
        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, user, company, amountMinor);
        var c = await db.Companies.SingleAsync(x => x.Id == company);
        var line = new CreditLedger(c, []).Charge(order, amountMinor, DateTime.UtcNow, DateTime.UtcNow).Entry;
        db.CreditLedgerEntries.Add(line);
        await db.SaveChangesAsync();
        return line;
    }

    private async Task<(long Used, long Seq, int Lines)> StateAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        var c = await db.Companies.AsNoTracking().SingleAsync(x => x.Id == company);
        return (c.CreditUsedMinor, c.CreditLedgerSeq, await db.CreditLedgerEntries.CountAsync(l => l.CompanyId == company));
    }

    private static PostgresException Pg(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException ?? throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL error, got {ex}");

    private static Task<int> InsertLineAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db, Guid company, long seq, string kind,
        Guid? order, long r, long a, long rAfter, long aAfter, DateOnly? dueOn = null, string? key = null, Guid? payment = null, Guid? adjustment = null) =>
        db.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, order_id, payment_id, adjustment_id,
                receivable_delta_minor, on_account_delta_minor, receivable_after_minor, on_account_after_minor, due_on, occurred_on,
                currency, idempotency_key, created_at)
            VALUES ({Guid.NewGuid()}, {company}, {seq}, {Guid.NewGuid()}, {kind}, {order}, {payment}, {adjustment}, {r}, {a}, {rAfter}, {aAfter},
                {dueOn}, {Today}, 'BDT', {key ?? "test:" + Guid.NewGuid().ToString("N")}, now())");

    [PostgresFact]
    public async Task CreditLedger_IsAppendOnly_UpdateDeleteAndTruncateFail()
    {
        var (company, user) = await CompanyAsync();
        var line = await ChargeAsync(company, user, 50_000);

        await using var db = fixture.CreateContext();
        foreach (var attempt in new Func<Task<int>>[]
                 {
                     () => db.Database.ExecuteSqlRawAsync("UPDATE credit_ledger_entries SET receivable_delta_minor = 1 WHERE id = {0}", line.Id),
                     () => db.Database.ExecuteSqlRawAsync("UPDATE credit_ledger_entries SET due_on = due_on + 60 WHERE id = {0}", line.Id),
                     () => db.Database.ExecuteSqlRawAsync("DELETE FROM credit_ledger_entries WHERE id = {0}", line.Id),
                     () => db.Database.ExecuteSqlRawAsync("TRUNCATE credit_ledger_entries")
                 })
            Pg(await Assert.ThrowsAnyAsync<Exception>(attempt)).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var stored = await db.CreditLedgerEntries.AsNoTracking().SingleAsync(l => l.Id == line.Id);
        (stored.ReceivableDeltaMinor, stored.DueOn).Should().Be((50_000, line.DueOn));
    }

    [PostgresFact]
    public async Task ChangingCreditUsed_WithoutALedgerEntry_FailsAtCommit()
    {
        var (company, user) = await CompanyAsync();
        await ChargeAsync(company, user, 50_000);

        // A raw write that forgets the ledger: the UPDATE itself is accepted, the COMMIT is not.
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            (await db.Database.ExecuteSqlRawAsync("UPDATE companies SET credit_used = credit_used - 10000 WHERE id = {0}", company)).Should().Be(1);
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync())).MessageText.Should().Contain($"company {company} has credit used 40000");
        }

        // A code path that moves the cached total through the domain but never stores the line.
        await using (var db = fixture.CreateContext())
        {
            var order = await CreditTestData.InsertOrderAsync(db, user, company, 20_000);
            await using var tx = await db.Database.BeginTransactionAsync();
            var c = await db.Companies.SingleAsync(x => x.Id == company);
            c.ChargeCredit(order, 20_000, DateTime.UtcNow, DateTime.UtcNow);
            await db.SaveChangesAsync();
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync())).SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
        }

        (await StateAsync(company)).Should().Be((50_000, 1, 1), "nothing of either attempt was kept");
    }

    [PostgresFact]
    public async Task ALedgerEntry_WithoutUpdatingTheCompany_FailsAtCommit()
    {
        var (company, user) = await CompanyAsync();
        await ChargeAsync(company, user, 50_000);

        await using (var db = fixture.CreateContext())
        {
            var order = await CreditTestData.InsertOrderAsync(db, user, company, 20_000);
            await using var tx = await db.Database.BeginTransactionAsync();
            var copy = await db.Companies.AsNoTracking().SingleAsync(x => x.Id == company);   // a copy EF will not save
            db.CreditLedgerEntries.Add(new CreditLedger(copy, []).Charge(order, 20_000, DateTime.UtcNow, DateTime.UtcNow).Entry);
            await db.SaveChangesAsync();
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync())).MessageText
                .Should().Contain("has credit used 50000, on account 0, line 1 but its ledger says 70000, 0, 2");
        }

        (await StateAsync(company)).Should().Be((50_000, 1, 1));
    }

    [PostgresFact]
    public async Task LedgerChain_WithAGapOrAWrongRunningTotal_FailsAtCommit()
    {
        var (company, user) = await CompanyAsync();
        await ChargeAsync(company, user, 50_000);
        Guid order;
        await using (var db = fixture.CreateContext()) order = await CreditTestData.InsertOrderAsync(db, user, company, 10_000);

        // Each attempt keeps the company equal to its own last line, so only the chain check can catch it.
        foreach (var (seq, after) in new[] { (3L, 60_000L), (2L, 70_000L) })   // a gap; a running total that is not 50,000 + 10,000
        {
            await using var db = fixture.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            await InsertLineAsync(db.Database, company, seq, "charge", order, 10_000, 0, after, 0, Today.AddDays(30));
            await db.Database.ExecuteSqlRawAsync("UPDATE companies SET credit_used = {0}, credit_ledger_seq = {1} WHERE id = {2}", after, seq, company);
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync())).MessageText.Should().Contain("does not continue the chain");
        }
        (await StateAsync(company)).Should().Be((50_000, 1, 1));

        // The control: the correct next line commits.
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await InsertLineAsync(db.Database, company, 2, "charge", order, 10_000, 0, 60_000, 0, Today.AddDays(30));
            await db.Database.ExecuteSqlRawAsync("UPDATE companies SET credit_used = 60000, credit_ledger_seq = 2 WHERE id = {0}", company);
            await tx.CommitAsync();
        }
        (await StateAsync(company)).Should().Be((60_000, 2, 2));
    }

    [PostgresFact]
    public async Task AChargeForTheSameOrder_CannotBeStoredTwice_EvenAsTheNextLine()
    {
        // CS18 backstop: an approval sweep that slipped past every check still meets the unique key charge:{orderId}.
        var (company, user) = await CompanyAsync();
        var line = await ChargeAsync(company, user, 50_000);

        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var ex = Pg(await Assert.ThrowsAnyAsync<Exception>(() =>
            InsertLineAsync(db.Database, company, 2, "charge", line.OrderId, 50_000, 0, 100_000, 0, Today.AddDays(30), key: line.IdempotencyKey)));
        (ex.SqlState, ex.ConstraintName).Should().Be((PostgresErrorCodes.UniqueViolation, "ux_credit_ledger_idempotency_key"));
    }

    [PostgresFact]
    public async Task TheDatabase_RefusesLinesThatBreakTheKindAndSignRules()
    {
        var (company, user) = await CompanyAsync();
        Guid order;
        await using (var db = fixture.CreateContext()) order = await CreditTestData.InsertOrderAsync(db, user, company, 10_000);

        var bad = new (string Why, Func<Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade, Task<int>> Insert)[]
        {
            ("an adjustment raising a debt", d => InsertLineAsync(d, company, 1, "write_off", order, 10_000, 0, 10_000, 0, adjustment: Guid.NewGuid())),
            ("a debt without an order", d => InsertLineAsync(d, company, 1, "payment_reversed", null, 10_000, 0, 10_000, 0, payment: Guid.NewGuid())),
            ("a negative charge", d => InsertLineAsync(d, company, 1, "charge", order, -10_000, 0, 0, 0, Today)),
            ("a charge without a due date", d => InsertLineAsync(d, company, 1, "charge", order, 10_000, 0, 10_000, 0)),
            ("a due date on a payment", d => InsertLineAsync(d, company, 1, "payment_on_account", null, 0, 5_000, 0, 5_000, Today, payment: Guid.NewGuid())),
            ("on-account money without its source", d => InsertLineAsync(d, company, 1, "payment_on_account", null, 0, 5_000, 0, 5_000)),
            ("a running total below zero", d => InsertLineAsync(d, company, 1, "payment_applied", order, -10_000, 0, -10_000, 0, payment: Guid.NewGuid())),
            ("an unknown kind", d => InsertLineAsync(d, company, 1, "gift", order, 10_000, 0, 10_000, 0)),
        };
        foreach (var (why, insert) in bad)
        {
            await using var db = fixture.CreateContext();
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => insert(db.Database))).SqlState.Should().Be(PostgresErrorCodes.CheckViolation, why);
        }
        (await StateAsync(company)).Should().Be((0, 0, 0));
    }
}
