using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteAcceptKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.9 races against a real database (ADR-0011 §8.6, §9; threat model QT4, QT13): two acceptances, an acceptance and a withdrawal, an
/// acceptance and a newer version being sent or approved serialise on the quote row and exactly one wins, with a 409 for the other and
/// never a 500; and accepting quotes on credit while orders are cancelled, credit payments verified and bounced and other credit orders
/// placed never deadlocks (one lock order: quotes → quote_versions → orders → credit rows → companies).
/// </summary>
public sealed class QuoteAcceptConcurrencyTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const int Rounds = 6;
    private readonly QuoteAcceptKit _kit = new(fixture);

    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);

    /// <summary>Runs both at once; each ends in success or a domain refusal (a 409 over HTTP), never anything else.</summary>
    private static async Task<(DomainException? A, DomainException? B)> RaceAsync(Func<Task> a, Func<Task> b)
    {
        using var gate = new ManualResetEventSlim(false);
        async Task<DomainException?> Run(Func<Task> step)
        {
            gate.Wait();
            try { await step(); return null; }
            catch (DomainException ex) { return ex; }
        }

        var (ta, tb) = (Task.Run(() => Run(a)), Task.Run(() => Run(b)));
        gate.Set();
        return (await ta, await tb);
    }

    private async Task WithDeskAsync(Guid staff, Func<StaffQuoteService, Task> step)
    {
        await using var db = _kit.Db();
        await step(_kit.Desk(db, staff));
    }

    [PostgresFact]
    public async Task TwoParallelAccepts_MakeExactlyOneOrder()
    {
        var t = await _kit.TeamAsync(buyers: 2);
        for (var round = 0; round < Rounds; round++)
        {
            var item = await _kit.ItemAsync();
            var quote = await _kit.SentAsync(t, [new Line(item, 2)]);
            // Two buyers of the company (different keys and buyer locks), or the same buyer clicking twice with two keys.
            var second = round % 2 == 0 ? t.Buyers[1] : t.Buyer;
            var (a, b) = await RaceAsync(() => _kit.AcceptAsync(t, quote, onCredit: true), () => _kit.AcceptAsync(t, quote, buyer: second, onCredit: true));

            new[] { a, b }.Count(e => e is null).Should().Be(1, $"round {round}: exactly one acceptance wins");
            (a ?? b)!.Code.Should().Be("QUOTE_ALREADY_ACCEPTED", $"round {round}");
            (await _kit.OrdersOfAsync(quote)).Should().ContainSingle(o => o.Status == OrderStatus.Confirmed);
            (await _kit.StockAsync(item)).Should().Be((48, 0), "the loser gave its held stock back");
            (await _kit.QuoteAsync(quote)).Version(1).AcceptedOrderId.Should().Be((await _kit.OrdersOfAsync(quote)).Single().Id);
        }
    }

    [PostgresFact]
    public async Task WithdrawRacingAnAccept_NoOrderCommitsAfterTheWithdrawal()
    {
        var t = await _kit.TeamAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var item = await _kit.ItemAsync();
            var quote = await _kit.SentAsync(t, [new Line(item)]);
            var (accept, withdraw) = await RaceAsync(() => _kit.AcceptAsync(t, quote, onCredit: true),
                () => WithDeskAsync(t.Staff, d => d.WithdrawAsync(quote, 1, new StaffReasonRequest("Supplier price went up"))));

            var v = (await _kit.QuoteAsync(quote)).Version(1);
            var orders = await _kit.OrdersOfAsync(quote);
            if (accept is null)
            {
                withdraw!.Code.Should().Be("QUOTE_INVALID_TRANSITION", $"round {round}: an accepted version is not withdrawn");
                (v.Status, orders.Count).Should().Be((QuoteVersionStatus.Accepted, 1));
            }
            else
            {
                (withdraw, accept.Code).Should().Be((null, "QUOTE_NOT_ACCEPTABLE"), $"round {round}");
                (v.Status, orders.Count).Should().Be((QuoteVersionStatus.Withdrawn, 0), "no order commits after the withdrawal");
                (await _kit.StockAsync(item)).Should().Be((50, 0));
            }
        }
    }

    [PostgresFact]
    public async Task SendingANewVersionRacingAnAccept_ExactlyOneWins()
    {
        var t = await _kit.TeamAsync();
        var approver = await _kit.UserAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var item = await _kit.ItemAsync();
            var quote = await _kit.SentAsync(t, [new Line(item, 2)]);
            // Even rounds: version 2 is sent directly. Odd rounds: version 2 (31 days) waits for approval and the approval sends it.
            var viaApproval = round % 2 == 1;
            await WithDeskAsync(t.Staff, async d =>
            {
                await d.ReviseQuoteAsync(quote);
                await EditAsync(d, quote, 2, [new Line(item, 3)], validityDays: viaApproval ? 31 : 15);
                if (viaApproval) (await d.SendAsync(quote, 2)).Status.Should().Be("pending_approval");
            });
            var hash2 = (await _kit.VersionAsync(quote, 2)).ContentHash;
            Func<Task> issue = viaApproval
                ? () => WithDeskAsync(approver, d => d.ApproveAsync(quote, 2, new ApproveQuoteRequest(hash2, [], [], "long project")))
                : () => WithDeskAsync(t.Staff, d => d.SendAsync(quote, 2));

            var (accept, sent) = await RaceAsync(() => _kit.AcceptAsync(t, quote, n: 1, onCredit: true), issue);

            var q = await _kit.QuoteAsync(quote);
            var orders = await _kit.OrdersOfAsync(quote);
            if (accept is null)
            {
                sent!.Code.Should().Be("QUOTE_ALREADY_ACCEPTED", $"round {round}: a version with a live order is not superseded");
                (q.Version(1).Status, q.Version(2).Status, orders.Count).Should().Be(
                    (QuoteVersionStatus.Accepted, viaApproval ? QuoteVersionStatus.PendingApproval : QuoteVersionStatus.Draft, 1));
            }
            else
            {
                (sent, accept.Code).Should().Be((null, "QUOTE_NOT_ACCEPTABLE"), $"round {round}");
                (q.Version(1).Status, q.Version(2).Status, orders.Count).Should().Be((QuoteVersionStatus.Superseded, QuoteVersionStatus.Sent, 0));
            }
        }
    }

    // ---- the global lock order ---------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnAcceptance_WaitsForTheQuoteRowBeforeItTakesTheCompanyRow()
    {
        // §9: quotes → quote_versions before companies. While a quote command holds the quote row, a credit acceptance of that quote waits
        // for it holding no company lock, so every credit, payment and cancel path of the company goes on meanwhile.
        var t = await _kit.TeamAsync();
        var quote = await _kit.SentAsync(t, [new Line(await _kit.ItemAsync())]);
        await using var holder = _kit.Db();
        await using var quoteLock = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM quotes WHERE id = {quote} FOR NO KEY UPDATE");

        var accept = Task.Run(() => _kit.AcceptAsync(t, quote, onCredit: true));
        await WaitingForALockAsync(accept);
        await using (var probe = _kit.Db())
        {
            await using var companyLock = await probe.Database.BeginTransactionAsync();
            var free = () => probe.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {t.Company} FOR UPDATE NOWAIT");
            await free.Should().NotThrowAsync("the waiting acceptance holds no company lock");
            await companyLock.RollbackAsync();
        }

        await quoteLock.RollbackAsync();
        (await accept).Status.Should().Be("confirmed");
    }

    [PostgresFact]
    public async Task AWithdrawalThatCommitsWhileTheAcceptanceWaits_IsSeenUnderTheQuoteLock()
    {
        // The deterministic form of the race: the early check saw the version Sent; the decision that binds is the one taken after the
        // quote row is locked, on the rows as they are then.
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        await using var holder = _kit.Db();
        await using var withdrawal = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM quotes WHERE id = {quote} FOR NO KEY UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($@"UPDATE quote_versions SET status = 'Withdrawn', withdrawn_by = {t.Staff}, withdrawn_at = now(),
            withdraw_reason = 'Supplier price went up', updated_at = now() WHERE quote_id = {quote} AND version_no = 1");

        var accept = Task.Run(() => _kit.AcceptAsync(t, quote, onCredit: true));
        await WaitingForALockAsync(accept);
        await withdrawal.CommitAsync();

        (await FluentActions.Awaiting(() => accept).Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("QUOTE_NOT_ACCEPTABLE");
        (await _kit.OrdersOfAsync(quote)).Should().BeEmpty("no order commits after the withdrawal");
        (await _kit.StockAsync(item)).Should().Be((50, 0), "the stock it held is back");
    }

    /// <summary>Returns once a session of this test database waits for a row lock (the acceptance at the quote row), which has not finished.</summary>
    private async Task WaitingForALockAsync(Task accept)
    {
        await using var probe = _kit.Db();
        var limit = Task.Delay(TimeSpan.FromSeconds(20));
        while (await probe.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync() == 0)
        {
            limit.IsCompleted.Should().BeFalse("the acceptance should reach the quote lock and wait there");
            accept.IsCompleted.Should().BeFalse("the acceptance must wait for the quote row");
            await Task.Delay(50);
        }
    }

    private async Task<T> CreditPayments<T>(Guid actor, Func<CreditPaymentService, Task<T>> action)
    {
        await using var db = _kit.Db();
        return await action(new CreditPaymentService(db, new Who(actor, "corr-credit"), new CreditOptions(), TimeProvider.System));
    }

    private async Task<Guid> RecordedAsync(Guid company, long amount) =>
        (await CreditPayments(await _kit.UserAsync(), s => s.RecordAsync(company,
            new RecordCreditPaymentRequest("rtgs", amount, Today, "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()), Key()))).Payment.Id;

    private async Task<PlacedOrder> CreditOrderAsync(Team t, Guid buyer, Item item)
    {
        await using var db = _kit.Db();
        return await _kit.Orders(db, buyer).PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(item.Variant, 1)], null, t.Company, PayOnCredit: true));
    }

    private async Task CancelConfirmedAsync(Guid order, Guid staff)
    {
        await using var db = _kit.Db();
        var who = new Who(staff, "corr-cancel");
        await new OrderCancellationService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System),
            new CreditLedgerService(db, who, TimeProvider.System), who, TimeProvider.System).CancelConfirmedAsync(order, new CancelConfirmedOrderRequest("customer called: wrong part"));
    }

    [PostgresFact]
    public async Task AcceptCancelVerifyAndPlace_InParallel_NeverDeadlock_AndTheLedgerAddsUp()
    {
        // One company, all at once: 6 acceptances on credit (quote → versions → order → company), 3 withdrawals of other quotes (quote tier
        // only), 6 staff cancels of confirmed credit orders (order → company → promotions), 4 verifications and 4 bounces (payment → company)
        // and 4 plain credit orders (company). No 40P01, no lost update; every acceptance and every withdrawal is whole.
        var t = await _kit.TeamAsync(buyers: 11);
        var accepts = new List<(Guid Quote, Guid Buyer)>();
        for (var i = 0; i < 6; i++) accepts.Add((await _kit.SentAsync(t, [new Line(await _kit.ItemAsync(), 1)]), t.Buyers[i]));
        var withdrawals = new List<Guid>();
        for (var i = 0; i < 3; i++) withdrawals.Add(await _kit.SentAsync(t, [new Line(await _kit.ItemAsync(), 1)]));
        var cancels = new List<PlacedOrder>();
        for (var i = 0; i < 6; i++) cancels.Add(await CreditOrderAsync(t, t.Buyers[6], await _kit.ItemAsync()));
        var toBounce = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var payment = await RecordedAsync(t.Company, 100_000);
            await CreditPayments(await _kit.UserAsync(), s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
            toBounce.Add(payment);
        }

        var toVerify = new List<Guid>();
        for (var i = 0; i < 4; i++) toVerify.Add(await RecordedAsync(t.Company, 90_000));
        var fresh = new List<Item>();
        for (var i = 0; i < 4; i++) fresh.Add(await _kit.ItemAsync());
        var (staff, verifier, bouncer) = (await _kit.UserAsync(), await _kit.UserAsync(), await _kit.UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var work = new List<Task>();
        work.AddRange(accepts.Select(a => Task.Run(async () => { gate.Wait(); (await _kit.AcceptAsync(t, a.Quote, buyer: a.Buyer, onCredit: true)).Status.Should().Be("confirmed"); })));
        work.AddRange(withdrawals.Select(q => Task.Run(async () => { gate.Wait(); await WithDeskAsync(t.Staff, d => d.WithdrawAsync(q, 1, new StaffReasonRequest("Price list changed"))); })));
        work.AddRange(cancels.Select(o => Task.Run(async () => { gate.Wait(); await CancelConfirmedAsync(o.OrderId, staff); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await CreditPayments(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.AddRange(toBounce.Select(p => Task.Run(async () => { gate.Wait(); await CreditPayments(bouncer, s => s.BounceAsync(p, new BounceCreditPaymentRequest("cheque returned unpaid"))); })));
        work.AddRange(fresh.Select((item, i) => Task.Run(async () => { gate.Wait(); (await CreditOrderAsync(t, t.Buyers[7 + i], item)).Status.Should().Be("confirmed"); })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        await all;   // any 40P01, lost update or refused step throws here

        await using var db = _kit.Db();
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == t.Company);
        var lines = await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == t.Company).OrderBy(l => l.Seq).ToListAsync();
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i));
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor));
        company.CreditOnAccountMinor.Should().Be(lines.Sum(l => l.OnAccountDeltaMinor));
        foreach (var (quote, _) in accepts)
        {
            var order = (await _kit.OrdersOfAsync(quote)).Should().ContainSingle().Subject;
            order.Status.Should().Be(OrderStatus.Confirmed);
            lines.Where(l => l.OrderId == order.Id && l.Kind == CreditLedgerKind.Charge).Should().ContainSingle();
            (await _kit.QuoteAsync(quote)).Version(1).AcceptedOrderId.Should().Be(order.Id);
        }

        foreach (var quote in withdrawals) (await _kit.QuoteAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Withdrawn);
        foreach (var o in cancels) lines.Where(l => l.OrderId == o.OrderId).Sum(l => l.ReceivableDeltaMinor).Should().Be(0, "a cancelled order owes nothing");
    }
}
