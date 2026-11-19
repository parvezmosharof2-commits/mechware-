using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Cart;
using Mechware.Application.Companies;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Quotes;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.5 service level (threat model QT10): the caps are counted under the per-company advisory lock, so parallel requests cannot all pass
/// them; a parallel replay is one RFQ; an unverified account or company is refused; the cart's later contents do not break a replay.
/// Product requests are used throughout, which need no cart and no parser.
/// </summary>
public sealed class BuyerRfqServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 3_960_000_000;

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    private sealed class Caller(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.45";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-d45";
        public bool HasPermission(string permission) => false;
    }

    private sealed class FakeCart(Func<IReadOnlyList<CartLineView>> lines) : ICartService
    {
        public Task<CartView> GetAsync(CartOwner owner, CancellationToken ct = default)
        {
            var l = lines();
            return Task.FromResult(new CartView(l, [], l.Count, l.Sum(x => x.Quantity), 0, CartView.CurrencyCode, null));
        }

        public Task<CartView> ApplyAsync(CartOwner owner, CartOperation operation, string? ifMatch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CartView> ClearAsync(CartOwner owner, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoParser : IQuickOrderValidator
    {
        public Task<QuickOrderValidation> ValidateAsync(string? text, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private async Task<(Guid Owner, Guid Company)> FirmAsync(bool activate = true, bool verifiedOwner = true)
    {
        var owner = await TestUsers.CreateAsync(fixture, verifiedOwner);
        await using var db = fixture.CreateContext();
        var bin = (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner, new RegisterCompanyRequest("Rfq Service Ltd " + bin[^5..], "TL-RS-" + bin[^5..], bin, null, null))).Id;
        if (activate) await companies.AdminActivateAsync(id);
        return (owner, id);
    }

    private async Task<Guid> VariantAsync()
    {
        await using var db = fixture.CreateContext();
        return (await CatalogTestData.SeedVariantAsync(db, 10)).VariantId;
    }

    private async Task<RfqCreated> AskAsync(Guid user, Guid company, Guid variant, string? key = null, QuoteOptions? options = null, TimeProvider? clock = null,
        string? message = null, Func<IReadOnlyList<CartLineView>>? cart = null, string source = "product")
    {
        await using var db = fixture.CreateContext();
        var service = new BuyerRfqService(db, new NoParser(), new FakeCart(cart ?? (() => [])), options ?? new QuoteOptions(), new Caller(user), clock ?? TimeProvider.System);
        return await service.CreateAsync(new CreateRfqRequest(user, company, key ?? "k-" + Guid.NewGuid().ToString("N"), source, null, variant, 3, message, null, null));
    }

    private static async Task<string> CodeOfAsync(Func<Task> action)
    {
        var ex = (await FluentActions.Awaiting(action).Should().ThrowAsync<DomainException>()).Which;
        return ex.Code;
    }

    [PostgresFact]
    public async Task RfqIsRateLimited_PerCompany_UnderTheLock_SoParallelRequestsCannotAllPass()
    {
        var (owner, company) = await FirmAsync();
        var variant = await VariantAsync();
        var options = new QuoteOptions { MaxRfqsPerCompanyPerHour = 4, MaxOpenRfqsPerCompany = 100 };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 14).Select(async _ =>
        {
            try { await AskAsync(owner, company, variant, options: options); return "ok"; }
            catch (TooManyRequestsException ex) { return ex.Code; }
        }));

        outcomes.Count(o => o == "ok").Should().Be(4, "the count and the insert are one step under the company lock");
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "RFQ_RATE_LIMITED");
        await using var db = fixture.CreateContext();
        (await db.Rfqs.CountAsync(r => r.CompanyId == company)).Should().Be(4);
    }

    [PostgresFact]
    public async Task TheHourlyCap_IsAWindow_AndTheOpenCapFreesWhenOneIsWithdrawn()
    {
        var (owner, company) = await FirmAsync();
        var variant = await VariantAsync();
        var clock = new Clock(DateTime.UtcNow);
        var options = new QuoteOptions { MaxRfqsPerCompanyPerHour = 2, MaxOpenRfqsPerCompany = 100 };
        await AskAsync(owner, company, variant, options: options, clock: clock);
        await AskAsync(owner, company, variant, options: options, clock: clock);
        (await CodeOfAsync(() => AskAsync(owner, company, variant, options: options, clock: clock))).Should().Be("RFQ_RATE_LIMITED");
        clock.Now = clock.Now.AddMinutes(61);
        await AskAsync(owner, company, variant, options: options, clock: clock);   // the first two are older than an hour now

        var (owner2, company2) = await FirmAsync();
        var open = new QuoteOptions { MaxRfqsPerCompanyPerHour = 100, MaxOpenRfqsPerCompany = 2 };
        var first = await AskAsync(owner2, company2, variant, options: open);
        await AskAsync(owner2, company2, variant, options: open);
        (await CodeOfAsync(() => AskAsync(owner2, company2, variant, options: open))).Should().Be("TOO_MANY_OPEN_RFQS");
        await using (var db = fixture.CreateContext())
            await new BuyerRfqService(db, new NoParser(), new FakeCart(() => []), open, new Caller(owner2), TimeProvider.System).WithdrawAsync(owner2, company2, first.Rfq.Rfq.Id);
        await AskAsync(owner2, company2, variant, options: open);
    }

    [PostgresFact]
    public async Task RfqReplay_InParallel_WithTheSameKey_CreatesOne()
    {
        var (owner, company) = await FirmAsync();
        var variant = await VariantAsync();
        var key = "same-key-" + Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AskAsync(owner, company, variant, key)));

        results.Select(r => r.Rfq.Rfq.Id).Distinct().Should().ContainSingle();
        results.Count(r => !r.Replayed).Should().Be(1, "one request created it, the others got it back");
        await using var db = fixture.CreateContext();
        (await db.Rfqs.CountAsync(r => r.CompanyId == company)).Should().Be(1);
        (await db.OutboxMessages.CountAsync(o => o.EventType == nameof(RfqSubmittedEvent) && o.AggregateId == results[0].Rfq.Rfq.Id)).Should().Be(1);
    }

    [PostgresFact]
    public async Task AnUnverifiedAccountOrCompany_CannotRequestAQuote()
    {
        var variant = await VariantAsync();
        var (unverifiedUser, company) = await FirmAsync(verifiedOwner: false);
        (await CodeOfAsync(() => AskAsync(unverifiedUser, company, variant))).Should().Be("ACCOUNT_NOT_VERIFIED");
        var (owner, pending) = await FirmAsync(activate: false);
        (await CodeOfAsync(() => AskAsync(owner, pending, variant))).Should().Be("COMPANY_NOT_VERIFIED");
        var stranger = await TestUsers.CreateAsync(fixture);
        await FluentActions.Awaiting(() => AskAsync(stranger, company, variant)).Should().ThrowAsync<KeyNotFoundException>().WithMessage(BuyerRfqService.NotFoundMessage);

        await using var db = fixture.CreateContext();
        (await db.Rfqs.CountAsync(r => r.CompanyId == company || r.CompanyId == pending)).Should().Be(0);
    }

    [PostgresFact]
    public async Task ARequestFromTheCart_IsReplayedEvenWhenTheCartChangedSince()
    {
        var (owner, company) = await FirmAsync();
        var first = await VariantAsync();
        var second = await VariantAsync();
        var lines = new List<CartLineView> { new(first, "SKU-A", "A", 2, 100, 200, 1, 1) };
        var key = "cart-key-" + Guid.NewGuid().ToString("N");

        var created = await AskAsync(owner, company, first, key, source: "cart", cart: () => lines);
        lines.Add(new CartLineView(second, "SKU-B", "B", 1, 100, 100, 1, 1));
        var replay = await AskAsync(owner, company, first, key, source: "cart", cart: () => lines);

        created.Replayed.Should().BeFalse();
        replay.Replayed.Should().BeTrue();
        replay.Rfq.Rfq.Id.Should().Be(created.Rfq.Rfq.Id);
        replay.Rfq.Lines.Should().ContainSingle("the RFQ keeps the cart as it was when it was asked");
    }

    [PostgresFact]
    public async Task Withdraw_RecordsTheActor_InTheAuditRow_AndAMemberOfAnotherCompanyCannotReachIt()
    {
        var (owner, company) = await FirmAsync();
        var (otherOwner, otherCompany) = await FirmAsync();
        var variant = await VariantAsync();
        var rfq = (await AskAsync(owner, company, variant, message: "Please quote\u0000 this")).Rfq;
        rfq.Rfq.Message.Should().Be("Please quote this");

        await using (var db = fixture.CreateContext())
        {
            var service = new BuyerRfqService(db, new NoParser(), new FakeCart(() => []), new QuoteOptions(), new Caller(otherOwner), TimeProvider.System);
            await FluentActions.Awaiting(() => service.WithdrawAsync(otherOwner, company, rfq.Rfq.Id)).Should().ThrowAsync<KeyNotFoundException>();
            await FluentActions.Awaiting(() => service.WithdrawAsync(otherOwner, otherCompany, rfq.Rfq.Id)).Should().ThrowAsync<KeyNotFoundException>("the id of another company's RFQ under one's own company");
        }

        await using (var db = fixture.CreateContext())
        {
            var done = await new BuyerRfqService(db, new NoParser(), new FakeCart(() => []), new QuoteOptions(), new Caller(owner), TimeProvider.System).WithdrawAsync(owner, company, rfq.Rfq.Id);
            done.Rfq.Status.Should().Be("withdrawn");
        }

        await using var check = fixture.CreateContext();
        var audit = await check.AuditLogs.AsNoTracking().Where(a => a.Action == "rfq.withdrawn" && a.EntityId == rfq.Rfq.Id.ToString()).ToListAsync();
        audit.Should().ContainSingle().Which.Should().Match<Mechware.Domain.Identity.AuditLog>(a => a.ActorId == owner && a.ActorIp == "203.0.113.45" && a.CorrelationId == "corr-d45");
        (await check.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfq.Rfq.Id)).WithdrawnAtUtc.Should().NotBeNull();
    }
}
