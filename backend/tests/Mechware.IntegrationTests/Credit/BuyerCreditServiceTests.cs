using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.9 (ADR-0010 §10, threat model CS22, CS23, CS24) against a real database: the buyer reads of a company's own credit. The point is who may
/// NOT read: another company's member, a non-member, the wrong role, a deactivated or uninvited member, a Pending or Rejected company: one answer
/// for all of them. And what a buyer may not SEE even in their own statement: staff ids, internal reasons, notes, rejected payments, full references.
/// </summary>
public class BuyerCreditServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string NotFound = "Company not found.";
    private static long _binSeq = 3_980_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Ref() => "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.39";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-b39";
        public bool HasPermission(string permission) => true;
    }

    private sealed record Firm(Guid Id, Guid Owner);

    private MechwareDbContext Ctx() => fixture.CreateContext();

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private async Task<T> Buyer<T>(Func<BuyerCreditService, Task<T>> action, CreditOptions? options = null)
    {
        await using var db = Ctx();
        return await action(new BuyerCreditService(db, options ?? new CreditOptions(), TimeProvider.System));
    }

    private async Task<T> Pay<T>(Guid actor, Func<CreditPaymentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, new Staff(actor), new CreditOptions(), TimeProvider.System));
    }

    private async Task<T> Adj<T>(Guid actor, Func<CreditAdjustmentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditAdjustmentService(db, new Staff(actor), new CreditOptions { PayoutAfterClearingDays = 0 }, TimeProvider.System));
    }

    /// <summary>A Net-30 company; Active unless <paramref name="status"/> says otherwise.</summary>
    private async Task<Firm> CompanyAsync(string name, CompanyStatus status = CompanyStatus.Active, long limit = 1_000_000_000)
    {
        var owner = await UserAsync();
        await using var db = Ctx();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner, new RegisterCompanyRequest(name, "TL-B39-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, limit));
        if (status == CompanyStatus.Rejected) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET status = 'Rejected' WHERE id = {id}");
        else if (status == CompanyStatus.Pending) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET status = 'Pending' WHERE id = {id}");
        return new Firm(id, owner);
    }

    private async Task<Guid> MemberAsync(Firm firm, CompanyRoles roles)
    {
        var user = await UserAsync();
        await using var db = Ctx();
        await new CompanyService(db).AddAcceptedMemberAsync(firm.Owner, firm.Id, new AddMemberRequest(user, roles, null, null));
        return user;
    }

    /// <summary>An order charged <paramref name="daysAgo"/> days ago (Net-30: due 30 days after).</summary>
    private async Task<(Guid Order, string Number)> ChargeAsync(Guid company, long amount, int daysAgo)
    {
        await using var db = Ctx();
        var order = await CreditTestData.InsertOrderAsync(db, await UserAsync(), company, amount);
        var entity = await db.Companies.SingleAsync(c => c.Id == company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company).ToListAsync();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(entity, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return (order, await db.Orders.Where(o => o.Id == order).Select(o => o.OrderNumber).SingleAsync());
    }

    private async Task<Guid> RecordedAsync(Guid company, long amount, string reference, string? note = null) =>
        (await Pay(await UserAsync(), s => s.RecordAsync(company, new RecordCreditPaymentRequest("rtgs", amount, Today, reference, Note: note), Key()))).Payment.Id;

    private async Task<Guid> PaidAsync(Guid company, long amount, string reference, string? note = null)
    {
        var payment = await RecordedAsync(company, amount, reference, note);
        await Pay(await UserAsync(), s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    private static async Task AssertNotFoundAsync(Func<Task> read, string because) =>
        (await Assert.ThrowsAsync<KeyNotFoundException>(read)).Message.Should().Be(NotFound, because);

    // ---- /credit -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task TheCreditPosition_ShowsLimitUsedAvailableOverdueAging_AndPendingPayments()
    {
        var firm = await CompanyAsync("Position " + Guid.NewGuid().ToString("N")[..8], limit: 100_000_000);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        await ChargeAsync(firm.Id, 50_000, 40);      // due 10 days ago
        await ChargeAsync(firm.Id, 20_000, 0);       // due in 30 days
        await PaidAsync(firm.Id, 25_000, Ref());     // oldest due first: 25,000 left on the first order
        await RecordedAsync(firm.Id, 5_000, "REF123456789", note: "internal: chased by phone");

        var credit = await Buyer(s => s.GetAsync(buyer, firm.Id));

        credit.Should().Match<BuyerCreditDto>(c => c.CompanyId == firm.Id && c.Terms == "net30" && c.LimitMinor == 100_000_000 && c.UsedMinor == 45_000
            && c.AvailableMinor == 100_000_000 - 45_000 && c.OnAccountMinor == 0 && c.OverdueMinor == 25_000 && c.Currency == "BDT" && !c.OnHold);
        credit.OldestDueOn.Should().Be(Today.AddDays(-10));
        credit.Aging.Should().Be(new CreditAgingBuckets(Current: 20_000, Days1To30: 25_000, Days31To60: 0, Days61To90: 0, Days90Plus: 0));
        credit.PendingPayments.Should().ContainSingle().Which.Should().Be(new BuyerPendingPaymentDto(Today, "rtgs", 5_000, "****6789"));
    }

    [PostgresFact]
    public async Task OnHold_FollowsTheOverdueRule_AndAnAdminLiftTurnsItOff_WithoutShowingTheOverride()
    {
        var firm = await CompanyAsync("Hold " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        await ChargeAsync(firm.Id, 10_000, 45);      // exactly 15 days past due: not held yet
        (await Buyer(s => s.GetAsync(buyer, firm.Id))).OnHold.Should().BeFalse();

        await ChargeAsync(firm.Id, 10_000, 46);      // 16 days past due
        (await Buyer(s => s.GetAsync(buyer, firm.Id))).OnHold.Should().BeTrue();

        await using (var db = Ctx()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET credit_hold_override_until = now() + interval '3 days' WHERE id = {firm.Id}");
        var lifted = await Buyer(s => s.GetAsync(buyer, firm.Id));
        lifted.OnHold.Should().BeFalse();
        JsonSerializer.Serialize(lifted).ToLowerInvariant().Should().NotContain("override");
    }

    [PostgresFact]
    public async Task AvailableCredit_IsNeverBelowZero_EvenWhenABounceTookUsedAboveTheLimit()
    {
        var firm = await CompanyAsync("Over " + Guid.NewGuid().ToString("N")[..8], limit: 100_000);
        var buyer = await MemberAsync(firm, CompanyRoles.Admin);
        await ChargeAsync(firm.Id, 100_000, 5);
        var payment = await PaidAsync(firm.Id, 100_000, Ref());
        await ChargeAsync(firm.Id, 100_000, 1);
        await Pay(await UserAsync(), s => s.BounceAsync(payment, new BounceCreditPaymentRequest("cheque returned by the bank")));

        var credit = await Buyer(s => s.GetAsync(buyer, firm.Id));
        credit.UsedMinor.Should().Be(200_000);
        credit.AvailableMinor.Should().Be(0);
    }

    // ---- /credit/open-items ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task OpenItems_ListOnlyOrdersThatOweSomething_OldestDueFirst_WithTheirStatus_AndPage()
    {
        var firm = await CompanyAsync("Items " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        var paid = await ChargeAsync(firm.Id, 4_000, 60);       // paid off below
        var overdue = await ChargeAsync(firm.Id, 30_000, 50);   // due 20 days ago
        var partial = await ChargeAsync(firm.Id, 20_000, 10);   // due in 20 days
        var fresh = await ChargeAsync(firm.Id, 9_000, 0);
        await PaidAsync(firm.Id, 4_000 + 5_000, Ref());         // oldest due first: the 60-day order (4,000), then 5,000 of the 50-day order

        var items = (await Buyer(s => s.OpenItemsAsync(buyer, firm.Id, 1, 10))).Items;
        items.Select(i => i.OrderNumber).Should().Equal(overdue.Number, partial.Number, fresh.Number);
        items[0].Should().Be(new BuyerOpenItemDto(overdue.Number, 30_000, 25_000, Today.AddDays(-20), 20, "overdue"));
        items[1].Should().Be(new BuyerOpenItemDto(partial.Number, 20_000, 20_000, Today.AddDays(20), 0, "open"));
        items[2].Status.Should().Be("open");
        items.Select(i => i.OrderNumber).Should().NotContain(paid.Number);

        var second = await Buyer(s => s.OpenItemsAsync(buyer, firm.Id, 2, 2));
        second.Should().Match<CreditPage<BuyerOpenItemDto>>(p => p.TotalCount == 3 && p.Page == 2 && p.PageSize == 2 && p.Items.Count == 1);
        (await Buyer(s => s.OpenItemsAsync(buyer, firm.Id, 1, 5_000))).PageSize.Should().Be(BuyerCreditService.MaxPageSize);
    }

    [PostgresFact]
    public async Task APartlyPaidOrder_ShowsItsChargeAndWhatIsLeft_AsPartiallyPaid()
    {
        var firm = await CompanyAsync("Partly " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        var order = await ChargeAsync(firm.Id, 20_000, 5);
        await PaidAsync(firm.Id, 7_000, Ref());

        (await Buyer(s => s.OpenItemsAsync(buyer, firm.Id, 1, 10))).Items.Should().ContainSingle()
            .Which.Should().Be(new BuyerOpenItemDto(order.Number, 20_000, 13_000, Today.AddDays(25), 0, "partially_paid"));
    }

    // ---- /credit/statement -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task TheStatement_ListsLinesInOrder_WithTheCurrentTotalsOfTheSameSnapshot_AndFiltersByDay()
    {
        var firm = await CompanyAsync("Statement " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        var order = await ChargeAsync(firm.Id, 40_000, 3);
        await PaidAsync(firm.Id, 15_000, "CHEQUE-REF-0042");

        var statement = await Buyer(s => s.StatementAsync(buyer, firm.Id, null, null, 1, 10));
        statement.Lines.Items.Select(l => (l.Seq, l.Kind)).Should().Equal((1L, "charge"), (2L, "payment_applied"));
        statement.Lines.Items[0].Should().Match<BuyerStatementLineDto>(l => l.OrderNumber == order.Number && l.ReceivableDeltaMinor == 40_000 && l.ReceivableAfterMinor == 40_000 && l.PaymentMethod == null);
        statement.Lines.Items[1].Should().Match<BuyerStatementLineDto>(l => l.OrderNumber == order.Number && l.PaymentMethod == "rtgs" && l.ReferenceTail == "****0042"
            && l.ReceivableDeltaMinor == -15_000 && l.ReceivableAfterMinor == 25_000);
        (statement.CurrentReceivableMinor, statement.CurrentOnAccountMinor).Should().Be((25_000L, 0L));
        statement.Lines.Items[^1].ReceivableAfterMinor.Should().Be(statement.CurrentReceivableMinor);

        // The charge is about the day it was posted (3 days ago); the payment is about the day it cleared (today).
        (await Buyer(s => s.StatementAsync(buyer, firm.Id, Today.AddDays(1), null, 1, 10))).Lines.TotalCount.Should().Be(0);
        (await Buyer(s => s.StatementAsync(buyer, firm.Id, null, Today.AddDays(-4), 1, 10))).Lines.TotalCount.Should().Be(0);
        (await Buyer(s => s.StatementAsync(buyer, firm.Id, Today.AddDays(-4), Today.AddDays(-1), 1, 10))).Lines.Items.Should().ContainSingle().Which.Seq.Should().Be(1);
        (await Buyer(s => s.StatementAsync(buyer, firm.Id, null, null, 2, 1))).Lines.Items.Should().ContainSingle().Which.Seq.Should().Be(2);
        await Assert.ThrowsAsync<ValidationException>(() => Buyer(s => s.StatementAsync(buyer, firm.Id, Today, Today.AddDays(-1), 1, 10)));
    }

    /// <summary>CS23: a statement that holds every kind of line (an approved write-off with an internal reason, a recorded-then-rejected payment, a verified payment with a staff note) and still shows nothing internal.</summary>
    [PostgresFact]
    public async Task BuyerStatement_HidesRejectedPaymentsStaffNotesReasonsActorsAndFullReferences()
    {
        var firm = await CompanyAsync("Hidden " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        var order = await ChargeAsync(firm.Id, 30_000, 5);
        var (recorder, verifier, requester, approver) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
        var verified = (await Pay(recorder, s => s.RecordAsync(firm.Id, new RecordCreditPaymentRequest("rtgs", 10_000, Today, "SECRETBANKREF-7788", Note: "NOTE-ONLY-STAFF-SEE"), Key()))).Payment.Id;
        await Pay(verifier, s => s.VerifyAsync(verified, new VerifyCreditPaymentRequest(Today)));
        var rejected = (await Pay(recorder, s => s.RecordAsync(firm.Id, new RecordCreditPaymentRequest("rtgs", 99_000, Today, "REJECTEDREF-0001"), Key()))).Payment.Id;
        await Pay(verifier, s => s.RejectAsync(rejected, new RejectCreditPaymentRequest("REASON-ONLY-STAFF-SEE: forged slip")));
        var asked = (await Adj(requester, s => s.RequestAsync(firm.Id, new RequestCreditAdjustmentRequest("write_off", 2_000, "ADJUST-REASON-ONLY-STAFF-SEE", OrderId: order.Order), Key()))).Adjustment.Id;
        await Adj(approver, s => s.ApproveAsync(asked));
        var pending = await RecordedAsync(firm.Id, 3_000, "PENDINGREF-5566", note: "PENDING-NOTE-ONLY-STAFF-SEE");

        var credit = await Buyer(s => s.GetAsync(buyer, firm.Id));
        var items = await Buyer(s => s.OpenItemsAsync(buyer, firm.Id, 1, 50));
        var statement = await Buyer(s => s.StatementAsync(buyer, firm.Id, null, null, 1, 50));
        var json = JsonSerializer.Serialize(new object[] { credit, items, statement });

        statement.Lines.Items.Select(l => l.Kind).Should().Equal("charge", "payment_applied", "write_off");
        statement.Lines.TotalCount.Should().Be(3, "the rejected payment moved no money, so it has no line at all");
        foreach (var secret in new[] { "SECRETBANKREF-7788", "NOTE-ONLY-STAFF-SEE", "REJECTEDREF-0001", "REASON-ONLY-STAFF-SEE", "forged slip", "ADJUST-REASON-ONLY-STAFF-SEE",
                     "PENDINGREF-5566", "PENDING-NOTE-ONLY-STAFF-SEE", recorder.ToString(), verifier.ToString(), requester.ToString(), approver.ToString(), rejected.ToString(),
                     verified.ToString(), asked.ToString(), pending.ToString(), "203.0.113.39", "corr-b39" })
            json.Should().NotContainEquivalentOf(secret);
        json.Should().Contain("****7788").And.Contain("****5566");
        credit.PendingPayments.Should().ContainSingle().Which.AmountMinor.Should().Be(3_000, "the rejected one is not pending");
    }

    [PostgresFact]
    public async Task ACompanyNeverSeesAnotherCompanysLines_OrderNumbersOrPayments()
    {
        var mine = await CompanyAsync("Mine " + Guid.NewGuid().ToString("N")[..8]);
        var theirs = await CompanyAsync("Theirs " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(mine, CompanyRoles.Buyer);
        var myOrder = await ChargeAsync(mine.Id, 10_000, 2);
        var theirOrder = await ChargeAsync(theirs.Id, 77_000, 50);
        await PaidAsync(theirs.Id, 5_000, "THEIRREF-7421");
        await RecordedAsync(theirs.Id, 1_234, "THEIRPENDING-1111");

        var credit = await Buyer(s => s.GetAsync(buyer, mine.Id));
        var json = JsonSerializer.Serialize(new object[]
        {
            credit, await Buyer(s => s.OpenItemsAsync(buyer, mine.Id, 1, 50)), await Buyer(s => s.StatementAsync(buyer, mine.Id, null, null, 1, 50))
        });
        json.Should().Contain(myOrder.Number).And.NotContain(theirOrder.Number).And.NotContain("77000").And.NotContain("THEIRREF").And.NotContain("THEIRPENDING").And.NotContain("7421").And.NotContain("1234");
        credit.PendingPayments.Should().BeEmpty();
    }

    // ---- who may read (CS22, CS24) -----------------------------------------------------------------------------------------

    /// <summary>CS22 at service level: every way of NOT being allowed ends in the one same "Company not found." on all three reads.</summary>
    [PostgresFact]
    public async Task Idor_CreditOfAnotherCompany_IsTheSameNotFound_ForEveryOtherWay()
    {
        var target = await CompanyAsync("Target " + Guid.NewGuid().ToString("N")[..8]);
        var mine = await CompanyAsync("Mine " + Guid.NewGuid().ToString("N")[..8]);
        await ChargeAsync(target.Id, 10_000, 5);
        var strangerAdmin = await MemberAsync(mine, CompanyRoles.Admin | CompanyRoles.Buyer);    // full rights, but of ANOTHER company
        var nobody = await UserAsync();                                                           // no company at all
        var requester = await MemberAsync(target, CompanyRoles.Requester);
        var approver = await MemberAsync(target, CompanyRoles.Approver);
        var requesterApprover = await MemberAsync(target, CompanyRoles.Requester | CompanyRoles.Approver);
        var gone = await MemberAsync(target, CompanyRoles.Buyer);
        await using (var db = Ctx()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE company_members SET is_active = false WHERE company_id = {target.Id} AND user_id = {gone}");
        var invited = await UserAsync();
        await using (var db = Ctx()) await new CompanyService(db).AddMemberAsync(target.Owner, target.Id, new AddMemberRequest(invited, CompanyRoles.Buyer | CompanyRoles.Admin, null, null));   // never accepted

        foreach (var (who, label) in new[] { (strangerAdmin, "member of another company"), (nobody, "not a member"), (requester, "Requester"), (approver, "Approver"),
                     (requesterApprover, "Requester + Approver"), (gone, "deactivated member"), (invited, "invitation not accepted") })
        {
            await AssertNotFoundAsync(() => Buyer(s => s.GetAsync(who, target.Id)), label);
            await AssertNotFoundAsync(() => Buyer(s => s.OpenItemsAsync(who, target.Id, 1, 10)), label);
            await AssertNotFoundAsync(() => Buyer(s => s.StatementAsync(who, target.Id, null, null, 1, 10)), label);
        }

        // An unknown company id answers the very same way, and the dates are never looked at before the company.
        await AssertNotFoundAsync(() => Buyer(s => s.GetAsync(strangerAdmin, Guid.NewGuid())), "unknown company");
        await AssertNotFoundAsync(() => Buyer(s => s.StatementAsync(strangerAdmin, Guid.NewGuid(), Today, Today.AddDays(-1), 1, 10)), "unknown company with bad dates");
        await AssertNotFoundAsync(() => Buyer(s => s.StatementAsync(nobody, target.Id, Today, Today.AddDays(-1), 1, 10)), "not a member with bad dates");
        // The owner (Admin) of the target reads it fine.
        (await Buyer(s => s.GetAsync(target.Owner, target.Id))).UsedMinor.Should().Be(10_000);
    }

    [PostgresFact]
    public async Task OnlyTheRolesInBalanceVisibleTo_ReadTheCredit_AndTheOptionIsHonoured()
    {
        var firm = await CompanyAsync("Roles " + Guid.NewGuid().ToString("N")[..8]);
        var (admin, buyer, approver, requester) = (firm.Owner, await MemberAsync(firm, CompanyRoles.Buyer), await MemberAsync(firm, CompanyRoles.Approver), await MemberAsync(firm, CompanyRoles.Requester));
        (await Buyer(s => s.GetAsync(admin, firm.Id))).CompanyId.Should().Be(firm.Id);
        (await Buyer(s => s.GetAsync(buyer, firm.Id))).CompanyId.Should().Be(firm.Id);
        await AssertNotFoundAsync(() => Buyer(s => s.GetAsync(approver, firm.Id)), "Approver");
        await AssertNotFoundAsync(() => Buyer(s => s.GetAsync(requester, firm.Id)), "Requester");

        var adminsOnly = new CreditOptions { BalanceVisibleTo = CompanyRoles.Admin };
        (await Buyer(s => s.GetAsync(admin, firm.Id), adminsOnly)).CompanyId.Should().Be(firm.Id);
        await AssertNotFoundAsync(() => Buyer(s => s.GetAsync(buyer, firm.Id), adminsOnly), "Buyer when only Admin is allowed");
        var everyone = new CreditOptions { BalanceVisibleTo = CompanyRoles.Admin | CompanyRoles.Buyer | CompanyRoles.Approver | CompanyRoles.Requester };
        (await Buyer(s => s.GetAsync(requester, firm.Id), everyone)).CompanyId.Should().Be(firm.Id);
    }

    /// <summary>CS24: a Suspended company still sees what it owes (and so can pay); Pending and Rejected look like no company at all.</summary>
    [PostgresFact]
    public async Task ASuspendedCompany_StillSeesItsStatement_ButPendingAndRejectedOnesDoNot()
    {
        var suspended = await CompanyAsync("Susp " + Guid.NewGuid().ToString("N")[..8]);
        await ChargeAsync(suspended.Id, 12_000, 3);
        await using (var db = Ctx()) await new CompanyService(db).AdminSuspendAsync(suspended.Id);
        (await Buyer(s => s.GetAsync(suspended.Owner, suspended.Id))).UsedMinor.Should().Be(12_000);
        (await Buyer(s => s.OpenItemsAsync(suspended.Owner, suspended.Id, 1, 10))).TotalCount.Should().Be(1);
        (await Buyer(s => s.StatementAsync(suspended.Owner, suspended.Id, null, null, 1, 10))).Lines.TotalCount.Should().Be(1);

        foreach (var status in new[] { CompanyStatus.Pending, CompanyStatus.Rejected })
        {
            var firm = await CompanyAsync("Other " + Guid.NewGuid().ToString("N")[..8], status);
            await AssertNotFoundAsync(() => Buyer(s => s.GetAsync(firm.Owner, firm.Id)), status.ToString());
            await AssertNotFoundAsync(() => Buyer(s => s.OpenItemsAsync(firm.Owner, firm.Id, 1, 10)), status.ToString());
            await AssertNotFoundAsync(() => Buyer(s => s.StatementAsync(firm.Owner, firm.Id, null, null, 1, 10)), status.ToString());
        }
    }

    // ---- read model --------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task BuyerReads_NeverWaitForARowLock_AndWriteNoLedgerLine()
    {
        var firm = await CompanyAsync("Locks " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        await ChargeAsync(firm.Id, 20_000, 4);
        var payment = await RecordedAsync(firm.Id, 5_000, Ref());
        var before = await CountAsync("SELECT count(*)::int AS \"Value\" FROM credit_ledger_entries");

        await using var holder = Ctx();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_payments WHERE id = {payment} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id IN (SELECT order_id FROM credit_ledger_entries WHERE company_id = {firm.Id}) FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {firm.Id} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM company_members WHERE company_id = {firm.Id} FOR UPDATE");

        var reads = Task.WhenAll(new Task[] { Buyer(s => s.GetAsync(buyer, firm.Id)), Buyer(s => s.OpenItemsAsync(buyer, firm.Id, 1, 10)),
            Buyer(s => s.StatementAsync(buyer, firm.Id, null, null, 1, 10)) });
        (await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(reads, "a read never queues behind a writer's row lock");
        await reads;
        await tx.RollbackAsync();
        (await CountAsync("SELECT count(*)::int AS \"Value\" FROM credit_ledger_entries")).Should().Be(before);
    }

    /// <summary>Runs <paramref name="action"/> once, right after the company row was read (the totals), so a write commits between the totals and the lines.</summary>
    private sealed class AfterTheCompanyRowInterceptor(Func<Task> action) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, System.Data.Common.DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("credit_limit", StringComparison.Ordinal) && command.CommandText.Contains("FROM companies", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
    }

    /// <summary>The snapshot, deterministically: a charge commits after the totals were read and before the lines are; the answer still adds up.</summary>
    [PostgresFact]
    public async Task AWriteCommittedBetweenTheTotalsAndTheLines_IsNotSeen_BecauseTheReadIsOneSnapshot()
    {
        var firm = await CompanyAsync("Snapshot " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        await ChargeAsync(firm.Id, 10_000, 3);

        await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new AfterTheCompanyRowInterceptor(async () => await ChargeAsync(firm.Id, 7_000, 0))).Options);
        var statement = await new BuyerCreditService(db, new CreditOptions(), TimeProvider.System).StatementAsync(buyer, firm.Id, null, null, 1, 10);

        (await CountAsync($"SELECT count(*)::int AS \"Value\" FROM credit_ledger_entries WHERE company_id = '{firm.Id}'")).Should().Be(2, "the write did commit");
        statement.Lines.Items.Should().ContainSingle("the second charge was committed after the snapshot began");
        statement.CurrentReceivableMinor.Should().Be(10_000);
        statement.Lines.Items[^1].ReceivableAfterMinor.Should().Be(statement.CurrentReceivableMinor);
    }

    [PostgresFact]
    public async Task EveryBuyerRead_DuringParallelWrites_IsInternallyConsistent()
    {
        var firm = await CompanyAsync("Parallel " + Guid.NewGuid().ToString("N")[..8]);
        var buyer = await MemberAsync(firm, CompanyRoles.Buyer);
        for (var i = 0; i < 4; i++) await ChargeAsync(firm.Id, 10_000 + i, 3);

        var writers = Enumerable.Range(0, 4).Select(i => Task.Run(() => PaidAsync(firm.Id, 3_000 + i, Ref()))).ToList();
        var readers = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            var statement = await Buyer(s => s.StatementAsync(buyer, firm.Id, null, null, 1, 100));
            var last = statement.Lines.Items.Last();
            last.ReceivableAfterMinor.Should().Be(statement.CurrentReceivableMinor);
            last.OnAccountAfterMinor.Should().Be(statement.CurrentOnAccountMinor);
        })).ToList();
        await Task.WhenAll(writers.Cast<Task>().Concat(readers));
    }

    // ---- GET /companies/{id}: the narrow credit fields -----------------------------------------------------------------------

    [PostgresFact]
    public async Task TheCompanyView_ShowsTheCreditAmountsToAdminsAndBuyersOnly_AndTheTermsToEveryone()
    {
        var firm = await CompanyAsync("View " + Guid.NewGuid().ToString("N")[..8], limit: 5_000_000);
        var (buyer, approver, requester) = (await MemberAsync(firm, CompanyRoles.Buyer), await MemberAsync(firm, CompanyRoles.Approver), await MemberAsync(firm, CompanyRoles.Requester | CompanyRoles.Approver));
        async Task<CompanyDto> View(Guid who, CreditOptions? options = null, bool list = false)
        {
            await using var db = Ctx();
            var companies = new CompanyService(db, options);
            return list ? (await companies.ListMineAsync(who)).Single(c => c.Company.Id == firm.Id).Company : await companies.GetAsync(who, firm.Id);
        }

        foreach (var list in new[] { false, true })
        {
            foreach (var who in new[] { firm.Owner, buyer })
            {
                var full = await View(who, list: list);
                full.CreditLimit.Should().Be(new MoneyDto(5_000_000, "BDT"));
                full.CreditUsed.Should().NotBeNull();
                full.CreditAvailable.Should().NotBeNull();
            }
            foreach (var who in new[] { approver, requester })
            {
                var narrow = await View(who, list: list);
                (narrow.CreditLimit, narrow.CreditUsed, narrow.CreditAvailable).Should().Be((null, null, null));
                narrow.CreditTerms.Should().Be("net30");
            }
        }
        (await View(requester, new CreditOptions { BalanceVisibleTo = CompanyRoles.Requester })).CreditLimit.Should().NotBeNull();
        (await View(buyer, new CreditOptions { BalanceVisibleTo = CompanyRoles.Admin })).CreditLimit.Should().BeNull();

        // Staff keep every field.
        await using var db = Ctx();
        var staff = await new CompanyService(db).AdminGetAsync(firm.Id);
        staff.CreditLimit.Should().Be(new MoneyDto(5_000_000, "BDT"));
        staff.CreditUsed.Should().NotBeNull();
        staff.CreditAvailable.Should().NotBeNull();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var db = Ctx();
        return (await db.Database.SqlQueryRaw<int>(sql).ToListAsync()).Single();
    }
}
