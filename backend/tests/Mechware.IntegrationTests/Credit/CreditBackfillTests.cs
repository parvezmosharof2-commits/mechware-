using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// Threat model §১২ CS30: the B3.3 migration's one-time backfill (<see cref="CreditLedgerSql.Backfill"/>, the same SQL the
/// migration runs) over the state ReserveCredit left behind: <c>credit_used</c> moved, no ledger. Each test builds its own
/// database, because the backfill looks at every company in it.
/// </summary>
public class CreditBackfillTests
{
    private static long _binSeq = 3_200_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");

    private static async Task<Guid> CompanyAsync(PostgresDatabaseFixture fx, Guid admin, CreditTerms terms, long limitMinor)
    {
        await using var db = fx.CreateContext();
        var svc = new CompanyService(db);
        var id = (await svc.RegisterAsync(admin, new RegisterCompanyRequest("Backfill Mills Ltd", "TL-B-1", NextBin(), null, null))).Id;
        await svc.AdminActivateAsync(id);
        if (terms != CreditTerms.None) await svc.AdminSetCreditAsync(id, new SetCreditRequest(terms, limitMinor));
        return id;
    }

    /// <summary>Writes credit_used as the old ReserveCredit did, with the cached-total check off for this one transaction.</summary>
    private static async Task SetCreditUsedWithoutLedgerAsync(PostgresDatabaseFixture fx, params (Guid Company, long Used)[] companies)
    {
        await using var db = fx.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE companies DISABLE TRIGGER trg_companies_credit_check");
        foreach (var (company, used) in companies)
            await db.Database.ExecuteSqlRawAsync("UPDATE companies SET credit_used = {0} WHERE id = {1}", used, company);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE companies ENABLE TRIGGER trg_companies_credit_check");
        await tx.CommitAsync();
    }

    private static DateTime Utc(string s) => DateTime.SpecifyKind(DateTime.Parse(s), DateTimeKind.Utc);

    [PostgresFact]
    public async Task Backfill_ChargesEveryConfirmedCreditOrder_AndMatchesCreditUsed()
    {
        var fx = new PostgresDatabaseFixture();
        await fx.InitializeAsync();
        try
        {
            var user = await TestUsers.CreateAsync(fx);
            var a = await CompanyAsync(fx, user, CreditTerms.Net30, 10_000_000);
            var b = await CompanyAsync(fx, user, CreditTerms.Net15, 10_000_000);
            var none = await CompanyAsync(fx, user, CreditTerms.None, 0);
            Guid o1, o2, o3, b1;
            await using (var db = fx.CreateContext())
            {
                o1 = await CreditTestData.InsertOrderAsync(db, user, a, 100_000, confirmedAtUtc: Utc("2026-11-01T10:00:00"), placedAtUtc: Utc("2026-11-01T10:00:00"));
                // Confirmed at 00:30 in Dhaka on the 6th: still the 5th in UTC.
                o2 = await CreditTestData.InsertOrderAsync(db, user, a, 150_000, confirmedAtUtc: Utc("2026-11-05T18:30:00"), placedAtUtc: Utc("2026-11-05T18:20:00"));
                // Confirmed before E8.3 added confirmed_at: only its Confirmed event says so.
                o3 = await CreditTestData.InsertOrderAsync(db, user, a, 200_000, status: "Processing", placedAtUtc: Utc("2026-11-10T08:00:00"));
                await CreditTestData.AddEventAsync(db, o3, 1, "PendingPayment", "PendingPayment", Utc("2026-11-10T08:00:00"));
                await CreditTestData.AddEventAsync(db, o3, 2, "PendingPayment", "Confirmed", Utc("2026-11-10T08:01:00"));
                // Not charged: still waiting for approval, cancelled before confirmation, paid by card.
                await CreditTestData.InsertOrderAsync(db, user, a, 70_000, status: "PendingApproval");
                var cancelled = await CreditTestData.InsertOrderAsync(db, user, a, 90_000, status: "Cancelled");
                await CreditTestData.AddEventAsync(db, cancelled, 1, "PendingApproval", "Cancelled", DateTime.UtcNow);
                await CreditTestData.InsertOrderAsync(db, user, a, 80_000, onCredit: false, confirmedAtUtc: DateTime.UtcNow);
                b1 = await CreditTestData.InsertOrderAsync(db, user, b, 300_000, confirmedAtUtc: Utc("2026-11-02T05:00:00"));
            }
            await SetCreditUsedWithoutLedgerAsync(fx, (a, 450_000), (b, 300_000));

            await using (var db = fx.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync(CreditLedgerSql.Backfill);
                await db.Database.ExecuteSqlRawAsync(CreditLedgerSql.Backfill);   // a second run finds nothing to do
            }

            await using (var db = fx.CreateContext())
            {
                var lines = await db.CreditLedgerEntries.AsNoTracking().OrderBy(l => l.CompanyId == a ? 0 : 1).ThenBy(l => l.Seq).ToListAsync();
                lines.Select(l => (l.CompanyId, l.Seq, l.OrderId, l.ReceivableDeltaMinor, l.ReceivableAfterMinor, l.OccurredOn, l.DueOn)).Should().Equal(
                    (a, 1L, (Guid?)o1, 100_000L, 100_000L, new DateOnly(2026, 11, 1), (DateOnly?)new DateOnly(2026, 12, 1)),
                    (a, 2L, o2, 150_000L, 250_000L, new DateOnly(2026, 11, 6), new DateOnly(2026, 12, 6)),
                    (a, 3L, o3, 200_000L, 450_000L, new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 10)),
                    (b, 1L, b1, 300_000L, 300_000L, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 17)));
                lines.Should().OnlyContain(l => l.Kind == CreditLedgerKind.Charge && l.ActorUserId == null && l.OnAccountDeltaMinor == 0
                                                && l.IdempotencyKey == CreditLedgerKeys.Charge(l.OrderId!.Value), "keys a later charge would use, actor system");

                var companies = await db.Companies.AsNoTracking().Where(c => c.Id == a || c.Id == b || c.Id == none).ToDictionaryAsync(c => c.Id);
                (companies[a].CreditUsedMinor, companies[a].CreditLedgerSeq).Should().Be((450_000, 3));
                (companies[b].CreditUsedMinor, companies[b].CreditLedgerSeq).Should().Be((300_000, 1));
                (companies[none].CreditUsedMinor, companies[none].CreditLedgerSeq).Should().Be((0, 0));
            }

