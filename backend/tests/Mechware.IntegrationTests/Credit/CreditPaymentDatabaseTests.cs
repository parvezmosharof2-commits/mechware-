using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// Threat model §১২ CS2, CS4, CS20 (database half) and B3.3b: what the database itself refuses for payments and adjustments
/// (CHECKs, partial unique indexes, the step-by-step guard of <c>CreditLedgerSql.InstallSettlement</c>, the ledger's new
/// foreign keys), and a verification's planned lines committing under the ledger's own checks.
/// </summary>
public class CreditPaymentDatabaseTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 3_500_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
    private static readonly CreditPaymentRules Rules = new CreditOptions().PaymentRules();
    private static readonly string Hash = new('b', 64);
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Ref(string prefix = "TRX") => prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private async Task<Guid> CompanyAsync(long limitMinor = 10_000_000)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var svc = new CompanyService(db);
        var id = (await svc.RegisterAsync(admin, new RegisterCompanyRequest("Payment Mills Ltd", "TL-P-1", NextBin(), null, null))).Id;
        await svc.AdminActivateAsync(id);
        await svc.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return id;
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private static CreditPaymentInput Input(Guid company, CreditPaymentMethod method, string? reference, string? bank = null, long amount = 10_000) =>
        new(company, method, amount, reference, bank, Today, method is CreditPaymentMethod.Cheque or CreditPaymentMethod.PayOrder ? Today : null);

    private async Task<CreditPayment> RecordAsync(CreditPaymentInput input, Guid recorder, string? key = null)
    {
        await using var db = fixture.CreateContext();
        var payment = CreditPayment.Record(Guid.NewGuid(), input, recorder, key ?? "key-" + Guid.NewGuid().ToString("N"), Hash, Rules, DateTime.UtcNow);
        db.CreditPayments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private async Task ChangeAsync(Guid paymentId, Action<CreditPayment> change)
    {
        await using var db = fixture.CreateContext();
        change(await db.CreditPayments.SingleAsync(p => p.Id == paymentId));
        await db.SaveChangesAsync();
    }

    private static PostgresException Pg(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException ?? throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL error, got {ex}");

    private static async Task<PostgresException> RefusedAsync(Func<Task> act) => Pg(await Assert.ThrowsAnyAsync<Exception>(act));

    private async Task<PostgresException> SqlRefusedAsync(string sql, params object[] args)
    {
        await using var db = fixture.CreateContext();
        return await RefusedAsync(() => db.Database.ExecuteSqlRawAsync(sql, args));
    }

    private static Task<int> InsertPaymentAsync(DatabaseFacade db, Guid company, Guid recorder, string method = "beftn", long amount = 10_000,
        string? reference = null, string? bank = null, bool chequeDate = false, string status = "recorded", string currency = "BDT", string allocations = "[]") =>
        db.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO credit_payments (id, company_id, method, amount_minor, currency, reference, drawer_bank, received_on, cheque_date,
                allocations, status, recorded_by, idempotency_key, request_hash, created_at)
            VALUES ({Guid.NewGuid()}, {company}, {method}, {amount}, {currency}, {reference ?? Ref()}, {bank}, {Today}, {(chequeDate ? Today : null)},
                {allocations}::jsonb, {status}, {recorder}, {"key-" + Guid.NewGuid().ToString("N")}, {Hash}, now())");

    [PostgresFact]
    public async Task VerifiedByEqualsRecordedBy_FailsAtDbLevel()
    {
        // The domain refuses it first (CreditPaymentTests.Verify_ByTheRecorder_IsRefused); a code path that skips the domain
        // still meets the CHECK.
        var company = await CompanyAsync();
        var recorder = await UserAsync();
        var payment = await RecordAsync(Input(company, CreditPaymentMethod.Beftn, Ref()), recorder);

        foreach (var sql in new[]
                 {
                     "UPDATE credit_payments SET status = 'verified', verified_by = recorded_by, verified_at = now(), cleared_on = received_on WHERE id = {0}",
                     "UPDATE credit_payments SET status = 'rejected', rejected_by = recorded_by, rejected_at = now(), decision_reason = 'mine' WHERE id = {0}"
                 })
        {
            var ex = await SqlRefusedAsync(sql, payment.Id);
            (ex.SqlState, ex.ConstraintName).Should().Be((PostgresErrorCodes.CheckViolation, "ck_credit_payments_four_eyes"));
        }
        // The normal path: the domain refuses; were its check ever lost, the save would meet the CHECK. Never stored either way.
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => ChangeAsync(payment.Id, p => p.Verify(recorder, Today, DateTime.UtcNow)));
        (thrown is ConflictException { Code: "CREDIT_PAYMENT_FOUR_EYES" }
            || thrown.InnerException is PostgresException { ConstraintName: "ck_credit_payments_four_eyes" }).Should().BeTrue($"got {thrown}");

        var verifier = await UserAsync();
        await ChangeAsync(payment.Id, p => p.Verify(verifier, Today, DateTime.UtcNow));
        await using var check = fixture.CreateContext();
        (await check.CreditPayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Should()
            .BeEquivalentTo(new { Status = CreditPaymentStatus.Verified, VerifiedBy = (Guid?)verifier, RecordedBy = recorder, ClearedOn = (DateOnly?)Today });
    }

    [PostgresFact]
    public async Task ApprovedByEqualsRequestedBy_FailsAtDbLevel()
    {
        var company = await CompanyAsync();
        var requester = await UserAsync();
        var adjustment = CreditAdjustment.Request(Guid.NewGuid(), new CreditAdjustmentInput(company, CreditAdjustmentKind.OnAccountCorrection, -500,
            "mis-posted on-account amount", CreditNoteId: Guid.NewGuid()), requester, Rules.MaxPaymentMinor, DateTime.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.CreditAdjustments.Add(adjustment);
            await db.SaveChangesAsync();
        }

        var ex = await SqlRefusedAsync(
            "UPDATE credit_adjustments SET status = 'approved', approved_by = requested_by, approved_at = now() WHERE id = {0}", adjustment.Id);
        (ex.SqlState, ex.ConstraintName).Should().Be((PostgresErrorCodes.CheckViolation, "ck_credit_adjustments_four_eyes"));

        var approver = await UserAsync();
        await using (var db = fixture.CreateContext())
        {
            (await db.CreditAdjustments.SingleAsync(a => a.Id == adjustment.Id)).Approve(approver, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        await using var check = fixture.CreateContext();
        (await check.CreditAdjustments.AsNoTracking().SingleAsync(a => a.Id == adjustment.Id)).ApprovedBy.Should().Be(approver);
    }

    private static readonly CreditPaymentMethod[] TransferMethods =
        [CreditPaymentMethod.Beftn, CreditPaymentMethod.Rtgs, CreditPaymentMethod.Npsb, CreditPaymentMethod.Bkash, CreditPaymentMethod.Nagad];

    [PostgresFact]
    public async Task TheSameBankReference_CannotBeRecordedTwice_EvenForAnotherCompany()
    {
        var (a, b) = (await CompanyAsync(), await CompanyAsync());
        var (recorder, other) = (await UserAsync(), await UserAsync());
        foreach (var method in TransferMethods)
        {
            var reference = Ref();
            await RecordAsync(Input(a, method, reference), recorder);
            var anotherMethod = TransferMethods[(Array.IndexOf(TransferMethods, method) + 1) % TransferMethods.Length];
            foreach (var (company, m) in new[] { (b, method), (a, method), (b, anotherMethod) })
            {
                var ex = await RefusedAsync(() => RecordAsync(Input(company, m, reference), other));
                (ex.SqlState, ex.ConstraintName).Should().Be((PostgresErrorCodes.UniqueViolation, "ux_credit_payments_transfer_reference"), $"{method} then {m}");
            }
        }
    }

    [PostgresFact]
    public async Task TheSameBankReference_DifferentlyWritten_IsADuplicate()
    {
        var (a, b) = (await CompanyAsync(), await CompanyAsync());
        var recorder = await UserAsync();
        var tail = Guid.NewGuid().ToString("N")[..8];
        await RecordAsync(Input(a, CreditPaymentMethod.Beftn, $"ab-123-{tail}"), recorder);
        (await RefusedAsync(() => RecordAsync(Input(b, CreditPaymentMethod.Beftn, $"AB 123 {tail.ToUpperInvariant()}"), recorder)))
            .ConstraintName.Should().Be("ux_credit_payments_transfer_reference");

        await RecordAsync(Input(a, CreditPaymentMethod.Cheque, $"chq-{tail}", "Dutch-Bangla Bank Ltd."), recorder);
        (await RefusedAsync(() => RecordAsync(Input(a, CreditPaymentMethod.Cheque, $"CHQ {tail}", "DUTCH BANGLA BANK LTD"), recorder)))
            .ConstraintName.Should().Be("ux_credit_payments_instrument_reference");
    }

    [PostgresFact]
    public async Task TheSameChequeNumber_FromAnotherBank_IsAllowed()
    {
        var (a, b) = (await CompanyAsync(), await CompanyAsync());
        var recorder = await UserAsync();
        var number = Ref("00");
        await RecordAsync(Input(a, CreditPaymentMethod.Cheque, number, "Dutch-Bangla Bank"), recorder);
        await RecordAsync(Input(a, CreditPaymentMethod.Cheque, number, "BRAC Bank"), recorder);           // another bank's cheque book
        await RecordAsync(Input(b, CreditPaymentMethod.Cheque, number, "Dutch-Bangla Bank"), recorder);   // another company's cheque

        // The same instrument again, even under another instrument method (a pay order recorded once as a cheque).
        foreach (var method in new[] { CreditPaymentMethod.Cheque, CreditPaymentMethod.PayOrder, CreditPaymentMethod.BankDeposit })
            (await RefusedAsync(() => RecordAsync(Input(a, method, number, "Dutch Bangla Bank"), recorder)))
                .ConstraintName.Should().Be("ux_credit_payments_instrument_reference", method.ToString());
        await RecordAsync(Input(a, CreditPaymentMethod.Beftn, number), recorder);   // a transfer id lives in the other key
    }

    [PostgresFact]
    public async Task ARejectedPayment_FreesItsReference()
    {
        var company = await CompanyAsync();
        var (recorder, checker) = (await UserAsync(), await UserAsync());
        foreach (var (method, bank) in new[] { (CreditPaymentMethod.Npsb, (string?)null), (CreditPaymentMethod.PayOrder, "Sonali Bank") })
        {
            var reference = Ref();
            var wrong = await RecordAsync(Input(company, method, reference, bank, amount: 99_999), recorder);
            await ChangeAsync(wrong.Id, p => p.Reject(checker, "amount typed wrong; re-recorded", DateTime.UtcNow));

            var right = await RecordAsync(Input(company, method, reference, bank), recorder);
            right.Reference.Should().Be(reference);
            (await RefusedAsync(() => RecordAsync(Input(company, method, reference, bank), recorder))).SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
    }

    [PostgresFact]
    public async Task TheSameIdempotencyKey_ByTheSameRecorder_IsRefused()
    {
        var company = await CompanyAsync();
        var (recorder, other) = (await UserAsync(), await UserAsync());
        await RecordAsync(Input(company, CreditPaymentMethod.Rtgs, Ref()), recorder, key: "pay-key-0001");
        (await RefusedAsync(() => RecordAsync(Input(company, CreditPaymentMethod.Rtgs, Ref()), recorder, key: "pay-key-0001")))
            .ConstraintName.Should().Be("ux_credit_payments_idempotency");
        await RecordAsync(Input(company, CreditPaymentMethod.Rtgs, Ref()), other, key: "pay-key-0001");
    }

    [PostgresFact]
    public async Task APayment_ChangesOnlyByItsNextStep_AndIsNeverDeleted()
    {
        var company = await CompanyAsync();
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        await using (var db = fixture.CreateContext())
            (await RefusedAsync(() => InsertPaymentAsync(db.Database, company, recorder, status: "verified"))).MessageText.Should().Contain("created as recorded");

        var payment = await RecordAsync(Input(company, CreditPaymentMethod.Cheque, Ref("CHQ"), "Pubali Bank") with
        {
            Allocations = [new(Guid.NewGuid(), 5_000)],
            Note = "handed over at the Tongi site"
        }, recorder);
        var edits = new[]
        {
            "UPDATE credit_payments SET amount_minor = amount_minor + 1 WHERE id = {0}",
            "UPDATE credit_payments SET reference = 'OTHER0001' WHERE id = {0}",
            "UPDATE credit_payments SET allocations = '[]'::jsonb WHERE id = {0}",
            "UPDATE credit_payments SET note = 'changed', updated_at = now() WHERE id = {0}",
            "UPDATE credit_payments SET recorded_by = gen_random_uuid() WHERE id = {0}",
        };
        foreach (var edit in edits)
            (await SqlRefusedAsync(edit, payment.Id)).MessageText.Should().Contain("changes only by its next step", edit);

        await ChangeAsync(payment.Id, p => p.Verify(verifier, Today, DateTime.UtcNow));
        foreach (var edit in edits.Append("UPDATE credit_payments SET cleared_on = cleared_on - 1, verified_at = now() WHERE id = {0}"))
            (await SqlRefusedAsync(edit, payment.Id)).MessageText.Should().Contain("a verified row changes only by its next step", edit);
        foreach (var step in new[] { "recorded", "rejected" })
            (await SqlRefusedAsync($"UPDATE credit_payments SET status = '{step}' WHERE id = {{0}}", payment.Id)).MessageText.Should().Contain($"verified cannot become {step}");

        await ChangeAsync(payment.Id, p => p.Bounce(verifier, "returned: account closed", DateTime.UtcNow));
        (await SqlRefusedAsync("UPDATE credit_payments SET status = 'verified', bounced_by = NULL, bounced_at = NULL, decision_reason = NULL WHERE id = {0}", payment.Id))
            .MessageText.Should().Contain("bounced cannot become verified");
        (await SqlRefusedAsync("UPDATE credit_payments SET decision_reason = 'something else' WHERE id = {0}", payment.Id))
            .MessageText.Should().Contain("changes only by its next step");

        foreach (var wipe in new[] { "DELETE FROM credit_payments WHERE id = {0}", "TRUNCATE credit_payments CASCADE" })
            (await SqlRefusedAsync(wipe, payment.Id)).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, wipe);

        await using var check = fixture.CreateContext();
        (await check.CreditPayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Should().BeEquivalentTo(new
        {
            Status = CreditPaymentStatus.Bounced,
            AmountMinor = 10_000L,
            Note = "handed over at the Tongi site",
            VerifiedBy = (Guid?)verifier,
            BouncedBy = (Guid?)verifier,
            DecisionReason = "returned: account closed"
        });
        (await check.CreditPayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Allocations.Should().ContainSingle().Which.AmountMinor.Should().Be(5_000);
    }

    [PostgresFact]
    public async Task AnAdjustment_ChangesOnlyByItsNextStep_AndIsNeverDeleted()
    {
        var company = await CompanyAsync();
        var (requester, approver) = (await UserAsync(), await UserAsync());
        Guid order;
        await using (var db = fixture.CreateContext()) order = await CreditTestData.InsertOrderAsync(db, requester, company, 50_000);
        var writeOff = CreditAdjustment.Request(Guid.NewGuid(), new CreditAdjustmentInput(company, CreditAdjustmentKind.WriteOff, 5_000,
            "customer insolvent, board minute 12", OrderId: order), requester, Rules.MaxPaymentMinor, DateTime.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.CreditAdjustments.Add(writeOff);
            await db.SaveChangesAsync();
        }

        (await SqlRefusedAsync("UPDATE credit_adjustments SET amount_minor = 50000 WHERE id = {0}", writeOff.Id)).MessageText.Should().Contain("changes only by its next step");
        await using (var db = fixture.CreateContext())
        {
            (await db.CreditAdjustments.SingleAsync(a => a.Id == writeOff.Id)).Approve(approver, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        (await SqlRefusedAsync("UPDATE credit_adjustments SET status = 'rejected', rejected_by = {1}, rejected_at = now(), decision_reason = 'oops' WHERE id = {0}",
            writeOff.Id, approver)).MessageText.Should().Contain("approved cannot become rejected");
        (await SqlRefusedAsync("UPDATE credit_adjustments SET reason = 'a different reason entirely' WHERE id = {0}", writeOff.Id)).MessageText.Should().Contain("changes only by its next step");
        foreach (var wipe in new[] { "DELETE FROM credit_adjustments WHERE id = {0}", "TRUNCATE credit_adjustments CASCADE" })
            (await SqlRefusedAsync(wipe, writeOff.Id)).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, wipe);
        (await SqlRefusedAsync(@"INSERT INTO credit_adjustments (id, company_id, kind, amount_minor, currency, order_id, reason, status, requested_by, approved_by, approved_at, created_at)
            VALUES ({0}, {1}, 'write_off', 100, 'BDT', {2}, 'created already approved', 'approved', {3}, {4}, now(), now())",
            Guid.NewGuid(), company, order, requester, approver)).MessageText.Should().Contain("created as requested");
    }

    [PostgresFact]
    public async Task TheDatabase_RefusesPaymentsOfTheWrongShape()
    {
        var company = await CompanyAsync();
        var recorder = await UserAsync();
        var bad = new (string Why, Func<DatabaseFacade, Task<int>> Insert)[]
        {
            ("a zero amount", d => InsertPaymentAsync(d, company, recorder, amount: 0)),
            ("above the hard ceiling", d => InsertPaymentAsync(d, company, recorder, amount: 1_000_000_000_001)),
            ("another currency", d => InsertPaymentAsync(d, company, recorder, currency: "USD")),
            ("an unknown method", d => InsertPaymentAsync(d, company, recorder, method: "paypal")),
            ("a reference that is not normalised", d => InsertPaymentAsync(d, company, recorder, reference: "ab-123")),
            ("a cheque without its bank", d => InsertPaymentAsync(d, company, recorder, method: "cheque", chequeDate: true)),
            ("a cheque without its date", d => InsertPaymentAsync(d, company, recorder, method: "cheque", bank: "SONALIBANK")),
            ("a transfer with a bank", d => InsertPaymentAsync(d, company, recorder, bank: "SONALIBANK")),
            ("allocations that are not a list", d => InsertPaymentAsync(d, company, recorder, allocations: "{}")),
        };
        foreach (var (why, insert) in bad)
        {
            await using var db = fixture.CreateContext();
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => insert(db.Database))).SqlState.Should().Be(PostgresErrorCodes.CheckViolation, why);
        }

        // Cash never bounces, even when a raw write skips the domain; and a verifier must be a real user.
        var verifier = await UserAsync();
        var cash = await RecordAsync(Input(company, CreditPaymentMethod.Cash, null), recorder);
        cash.Reference.Should().MatchRegex("^CR[0-9]{6}[0-9A-F]{6}$");
        (await RefusedAsync(() => ChangeAsync(cash.Id, p => p.Verify(Guid.NewGuid(), Today, DateTime.UtcNow)))).SqlState
            .Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        await ChangeAsync(cash.Id, p => p.Verify(verifier, Today, DateTime.UtcNow));
        var ex = await SqlRefusedAsync("UPDATE credit_payments SET status = 'bounced', bounced_by = {1}, bounced_at = now(), decision_reason = 'no' WHERE id = {0}",
            cash.Id, verifier);
        (ex.SqlState, ex.ConstraintName).Should().Be((PostgresErrorCodes.CheckViolation, "ck_credit_payments_status"));
    }

    [PostgresFact]
    public async Task ALedgerLine_NamesARealPaymentAndAdjustment()
    {
        var company = await CompanyAsync();
        var recorder = await UserAsync();
        var payment = await RecordAsync(Input(company, CreditPaymentMethod.Beftn, Ref()), recorder);
        static Task<int> Line(DatabaseFacade d, Guid company, string kind, Guid paymentId, Guid? adjustmentId) => d.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, payment_id, adjustment_id, receivable_delta_minor,
                on_account_delta_minor, receivable_after_minor, on_account_after_minor, occurred_on, currency, idempotency_key, created_at)
            VALUES ({Guid.NewGuid()}, {company}, 1, {Guid.NewGuid()}, {kind}, {paymentId}, {adjustmentId}, 0, 5000, 0, 5000, now()::date, 'BDT',
                {"test:" + Guid.NewGuid().ToString("N")}, now())");

        foreach (var (kind, paymentId, adjustmentId, constraint) in new[]
                 {
                     ("payment_on_account", Guid.NewGuid(), (Guid?)null, "FK_credit_ledger_entries_credit_payments_payment_id"),
                     ("on_account_correction", payment.Id, Guid.NewGuid(), "FK_credit_ledger_entries_credit_adjustments_adjustment_id"),
                 })
        {
            await using var attempt = fixture.CreateContext();
            var ex = await RefusedAsync(() => Line(attempt.Database, company, kind, paymentId, adjustmentId));
            (ex.SqlState, ex.ConstraintName).Should().Be((PostgresErrorCodes.ForeignKeyViolation, constraint));
        }

        // The control: the payment exists, so its on-account line commits (with the company moved in the same transaction).
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await Line(db.Database, company, "payment_on_account", payment.Id, null);
        await db.Database.ExecuteSqlRawAsync("UPDATE companies SET credit_on_account = 5000, credit_ledger_seq = 1 WHERE id = {0}", company);
        await tx.CommitAsync();
        (await SqlRefusedAsync("DELETE FROM credit_payments WHERE id = {0}", payment.Id)).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [PostgresFact]
    public async Task AVerification_PostsItsPlannedLines_AndCommitsUnderTheLedgerChecks()
    {
        // Domain + database together, the way B3.4 will do it in one transaction: two charged orders; the recorder names the
        // newer one for 3,000; the payment of 10,000 pays it, then the older one in full (FIFO), and 1,000 goes on account.
        var company = await CompanyAsync();
        var (buyer, recorder, verifier) = (await UserAsync(), await UserAsync(), await UserAsync());
        var (older, newer) = (Guid.Empty, Guid.Empty);
        await using (var db = fixture.CreateContext())
        {
            older = await CreditTestData.InsertOrderAsync(db, buyer, company, 6_000);
            newer = await CreditTestData.InsertOrderAsync(db, buyer, company, 5_000);
            var c = await db.Companies.SingleAsync(x => x.Id == company);
            var ledger = new CreditLedger(c, []);
            db.CreditLedgerEntries.Add(ledger.Charge(older, 6_000, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow.AddDays(-20)).Entry);
            db.CreditLedgerEntries.Add(ledger.Charge(newer, 5_000, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-2)).Entry);
            await db.SaveChangesAsync();
        }
        var payment = await RecordAsync(Input(company, CreditPaymentMethod.Rtgs, Ref(), amount: 10_000) with { Allocations = [new(newer, 3_000)] }, recorder);

        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            // Lock order (§7): credit_payments → companies.
            var p = (await db.CreditPayments.FromSqlInterpolated($"SELECT p.*, p.xmin FROM credit_payments p WHERE p.id = {payment.Id} FOR UPDATE").ToListAsync()).Single();
            var c = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {company} FOR UPDATE").ToListAsync()).Single();
            var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company).ToListAsync();
            var ledger = new CreditLedger(c, lines);
            var open = lines.Where(l => l.Kind == CreditLedgerKind.Charge)
                .Select(l => new CreditOpenCharge(l.OrderId!.Value, ledger.BalanceOf(l.OrderId.Value).ReceivableMinor, l.DueOn!.Value, l.CreatedAtUtc));

            p.Verify(verifier, Today, DateTime.UtcNow);
            var plan = CreditAllocationPlanner.Plan(p.AmountMinor, open, p.Allocations);
            foreach (var posting in plan.Postings(p.Id, Today, Guid.NewGuid(), verifier, "corr-b33b"))
                db.CreditLedgerEntries.Add(ledger.Post(posting, DateTime.UtcNow).Entry);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using var check = fixture.CreateContext();
        var after = await check.Companies.AsNoTracking().SingleAsync(x => x.Id == company);
        (after.CreditUsedMinor, after.CreditOnAccountMinor, after.CreditLedgerSeq).Should().Be((2_000, 1_000, 5), "11,000 owed − 9,000 applied; 1,000 on account");
        var verification = await check.CreditLedgerEntries.AsNoTracking().Where(l => l.PaymentId == payment.Id).OrderBy(l => l.Seq).ToListAsync();
        verification.Select(l => (l.Kind, l.OrderId, l.ReceivableDeltaMinor, l.OnAccountDeltaMinor)).Should().Equal(
            (CreditLedgerKind.PaymentApplied, (Guid?)newer, -3_000L, 0L),
            (CreditLedgerKind.PaymentApplied, (Guid?)older, -6_000L, 0L),
            (CreditLedgerKind.PaymentOnAccount, (Guid?)null, 0L, 1_000L));
        (await check.CreditPayments.AsNoTracking().SingleAsync(x => x.Id == payment.Id)).Status.Should().Be(CreditPaymentStatus.Verified);
    }
}
