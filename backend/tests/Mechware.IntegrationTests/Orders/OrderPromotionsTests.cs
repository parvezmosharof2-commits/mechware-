using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

public class OrderPromotionsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<T> With<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System, ShippingTestKit.DevelopmentQuoter(db)));
    }

    private async Task<SeededVariant> SeedAsync(int onHand = 50, long price = 100_000)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    /// <summary>Promotions are global in the table, so each test gives its own a unique code and scopes automatic ones to its own variant.</summary>
    private async Task<PromotionDefinition> AddPromotionAsync(Func<string, PromotionDefinition> make)
    {
        var code = "T" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var promotion = make(code);
        await using var db = fixture.CreateContext();
        db.Promotions.Add(promotion);
        await db.SaveChangesAsync();
        return promotion;
    }

    private static PromotionDefinition Percent(string? code, decimal pct, PromotionScope scope, int? global = null, int? perUser = null, bool active = true) =>
        PromotionDefinition.Create(Guid.NewGuid(), "Promo " + code, code, PromotionKind.PercentOff, scope, percent: pct, globalLimit: global, perUserLimit: perUser);

    private static PlaceOrderRequest Req(Guid user, Guid variant, int qty = 1, params string[] codes) =>
        new(user, Key(), [new PlaceOrderLine(variant, qty)], null, null, false, codes);

    private Task<Guid> Buyer() => TestUsers.CreateAsync(fixture);

    private async Task<(int Redeemed, int UserUses)> UsageAsync(Guid promotion, Guid? user = null)
    {
        await using var db = fixture.CreateContext();
        var p = await db.Promotions.AsNoTracking().SingleAsync(x => x.Id == promotion);
        var uses = user is null ? 0 : (await db.PromotionUserUsage.AsNoTracking().Where(u => u.PromotionId == promotion && u.UserId == user).Select(u => (int?)u.Uses).FirstOrDefaultAsync()) ?? 0;
        return (p.RedeemedCount, uses);
    }

    [PostgresFact]
    public async Task ACouponTakesMoneyOff_ByTheServersMath_AndIsRecordedOnTheOrder()
    {
        var v = await SeedAsync(price: 100_000);
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(code => Percent(code, 10, new PromotionScope(PromotionScopeKind.Variant, v.VariantId)));

        var placed = await With(s => s.PlaceAsync(Req(buyer, v.VariantId, 2, promo.Code!.ToLowerInvariant())));   // case does not matter

        placed.GrandTotalMinor.Should().Be(180_000, "2 x 1,000.00 less 10 %, VAT inside");
        await using var db = fixture.CreateContext();
        var item = await db.OrderItems.AsNoTracking().SingleAsync(i => i.VariantId == v.VariantId);
        (item.GrossMinor, item.DiscountMinor, item.TotalMinor).Should().Be((200_000, 20_000, 180_000));
        (await db.PromotionRedemptions.AsNoTracking().SingleAsync(r => r.OrderId == placed.OrderId)).DiscountMinor.Should().Be(20_000);
        (await UsageAsync(promo.Id, buyer)).Should().Be((1, 1));
    }

    [PostgresFact]
    public async Task AnAutomaticPromotion_AppliesWithoutACode()
    {
        var v = await SeedAsync(price: 100_000);
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(_ => Percent(null, 5, new PromotionScope(PromotionScopeKind.Variant, v.VariantId)));

        var placed = await With(s => s.PlaceAsync(Req(buyer, v.VariantId)));

        placed.GrandTotalMinor.Should().Be(95_000);
        (await UsageAsync(promo.Id)).Redeemed.Should().Be(1);
    }

    [PostgresFact]
    public async Task AnUnknownOrInactiveCode_IsAnErrorTheBuyerSees_NotSilentlyIgnored()
    {
        var v = await SeedAsync();
        var buyer = await Buyer();
        var inactive = await AddPromotionAsync(code => Percent(code, 10, PromotionScope.Everything));
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE promotions SET is_active = false WHERE id = {inactive.Id}");

        foreach (var code in new[] { "NO-SUCH-CODE", inactive.Code! })
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, code)));
            (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("COUPON_INVALID");
        }

        (await Stock(v.VariantId)).Should().Be(0, "a refused order holds no stock");
    }

    [PostgresFact]
    public async Task ACouponThatDoesNotApplyToTheBasket_IsRefusedWithItsReason()
    {
        var v = await SeedAsync();
        var other = await SeedAsync();
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(code => Percent(code, 10, new PromotionScope(PromotionScopeKind.Variant, other.VariantId)));

        var act = () => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!)));

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("COUPON_" + PromotionEngine.NoEligibleLines);
    }

    [PostgresFact]
    public async Task APerUserLimit_StopsTheSecondUse_ByTheSameBuyer_ButNotAnotherBuyer()
    {
        var v = await SeedAsync();
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(code => Percent(code, 10, new PromotionScope(PromotionScopeKind.Variant, v.VariantId), perUser: 1));

        await With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!)));
        var again = () => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!)));
        (await again.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("COUPON_" + PromotionEngine.PerUserLimitReached);

        var other = await Buyer();
        (await With(s => s.PlaceAsync(Req(other, v.VariantId, 1, promo.Code!)))).GrandTotalMinor.Should().Be(90_000);
    }

    [PostgresFact]
    public async Task TwentyBuyersRacingForTheLastFiveUses_ExactlyFiveWin_AndTheRestGetNothingReserved()
    {
        var promo = await AddPromotionAsync(code => Percent(code, 10, PromotionScope.Everything, global: 5));
        // One variant per buyer so nothing else serialises them: only the usage claim stands in the way.
        var variants = new List<SeededVariant>();
        var buyers = new List<Guid>();
        for (var i = 0; i < 20; i++) { variants.Add(await SeedAsync(onHand: 3)); buyers.Add(await Buyer()); }

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            try { await With(s => s.PlaceAsync(Req(buyers[i], variants[i].VariantId, 1, promo.Code!))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o == "ok").Should().Be(5);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o.StartsWith("COUPON_"));
        (await UsageAsync(promo.Id)).Redeemed.Should().Be(5, "never more uses than the limit");
        for (var i = 0; i < 20; i++)
            if (outcomes[i] != "ok") (await Stock(variants[i].VariantId)).Should().Be(0, "a refused order must give its stock back");
    }

    [PostgresFact]
    public async Task OneBuyerRacingTheirOwnLimit_GetsExactlyOneUse()
    {
        var v = await SeedAsync(onHand: 50);
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(code => Percent(code, 10, new PromotionScope(PromotionScopeKind.Variant, v.VariantId), perUser: 1));

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            try { await With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!))); return "ok"; }   // distinct idempotency keys
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o == "ok").Should().Be(1);
        (await UsageAsync(promo.Id, buyer)).UserUses.Should().Be(1);
    }

    [PostgresFact]
    public async Task CancellingTheOrder_GivesThePromotionUseBack()
    {
        var v = await SeedAsync();
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(code => Percent(code, 10, new PromotionScope(PromotionScopeKind.Variant, v.VariantId), global: 1, perUser: 1));
        var placed = await With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!)));
        (await UsageAsync(promo.Id, buyer)).Should().Be((1, 1));

        await With(s => s.CancelAsync(buyer, placed.OrderId, "changed my mind"));

        (await UsageAsync(promo.Id, buyer)).Should().Be((0, 0));
        await using var db = fixture.CreateContext();
        (await db.PromotionRedemptions.CountAsync(r => r.OrderId == placed.OrderId)).Should().Be(0);
        (await With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!)))).GrandTotalMinor.Should().Be(90_000, "the single use is available again");
    }

    [PostgresFact]
    public async Task APromotionCanNeverMakeAnOrderCostLess_ThanItsMinimumOrNegative()
    {
        var v = await SeedAsync(price: 10_000);
        var buyer = await Buyer();
        var promo = await AddPromotionAsync(code => PromotionDefinition.Create(Guid.NewGuid(), "Huge", code, PromotionKind.FixedOff,
            new PromotionScope(PromotionScopeKind.Variant, v.VariantId), fixedMinor: 999_999_999));

        var placed = await With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, promo.Code!)));

        placed.GrandTotalMinor.Should().Be(0, "a discount stops at the value of the goods");
    }

    [PostgresFact]
    public async Task TooManyCouponCodes_AreRefused()
    {
        var v = await SeedAsync();
        var buyer = await Buyer();
        var act = () => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, "AAA", "BBB", "CCC", "DDD")));
        await act.Should().ThrowAsync<ValidationException>();
    }

    // ---- Threat model v2, R1: coupon codes cannot be found by guessing ----

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<T> At<T>(DateTime now, Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), new FixedClock(now), ShippingTestKit.DevelopmentQuoter(db)));
    }

    private static async Task<ConflictException> Refused(Func<Task> act) => (await act.Should().ThrowAsync<ConflictException>()).Which;

    [PostgresFact]
    public async Task AnExpiredNotYetStartedOrUsedUpCode_LooksExactlyLikeOneThatNeverExisted()
    {
        var v = await SeedAsync();
        var now = DateTime.UtcNow;
        var expired = await AddPromotionAsync(c => PromotionDefinition.Create(Guid.NewGuid(), "Old", c, PromotionKind.PercentOff, PromotionScope.Everything,
            percent: 10, startsAtUtc: now.AddDays(-10), endsAtUtc: now.AddDays(-1)));
        var future = await AddPromotionAsync(c => PromotionDefinition.Create(Guid.NewGuid(), "Soon", c, PromotionKind.PercentOff, PromotionScope.Everything,
            percent: 10, startsAtUtc: now.AddDays(1), endsAtUtc: now.AddDays(10)));
        var usedUp = await AddPromotionAsync(c => Percent(c, 10, PromotionScope.Everything, global: 1));
        var first = await Buyer();
        await With(s => s.PlaceAsync(Req(first, v.VariantId, 1, usedUp.Code!)));   // someone else took the only use

        var buyer = await Buyer();
        var never = await Refused(() => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, "NOSUCHCODE1"))));
        foreach (var code in new[] { expired.Code!, future.Code!, usedUp.Code! })
        {
            var refused = await Refused(() => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, code))));
            refused.Code.Should().Be(never.Code).And.Be("COUPON_INVALID");
            refused.Message.Replace(code, "X").Should().Be(never.Message.Replace("NOSUCHCODE1", "X"), "the wording must not tell a real code from a made-up one");
        }
    }

    [PostgresFact]
    public async Task MixingARealCodeWithAGuess_DoesNotRevealTheRealOne_AndEveryGuessIsCounted()
    {
        var v = await SeedAsync();
        var now = DateTime.UtcNow;
        var expired = await AddPromotionAsync(c => PromotionDefinition.Create(Guid.NewGuid(), "Old", c, PromotionKind.PercentOff, PromotionScope.Everything,
            percent: 10, startsAtUtc: now.AddDays(-10), endsAtUtc: now.AddDays(-1)));
        var buyer = await Buyer();

        var refused = await Refused(() => With(s => s.PlaceAsync(Req(buyer, v.VariantId, 1, "GUESSA1", expired.Code!, "GUESSB2"))));
        refused.Code.Should().Be("COUPON_INVALID");
        refused.Message.Should().Contain("GUESSA1").And.Contain("GUESSB2").And.Contain(expired.Code!, "the expired code is named exactly like the made-up ones");

        await using var db = fixture.CreateContext();
        (await db.CouponFailureWindows.AsNoTracking().SingleAsync(w => w.UserId == buyer)).Failures.Should().Be(3, "three codes were guessed, not one");
    }

    [PostgresFact]
    public async Task TooManyRefusedCodes_StopThatUserForAnHour_ButNoOneElse()
    {
        var v = await SeedAsync();
        var good = await AddPromotionAsync(c => Percent(c, 10, PromotionScope.Everything));
        var prober = await Buyer();
        var now = DateTime.UtcNow;

        for (var i = 0; i < OrderService.MaxCouponFailuresPerHour; i++)
            (await Refused(() => At(now, s => s.PlaceAsync(Req(prober, v.VariantId, 1, $"GUESS{i:D4}"))))).Code.Should().Be("COUPON_INVALID");

        // Even a real code is now refused unread, so further guessing learns nothing.
        (await Refused(() => At(now.AddMinutes(30), s => s.PlaceAsync(Req(prober, v.VariantId, 1, good.Code!))))).Code.Should().Be("COUPON_ATTEMPTS_EXCEEDED");
        (await At(now.AddMinutes(30), s => s.PlaceAsync(Req(prober, v.VariantId, 1)))).OrderId.Should().NotBeEmpty("an order without a code is never blocked");
        var other = await Buyer();
        (await At(now.AddMinutes(30), s => s.PlaceAsync(Req(other, v.VariantId, 1, good.Code!)))).GrandTotalMinor.Should().Be(90_000, "other buyers are unaffected");

        // A new window starts after an hour: the count starts again from one, not from where it stopped...
        var later = now.AddMinutes(61);
        (await Refused(() => At(later, s => s.PlaceAsync(Req(prober, v.VariantId, 1, "GUESSAGAIN"))))).Code.Should().Be("COUPON_INVALID");
        (await At(later, s => s.PlaceAsync(Req(prober, v.VariantId, 1, good.Code!)))).GrandTotalMinor.Should().Be(90_000);

        // ...and the limit applies again in that window, not only once in an account's life.
        for (var i = 1; i < OrderService.MaxCouponFailuresPerHour; i++)
            (await Refused(() => At(later.AddMinutes(1), s => s.PlaceAsync(Req(prober, v.VariantId, 1, $"AGAIN{i:D4}"))))).Code.Should().Be("COUPON_INVALID");
        (await Refused(() => At(later.AddMinutes(2), s => s.PlaceAsync(Req(prober, v.VariantId, 1, good.Code!))))).Code.Should().Be("COUPON_ATTEMPTS_EXCEEDED");
    }

    [PostgresFact]
    public async Task ParallelRefusals_AreAllCounted()
    {
        var v = await SeedAsync();
        var prober = await Buyer();
        await Task.WhenAll(Enumerable.Range(0, OrderService.MaxCouponFailuresPerHour).Select(i =>
            Task.Run(async () => { try { await With(s => s.PlaceAsync(Req(prober, v.VariantId, 1, $"RACE{i:D4}"))); } catch (ConflictException) { } })));

        await using var db = fixture.CreateContext();
        (await db.CouponFailureWindows.AsNoTracking().SingleAsync(w => w.UserId == prober)).Failures.Should().Be(OrderService.MaxCouponFailuresPerHour);
    }

    private async Task<int> Stock(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        return (await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == variantId)).Reserved;
    }
}