            // The book goes on from there: the next charge is line 4 and passes the commit check.
            await using (var db = fx.CreateContext())
            {
                var next = await CreditTestData.InsertOrderAsync(db, user, a, 50_000);
                var company = await db.Companies.SingleAsync(c => c.Id == a);
                db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(next, 50_000, DateTime.UtcNow, DateTime.UtcNow).Entry);
                await db.SaveChangesAsync();
                (company.CreditUsedMinor, company.CreditLedgerSeq).Should().Be((500_000, 4));
            }
        }
        finally { await fx.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Backfill_WithAMismatch_StopsAndNamesTheCompany()
    {
        var fx = new PostgresDatabaseFixture();
        await fx.InitializeAsync();
        try
        {
            var user = await TestUsers.CreateAsync(fx);
            var good = await CompanyAsync(fx, user, CreditTerms.Net30, 10_000_000);
            var tooHigh = await CompanyAsync(fx, user, CreditTerms.Net30, 10_000_000);
            var phantom = await CompanyAsync(fx, user, CreditTerms.Net30, 10_000_000);
            await using (var db = fx.CreateContext())
            {
                await CreditTestData.InsertOrderAsync(db, user, good, 100_000, confirmedAtUtc: DateTime.UtcNow);
                await CreditTestData.InsertOrderAsync(db, user, tooHigh, 300_000, confirmedAtUtc: DateTime.UtcNow);
            }
            // tooHigh says 500,000 for orders of 300,000; phantom says 1,000 with no order at all. Neither is invented away.
            await SetCreditUsedWithoutLedgerAsync(fx, (good, 100_000), (tooHigh, 500_000), (phantom, 1_000));

            await using (var db = fx.CreateContext())
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(CreditLedgerSql.Backfill));
                ex.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
                ex.MessageText.Should().StartWith("credit ledger backfill stopped").And.Contain(tooHigh.ToString()).And.Contain(phantom.ToString())
                    .And.NotContain(good.ToString());
            }

            await using (var check = fx.CreateContext())
            {
                (await check.CreditLedgerEntries.CountAsync()).Should().Be(0, "the stop keeps nothing, not even the good company's charge");
                (await check.Companies.AsNoTracking().Where(c => c.Id == tooHigh).Select(c => new { c.CreditUsedMinor, c.CreditLedgerSeq }).SingleAsync())
                    .Should().Be(new { CreditUsedMinor = 500_000L, CreditLedgerSeq = 0L });
            }
        }
        finally { await fx.DisposeAsync(); }
    }
}
