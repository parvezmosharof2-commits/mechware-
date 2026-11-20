using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Approvals;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Approvals;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Approvals;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteAcceptKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.9 against a real database, service level (ADR-0011 §7, §8, §9, §11; threat model QT2, QT4, QT5, QT11, QT12, QT14, QT15, QT16): a sent
/// quote version becomes one order of all its lines through the normal order path, at the quoted net prices with today's VAT rate, with no
/// promotion, the company's approval rule, credit check and hold, spend limit and budget, stock reservation and the open-unpaid cap; one
/// live order per version; re-acceptance only after an order lapsed before confirmation; validity on the Dhaka day boundary.
/// </summary>
public sealed class QuoteAcceptanceServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private readonly QuoteAcceptKit _kit = new(fixture);

    private static DateTime DhakaMidnightUtc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-6);

    private async Task<Guid> UnpaidOrderAsync(Team t, Guid buyer, Item item, int quantity = 1)
    {
        await using var db = _kit.Db();
        var placed = await _kit.Orders(db, buyer).PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(item.Variant, quantity)], null, t.Company));
        placed.Status.Should().Be("pending_payment");
        return placed.OrderId;
    }

    private async Task CancelAsync(Guid buyer, Guid order)
    {
        await using var db = _kit.Db();
        await _kit.Orders(db, buyer).CancelAsync(buyer, order, "changed my mind");
    }

    /// <summary>A confirmed credit charge of <paramref name="daysAgo"/> days ago (Net-30), so the company is on hold from 46 days.</summary>
    private async Task OverdueChargeAsync(Team t, long amount, int daysAgo)
    {
        await using var db = _kit.Db();
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount);
        var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == t.Company).ToListAsync();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(company, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
    }

    private async Task<List<CreditLedgerEntry>> LedgerAsync(Guid company)
    {
        await using var db = _kit.Db();
        return await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync();
    }

    /// <summary>The company's approver decides the order's request; the order service applies it at <paramref name="at"/>.</summary>
    private async Task DecideAsync(Team t, Guid order, bool approve, DateTime? at = null)
    {
        await using var db = _kit.Db();
        var request = await db.ApprovalRequests.AsNoTracking().Where(r => r.SubjectType == "order" && r.SubjectId == order).Select(r => r.Id).SingleAsync();
        TimeProvider clock = at is { } when ? new FixedClock(when) : TimeProvider.System;
        await new ApprovalService(db, clock, _kit.Orders(db, t.Approver, at)).DecideAsync(t.Approver, t.Company, request, new DecisionRequest(approve, null));
    }

    // ---- QT2: the snapshot, never a price of now ------------------------------------------------------------------------

    [PostgresFact]
    public async Task AcceptingUsesTheSnapshotPrices_NotTheCatalogueOrAContractOfNow()
    {
        var t = await _kit.TeamAsync();
        var (bearing, belt) = (await _kit.ItemAsync(), await _kit.ItemAsync(grossMinor: 52_500));
        var quote = await _kit.SentAsync(t, [new Line(bearing, 4, bearing.Net - 100), new Line(belt, 3)]);
        var sent = await _kit.VersionAsync(quote);

        // After the send the catalogue price doubles, and the company gets a contract at half the price for the bearing: the quoted net
        // prices still bind (the quote price wins while valid, the lines are not resolved, §8.4).
        await _kit.SqlAsync($"UPDATE product_variants SET price_minor = price_minor * 2 WHERE id IN ({bearing.Variant}, {belt.Variant})");
        await using (var lists = _kit.Db())
        {
            var from = DateTime.UtcNow.AddMinutes(-5);
            await PriceListTestData.ApprovedAsync(lists, PriceListScope.Company, t.Company, from, from.AddDays(30), from,
                new PriceListTestData.Entry(bearing.Variant, 1, bearing.Net / 2));
        }
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true);

        placed.Should().BeEquivalentTo(new { Status = "confirmed", Replayed = false, GrandTotalMinor = sent.GoodsTotalMinor });
        placed.QuoteNumber.Should().Be((await _kit.QuoteAsync(quote)).Number);
        var order = await _kit.OrderAsync(placed.OrderId);
        (order.QuoteId, order.QuoteVersionId, order.CompanyId, order.TaxMode).Should().Be((quote, sent.Id, t.Company, TaxMode.Exclusive));
        var items = order.SubOrders.SelectMany(s => s.Items).ToDictionary(i => i.VariantId);
        foreach (var line in sent.Lines)
        {
            var item = items[line.VariantId];
            item.Should().BeEquivalentTo(new
            {
                line.Quantity,
                line.UnitPriceMinor,
                line.VatRate,
                line.Sku,
                Title = line.TitleEn,
                DiscountMinor = 0L,
                NetMinor = line.LineNetMinor,
                TaxMinor = line.LineTaxMinor,
                TotalMinor = line.LineTotalMinor,
                PriceSource = (PriceSource?)PriceSource.Quote,
                PriceBasis = (PriceBasis?)PriceBasis.NetOfVat,
                PriceListId = (Guid?)null,
                ContractRef = (string?)null,
                PublicUnitPriceMinor = (long?)line.PublicUnitPriceMinor,
                QuoteLineId = (Guid?)line.Id,
            });
        }

        await using var db = _kit.Db();
        var view = await _kit.Orders(db, t.Buyer).GetAsync(t.Buyer, placed.OrderId);
        view.QuoteNumber.Should().Be(placed.QuoteNumber, "the order view names the quote it accepted");
        view.SubOrders.SelectMany(s => s.Items).Should().OnlyContain(i => i.PriceSource == "quote");

        // The contract is live: a normal order of the same bearings would be priced at it.
        var contractNet = 4 * (bearing.Net / 2);
        (await _kit.Orders(db, t.Buyer).QuoteShippingAsync(new ShippingQuoteRequest(t.Buyer, [new PlaceOrderLine(bearing.Variant, 4)], CompanyId: t.Company)))
            .GrandTotalMinor.Should().Be(contractNet + (long)decimal.Round(contractNet * 0.05m, 0, MidpointRounding.ToEven));
    }

    [PostgresFact]
    public async Task AQuoteLine_NeverGetsAPromotionOrCoupon()
    {
        var t = await _kit.TeamAsync(buyers: 2);
        var item = await _kit.ItemAsync();
        await using (var db = _kit.Db())
        {
            db.Promotions.Add(PromotionDefinition.Create(Guid.NewGuid(), "Half off this item", null, PromotionKind.PercentOff,
                new PromotionScope(PromotionScopeKind.Variant, item.Variant), percent: 50m));
            await db.SaveChangesAsync();
        }

        // The automatic promotion is live: a normal company order of the same item gets it.
        await using (var db = _kit.Db())
        {
            var normal = await _kit.Orders(db, t.Buyers[1]).PlaceAsync(new PlaceOrderRequest(t.Buyers[1], Key(), [new PlaceOrderLine(item.Variant, 2)], null, t.Company, PayOnCredit: true));
            (await _kit.OrderAsync(normal.OrderId)).DiscountMinor.Should().BeGreaterThan(0);
        }

        var quote = await _kit.SentAsync(t, [new Line(item, 2)]);
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true);
        var order = await _kit.OrderAsync(placed.OrderId);
        order.DiscountMinor.Should().Be(0);
        order.SubOrders.SelectMany(s => s.Items).Should().OnlyContain(i => i.DiscountMinor == 0 && i.PriceSource == PriceSource.Quote);
        await using (var db = _kit.Db())
            (await db.PromotionRedemptions.CountAsync(r => r.OrderId == placed.OrderId)).Should().Be(0, "a quote order claims no promotion use");
    }

    [PostgresFact]
    public async Task AnAcceptance_IsOneTransaction_WithItsAuditRowAndTheQuoteAcceptedEvent()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var sent = await _kit.VersionAsync(quote);

        var placed = await _kit.AcceptAsync(t, quote, onCredit: true, po: "PO-7781");

        var accepted = (await _kit.QuoteAsync(quote)).Version(1);
        (accepted.Status, accepted.AcceptedOrderId, accepted.AcceptedBy).Should().Be((QuoteVersionStatus.Accepted, placed.OrderId, t.Buyer));
        (await _kit.OrderAsync(placed.OrderId)).PurchaseOrderNumber.Should().Be("PO-7781");
        await using var db = _kit.Db();
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == quote.ToString() && a.Action == "quote.accepted").SingleAsync();
        (audit.ActorId, audit.ActorIp, audit.CorrelationId).Should().Be((t.Buyer, Ip, "corr-accept"));
        audit.NewValuesJson.Should().Contain(sent.ContentHash!).And.Contain(placed.OrderId.ToString());
        var events = await db.OutboxMessages.AsNoTracking().Where(o => o.AggregateId == quote || o.AggregateId == placed.OrderId)
            .Select(o => o.EventType).ToListAsync();
        events.Should().Contain([nameof(QuoteAcceptedEvent), nameof(OrderPlacedEvent)]);
        events.Count(e => e == nameof(QuoteAcceptedEvent)).Should().Be(1);
    }

    // ---- QT4: one order per version ----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AcceptingTwiceMakesOneOrder_AndAReplayWithAnotherBodyIs409()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var key = Key();

        var first = await _kit.AcceptAsync(t, quote, onCredit: true, key: key);
        var replay = await _kit.AcceptAsync(t, quote, onCredit: true, key: key, expected: 1);   // the total is not part of the request hash
        (replay.OrderId, replay.Replayed, replay.QuoteNumber).Should().Be((first.OrderId, true, first.QuoteNumber));

        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true))).Should().Be("QUOTE_ALREADY_ACCEPTED", "a second key is a second order");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: false, key: key))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, key: key, po: "PO-1"))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, key: key, hash: new string('b', 64)))).Should().Be("IDEMPOTENCY_KEY_REUSED",
            "the content hash is part of what was asked");
        (await _kit.OrdersOfAsync(quote)).Should().ContainSingle();
        (await _kit.StockAsync(item)).Should().Be((48, 0), "the credit order deducted its stock once");
    }

    [PostgresFact]
    public async Task AContactWhoLostTheBuyerRole_Is403_EvenForAReplayOfTheirOwnKey()
    {
        var t = await _kit.TeamAsync();
        var quote = await _kit.SentAsync(t, [new Line(await _kit.ItemAsync())]);
        var key = Key();
        await _kit.AcceptAsync(t, quote, onCredit: true, key: key);

        // Still the quote's contact (so the quote is visible), now only a Requester: who may accept is decided before the replay.
        await _kit.SqlAsync($"UPDATE company_members SET roles = {(int)Domain.Companies.CompanyRoles.Requester} WHERE company_id = {t.Company} AND user_id = {t.Buyer}");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, key: key))).Should().Be("COMPANY_ROLE_REQUIRED");
    }

    [PostgresFact]
    public async Task ASupersededVersionCannotBeAccepted()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item, 2)]);
        await using (var db = _kit.Db())
        {
            var desk = _kit.Desk(db, t.Staff);
            await desk.ReviseQuoteAsync(quote);
            await EditAsync(desk, quote, 2, [new Line(item, 3)]);
            (await desk.SendAsync(quote, 2)).Status.Should().Be("sent");
        }

        (await CodeOf(() => _kit.AcceptAsync(t, quote, n: 1, onCredit: true))).Should().Be("QUOTE_NOT_ACCEPTABLE");
        var placed = await _kit.AcceptAsync(t, quote, n: 2, onCredit: true);
        (await _kit.OrderAsync(placed.OrderId)).SubOrders.Single().Items.Single().Quantity.Should().Be(3);
        (await _kit.StockAsync(item)).Should().Be((47, 0), "the refused acceptance held nothing");
    }

    [PostgresFact]
    public async Task AWithdrawnOrRejectedVersionCannotBeAccepted_NorAClosedQuote()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var (withdrawn, rejected, closed) = (await _kit.SentAsync(t, [new Line(item)]), await _kit.SentAsync(t, [new Line(item)]), await _kit.SentAsync(t, [new Line(item)]));
        await using (var db = _kit.Db())
        {
            var desk = _kit.Desk(db, t.Staff);
            await desk.WithdrawAsync(withdrawn, 1, new Application.Quotes.StaffReasonRequest("Supplier price went up"));
            await desk.CloseQuoteAsync(closed, new Application.Quotes.StaffReasonRequest("Customer bought elsewhere"));
        }

        await using (var db = _kit.Db())
        {
            var q = await StaffQuoteKit.LoadAsync(db, rejected);
            q.Reject(1, t.Buyer, "Too expensive", DateTime.UtcNow);   // the buyer's reject endpoint is D4.10
            await db.SaveChangesAsync();
        }

        foreach (var quote in new[] { withdrawn, rejected, closed })
            (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true))).Should().Be("QUOTE_NOT_ACCEPTABLE");
        (await LedgerAsync(t.Company)).Should().BeEmpty();
        (await _kit.StockAsync(item)).Should().Be((50, 0));
    }

    [PostgresFact]
    public async Task AcceptWithAStaleContentHash_Is409()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var upper = (await _kit.VersionAsync(quote)).ContentHash!.ToUpperInvariant();
        (await CodeOf(() => _kit.AcceptAsync(t, quote, hash: new string('a', 64)))).Should().Be("QUOTE_CHANGED");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, hash: upper))).Should().Be("QUOTE_CHANGED", "the hash is compared exactly");
        (await _kit.OrdersOfAsync(quote)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task AQuoteWhoseOrderLapsedBeforeConfirmation_CanBeAcceptedAgainWhileValid()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)], validityDays: 1);
        var until = (await _kit.VersionAsync(quote)).ValidUntilUtc!.Value;

        var key = Key();
        var first = await _kit.AcceptAsync(t, quote, key: key);
        first.Status.Should().Be("pending_payment");
        (await CodeOf(() => _kit.AcceptAsync(t, quote))).Should().Be("QUOTE_ALREADY_ACCEPTED", "the order is live");
        await CancelAsync(t.Buyer, first.OrderId);   // never confirmed: the version is acceptable again

        (await CodeOf(() => _kit.AcceptAsync(t, quote, at: until))).Should().Be("QUOTE_NOT_ACCEPTABLE", "but only while it is valid");
        var second = await _kit.AcceptAsync(t, quote);
        var v = (await _kit.QuoteAsync(quote)).Version(1);
        (v.Status, v.AcceptedOrderId).Should().Be((QuoteVersionStatus.Accepted, second.OrderId), "the version moved to the new order");
        (await _kit.OrdersOfAsync(quote)).Select(o => o.Status).Should().BeEquivalentTo([OrderStatus.Cancelled, OrderStatus.PendingPayment]);

        // The first key still answers with the first (cancelled) order, as a cart order's key does; nothing new is placed.
        var replay = await _kit.AcceptAsync(t, quote, key: key);
        (replay.OrderId, replay.Replayed, replay.Status).Should().Be((first.OrderId, true, "cancelled"));
        (await _kit.OrdersOfAsync(quote)).Should().HaveCount(2);
        await using var db = _kit.Db();
        (await db.AuditLogs.CountAsync(a => a.EntityId == quote.ToString() && a.Action == "quote.accepted")).Should().Be(2);
    }

    [PostgresFact]
    public async Task AQuoteWhoseOrderWasConfirmed_CannotBeAcceptedAgain_EvenAfterACancel()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true);
        placed.Status.Should().Be("confirmed");

        await using (var db = _kit.Db())
        {
            var staff = new Who(t.Staff);
            var cancelled = await new OrderCancellationService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System),
                new CreditLedgerService(db, staff, TimeProvider.System), staff, TimeProvider.System).CancelConfirmedAsync(placed.OrderId, new CancelConfirmedOrderRequest("customer called: wrong part"));
            cancelled.Status.Should().Be("cancelled");
        }

        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true))).Should().Be("QUOTE_ALREADY_ACCEPTED", "a confirmed order ends the quote");

        // The database refuses to move the accepted order too (§8.6 guard).
        await using (var db = _kit.Db())
        {
            var other = await CreditTestData.InsertOrderAsync(db, t.Buyer, t.Company, 1_000, onCredit: false, status: "PendingPayment");
            var act = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE quote_versions SET accepted_order_id = {other} WHERE quote_id = {quote}");
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("moves on only after its order lapsed");
        }
    }

    [PostgresFact]
    public async Task ASecondLiveOrderForOneVersion_FailsAtDbLevel()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var placed = await _kit.AcceptAsync(t, quote);
        var version = (await _kit.VersionAsync(quote)).Id;

        await using var db = _kit.Db();
        async Task<PostgresException> InsertAsync(Guid? quoteId, Guid? versionId, string status = "PendingPayment")
        {
            var act = () => db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO orders (id, order_number, user_id, company_id, status, tax_mode, currency, subtotal, discount_total, tax_total, shipping_total,
                    grand_total, ship_to, event_seq, placed_at, created_at, pays_on_credit, quote_id, quote_version_id)
                VALUES ({Guid.NewGuid()}, {OrderNumber.Generate(DateTime.UtcNow)}, {t.Buyer}, {t.Company}, {status}, 'Exclusive', 'BDT', 1, 0, 0, 0, 1,
                    '{{}}'::jsonb, 1, now(), now(), false, {quoteId}, {versionId})");
            return (await act.Should().ThrowAsync<PostgresException>()).Which;
        }

        (await InsertAsync(quote, version)).ConstraintName.Should().Be("ux_orders_live_quote_version");
        (await InsertAsync(quote, null)).ConstraintName.Should().Be("ck_orders_quote_link");

        // The backstop answers a 409, never a 500: a live order row the version does not know of (written past the domain) stops the acceptance.
        var other = await _kit.SentAsync(t, [new Line(item)]);
        var otherVersion = (await _kit.VersionAsync(other)).Id;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO orders (id, order_number, user_id, company_id, status, tax_mode, currency, subtotal, discount_total, tax_total, shipping_total,
                grand_total, ship_to, event_seq, placed_at, created_at, pays_on_credit, quote_id, quote_version_id)
            VALUES ({Guid.NewGuid()}, {OrderNumber.Generate(DateTime.UtcNow)}, {t.Buyer}, {t.Company}, 'Confirmed', 'Exclusive', 'BDT', 1, 0, 0, 0, 1,
                '{{}}'::jsonb, 1, now(), now(), true, {other}, {otherVersion})");
        (await CodeOf(() => _kit.AcceptAsync(t, other, onCredit: true))).Should().Be("QUOTE_ALREADY_ACCEPTED");
        (await _kit.StockAsync(item)).Should().Be((50, 2), "only the first acceptance holds stock; the refused one gave its hold back");
        var updateItem = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_items SET quote_line_id = NULL WHERE sub_order_id IN (SELECT id FROM sub_orders WHERE order_id = {placed.OrderId})");
        (await updateItem.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_order_items_price_snapshot");

        // A cancelled order frees the version for another order row (the domain decides whether that may happen).
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Cancelled' WHERE id = {placed.OrderId}");
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO orders (id, order_number, user_id, company_id, status, tax_mode, currency, subtotal, discount_total, tax_total, shipping_total,
                grand_total, ship_to, event_seq, placed_at, created_at, pays_on_credit, quote_id, quote_version_id)
            VALUES ({Guid.NewGuid()}, {OrderNumber.Generate(DateTime.UtcNow)}, {t.Buyer}, {t.Company}, 'PendingPayment', 'Exclusive', 'BDT', 1, 0, 0, 0, 1,
                '{{}}'::jsonb, 1, now(), now(), false, {quote}, {version})");
    }

    // ---- QT5: validity ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnExpiredQuoteCannotBeAccepted_AtTheStartOfTheDhakaDayAfterTheLastValidDay()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)], validityDays: 1);
        var v = await _kit.VersionAsync(quote);

        // Sent on Dhaka day D, valid for 1 day: the last valid day is D + 1, so it ends at the start of D + 2 in Dhaka = 18:00 UTC on D + 1.
        var dhakaDay = DateOnly.FromDateTime(v.SentAtUtc!.Value.AddHours(6));
        v.ValidUntilUtc.Should().Be(DhakaMidnightUtc(dhakaDay.AddDays(2)));

        var refused = await FluentActions.Awaiting(() => _kit.AcceptAsync(t, quote, at: v.ValidUntilUtc!.Value)).Should().ThrowAsync<ConflictException>();
        (refused.Which.Code, refused.Which.Message.Contains("expired")).Should().Be(("QUOTE_NOT_ACCEPTABLE", true), "valid_until is exclusive");
        (await _kit.StockAsync(item)).Should().Be((50, 0), "an expired quote holds no stock");

        var placed = await _kit.AcceptAsync(t, quote, at: v.ValidUntilUtc!.Value.AddTicks(-1));
        placed.Status.Should().Be("pending_payment", "the last tick of the last valid Dhaka day still accepts");
    }

    [PostgresFact]
    public async Task AnOrderWaitingForApproval_KeepsTheQuotePrice_AfterTheQuoteExpires()
    {
        var t = await _kit.TeamAsync(ruleFrom: 1);
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item, 2, item.Net - 500)], validityDays: 1);
        var v = await _kit.VersionAsync(quote);
        var until = v.ValidUntilUtc!.Value;

        var placed = await _kit.AcceptAsync(t, quote, at: until.AddMinutes(-1));
        placed.Status.Should().Be("pending_approval");
        // The catalogue price rises and the quote expires while the order waits; the approver says yes after valid_until.
        await _kit.SqlAsync($"UPDATE product_variants SET price_minor = price_minor * 3 WHERE id = {item.Variant}");
        await DecideAsync(t, placed.OrderId, approve: true, at: until.AddMinutes(30));

        var order = await _kit.OrderAsync(placed.OrderId);
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.GrandTotalMinor.Should().Be(v.GoodsTotalMinor);
        order.SubOrders.Single().Items.Single().UnitPriceMinor.Should().Be(item.Net - 500);
    }

    // ---- QT11: approvals, spend limit, budget --------------------------------------------------------------------------

    [PostgresFact]
    public async Task AcceptanceWaitsForApproval()
    {
        var t = await _kit.TeamAsync(ruleFrom: 100_000);
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item, 2)]);
        var v = await _kit.VersionAsync(quote);

        var placed = await _kit.AcceptAsync(t, quote, onCredit: true);
        placed.Status.Should().Be("pending_approval", "the rule covers the server total; credit is charged only at confirmation");
        await using (var db = _kit.Db())
        {
            var request = await db.ApprovalRequests.AsNoTracking().SingleAsync(r => r.SubjectType == "order" && r.SubjectId == placed.OrderId);
            (request.AmountMinor, request.Status).Should().Be((v.GoodsTotalMinor, ApprovalStatus.Pending));
        }

        (await LedgerAsync(t.Company)).Should().BeEmpty();
        (await _kit.StockAsync(item)).Should().Be((50, 2), "the stock is held while the approver decides");
        (await _kit.QuoteAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Accepted);

        await DecideAsync(t, placed.OrderId, approve: true);
        (await _kit.OrderAsync(placed.OrderId)).Status.Should().Be(OrderStatus.Confirmed);
        (await LedgerAsync(t.Company)).Should().ContainSingle(l => l.OrderId == placed.OrderId && l.Kind == CreditLedgerKind.Charge);
    }

    [PostgresFact]
    public async Task Accept_ObeysTheSpendLimitAndBudget_LikeAnOrder()
    {
        var item = await _kit.ItemAsync();
        var limited = await _kit.TeamAsync(spendLimit: 1_000);
        var quote = await _kit.SentAsync(limited, [new Line(item)]);
        (await CodeOf(() => _kit.AcceptAsync(limited, quote, onCredit: true))).Should().Be("SPEND_LIMIT_EXCEEDED");

        var budgeted = await _kit.TeamAsync();
        Guid centre;
        await using (var db = _kit.Db())
            centre = (await new CompanyService(db).AddCostCentreAsync(budgeted.Admin, budgeted.Company, new CostCentreRequest("PLANT-2", "Plant 2", 1_000))).Id;
        var budgetQuote = await _kit.SentAsync(budgeted, [new Line(item)]);
        (await CodeOf(() => _kit.AcceptAsync(budgeted, budgetQuote, onCredit: true, costCentre: centre))).Should().Be("BUDGET_EXCEEDED");

        // With a rule that covers it, the same acceptance waits for the approver instead.
        var ruled = await _kit.TeamAsync(spendLimit: 1_000, ruleFrom: 1);
        var ruledQuote = await _kit.SentAsync(ruled, [new Line(item)]);
        (await _kit.AcceptAsync(ruled, ruledQuote, onCredit: true)).Status.Should().Be("pending_approval");
        (await _kit.OrdersOfAsync(quote)).Should().BeEmpty();
        (await _kit.OrdersOfAsync(budgetQuote)).Should().BeEmpty();
    }

    // ---- QT12: credit ----------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AcceptingOnCredit_WhileOnHoldOrOverTheLimit_IsRefused()
    {
        var item = await _kit.ItemAsync();
        var small = await _kit.TeamAsync(creditLimit: 1_000);
        var quote = await _kit.SentAsync(small, [new Line(item)]);
        (await CodeOf(() => _kit.AcceptAsync(small, quote, onCredit: true))).Should().Be("CREDIT_LIMIT_EXCEEDED");

        var overdue = await _kit.TeamAsync();
        await OverdueChargeAsync(overdue, 10_000, daysAgo: 46);
        var held = await _kit.SentAsync(overdue, [new Line(item)]);
        (await CodeOf(() => _kit.AcceptAsync(overdue, held, onCredit: true))).Should().Be("CREDIT_HOLD_OVERDUE");
        (await LedgerAsync(overdue.Company)).Should().ContainSingle("a refused acceptance charges nothing");
        (await _kit.StockAsync(item)).Should().Be((50, 0));

        // The hold never blocks the quote itself: paid online, the same version is accepted.
        (await _kit.AcceptAsync(overdue, held, onCredit: false)).Status.Should().Be("pending_payment");
    }

    [PostgresFact]
    public async Task AnAdvanceOnlyQuote_CannotBeAcceptedOnCredit()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)], terms: "advance");

        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true))).Should().Be("QUOTE_REQUIRES_ADVANCE_PAYMENT");
        (await LedgerAsync(t.Company)).Should().BeEmpty();
        (await _kit.StockAsync(item)).Should().Be((50, 0), "refused before any stock is held");

        var placed = await _kit.AcceptAsync(t, quote, onCredit: false);
        placed.Status.Should().Be("pending_payment");
        (await _kit.OrderAsync(placed.OrderId)).PaysOnCredit.Should().BeFalse();
        (await LedgerAsync(t.Company)).Should().BeEmpty("an advance-only quote is never charged to credit");
    }

    [PostgresFact]
    public async Task AnAcceptedCreditQuote_PostsExactlyOneCharge_AtConfirmation()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item, 3)]);
        var key = Key();

        var placed = await _kit.AcceptAsync(t, quote, onCredit: true, key: key);
        await _kit.AcceptAsync(t, quote, onCredit: true, key: key);   // replay

        var charges = (await LedgerAsync(t.Company)).Where(l => l.OrderId == placed.OrderId).ToList();
        charges.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = CreditLedgerKind.Charge, ReceivableDeltaMinor = placed.GrandTotalMinor });
        await using var db = _kit.Db();
        (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == t.Company)).CreditUsedMinor.Should().Be(placed.GrandTotalMinor);
    }

    [PostgresFact]
    public async Task AnApprovalThatFindsTheHold_CancelsTheOrder_AndTheQuoteCanBeAcceptedAgain()
    {
        var t = await _kit.TeamAsync(ruleFrom: 1);
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true);
        placed.Status.Should().Be("pending_approval");

        await OverdueChargeAsync(t, 10_000, daysAgo: 60);   // the company falls on hold while the order waits
        await DecideAsync(t, placed.OrderId, approve: true);
        var cancelled = await _kit.OrderAsync(placed.OrderId);
        (cancelled.Status, cancelled.ConfirmedAtUtc).Should().Be((OrderStatus.Cancelled, (DateTime?)null));
        (await LedgerAsync(t.Company)).Should().NotContain(l => l.OrderId == placed.OrderId, "nothing is charged");

        var again = await _kit.AcceptAsync(t, quote, onCredit: false);
        again.Status.Should().Be("pending_approval", "the rule applies again, paid online this time");
        (await _kit.QuoteAsync(quote)).Version(1).AcceptedOrderId.Should().Be(again.OrderId);
    }

    // ---- QT14: stock -----------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ASentQuoteHoldsNoStock_AndAcceptingRechecksIt()
    {
        var t = await _kit.TeamAsync(buyers: 2);
        var item = await _kit.ItemAsync(onHand: 5);
        var quote = await _kit.SentAsync(t, [new Line(item, 4)]);
        (await _kit.StockAsync(item)).Should().Be((5, 0), "a sent quote holds no stock (§15 decision 4)");

        var other = await UnpaidOrderAsync(t, t.Buyers[1], item, 3);
        (await CodeOf(() => _kit.AcceptAsync(t, quote))).Should().Be("INSUFFICIENT_STOCK");
        (await _kit.StockAsync(item)).Should().Be((5, 3), "the refused acceptance released what it tried to hold");
        (await _kit.QuoteAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Sent, "still acceptable");

        await CancelAsync(t.Buyers[1], other);
        (await _kit.AcceptAsync(t, quote)).Status.Should().Be("pending_payment");
        (await _kit.StockAsync(item)).Should().Be((5, 4));
    }

    [PostgresFact]
    public async Task QuoteAcceptance_CountsTowardsTheOpenUnpaidOrderCap()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item)]);
        var withdrawn = await _kit.SentAsync(t, [new Line(item)]);
        await using (var db = _kit.Db())
            await _kit.Desk(db, t.Staff).WithdrawAsync(withdrawn, 1, new Application.Quotes.StaffReasonRequest("Replaced by another quote"));
        var open = new List<Guid>();
        for (var i = 0; i < PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer; i++) open.Add(await UnpaidOrderAsync(t, t.Buyer, item));

        (await CodeOf(() => _kit.AcceptAsync(t, quote))).Should().Be("TOO_MANY_UNPAID_ORDERS");
        (await CodeOf(() => _kit.AcceptAsync(t, withdrawn))).Should().Be("QUOTE_NOT_ACCEPTABLE", "the quote's own answer comes before stock and the cap");
        await CancelAsync(t.Buyer, open[0]);
        (await _kit.AcceptAsync(t, quote)).Status.Should().Be("pending_payment");
        (await CodeOf(() => UnpaidOrderAsync(t, t.Buyer, item))).Should().Be("TOO_MANY_UNPAID_ORDERS", "the accepted quote's order counts as an open unpaid order");
    }

    // ---- QT15: VAT -------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AVatRateChangeAfterSending_KeepsTheNetPrice_AndNeedsConfirmation()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var quote = await _kit.SentAsync(t, [new Line(item, 2)]);
        var v = await _kit.VersionAsync(quote);
        v.Lines.Single().VatRate.Should().Be(0.05m);

        await _kit.SqlAsync($"UPDATE categories SET vat_rate = 0.10 WHERE id = {item.Category}");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true))).Should().Be("PRICE_CHANGED", "the preview showed less than the server total now");
        (await _kit.OrdersOfAsync(quote)).Should().BeEmpty();
        (await _kit.StockAsync(item)).Should().Be((50, 0));

        var net = 2 * item.Net;
        var withNewVat = net + (long)decimal.Round(net * 0.10m, 0, MidpointRounding.ToEven);
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true, expected: withNewVat);
        placed.GrandTotalMinor.Should().Be(withNewVat);
        var line = (await _kit.OrderAsync(placed.OrderId)).SubOrders.Single().Items.Single();
        (line.UnitPriceMinor, line.VatRate, line.NetMinor).Should().Be((item.Net, 0.10m, net), "the net price is the quote's; only the VAT line changes");

        // A lower rate goes through at the lower server total without a new confirmation.
        var other = await _kit.ItemAsync();
        var cheaper = await _kit.SentAsync(t, [new Line(other, 2)]);
        await _kit.SqlAsync($"UPDATE categories SET vat_rate = 0 WHERE id = {other.Category}");
        (await _kit.AcceptAsync(t, cheaper, onCredit: true)).GrandTotalMinor.Should().Be(2 * other.Net);
    }

    // ---- QT16: delivery --------------------------------------------------------------------------------------------------

    private async Task<(Guid Quoted, Guid Other)> SitesAsync(Team t)
    {
        await using var db = _kit.Db();
        var quoted = new Domain.Companies.CompanySite(Guid.NewGuid(), t.Company, "Ashulia plant", "Karim Mia", "01711000000", "Plot 9, BSCIC", "Gate 2", "Savar", "Dhaka", "Dhaka", "1341", false);
        var other = new Domain.Companies.CompanySite(Guid.NewGuid(), t.Company, "Chattogram depot", "Rahim Uddin", "01811000000", "Port Road 4", null, "Pahartali", "Chattogram", "Chattogram", "4202", false);
        db.CompanySites.AddRange(quoted, other);
        await db.SaveChangesAsync();
        return (quoted.Id, other.Id);
    }

    [PostgresFact]
    public async Task AFixedShippingQuote_ShipsOnlyToItsQuotedSite()
    {
        var t = await _kit.TeamAsync();
        var (bearing, belt) = (await _kit.ItemAsync(), await _kit.ItemAsync());
        var (site, other) = await SitesAsync(t);
        var quote = await _kit.SentAsync(t, [new Line(bearing), new Line(belt)], delivery: new FixedDelivery(site, 7_500));
        var v = await _kit.VersionAsync(quote);
        Guid address;
        await using (var db = _kit.Db())
            address = await db.Addresses.Where(a => a.UserId == t.Buyer).Select(a => a.Id).FirstAsync();

        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, site: other))).Should().Be("QUOTE_DELIVERY_FIXED");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, address: address))).Should().Be("QUOTE_DELIVERY_FIXED");

        // The site's address changes after the send: the order goes to the address the quote was sent with.
        await _kit.SqlAsync($"UPDATE company_sites SET line1 = 'Somewhere else 1' WHERE id = {site}");
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true, site: site);
        var order = await _kit.OrderAsync(placed.OrderId);
        (order.ShippingMinor, order.GrandTotalMinor).Should().Be((15_000, v.GrandTotalMinor!.Value), "the quoted fee per seller, the quoted total");
        var shipTo = OrderService.ShipToForBuyer(order.ShipToJson)!;
        (shipTo.SiteName, shipTo.Line1, shipTo.RecipientName).Should().Be(("Ashulia plant", "Plot 9, BSCIC", "Karim Mia"));
    }

    [PostgresFact]
    public async Task AQuotedSiteThatWasRemoved_RefusesTheAcceptance()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var (site, _) = await SitesAsync(t);
        var quote = await _kit.SentAsync(t, [new Line(item)], delivery: new FixedDelivery(site, 5_000));
        await using (var db = _kit.Db())
            await new CompanyService(db).RemoveSiteAsync(t.Admin, t.Company, site);

        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true))).Should().Be("QUOTE_DELIVERY_SITE_GONE");
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, site: site))).Should().Be("QUOTE_DELIVERY_SITE_GONE");
        (await _kit.OrdersOfAsync(quote)).Should().BeEmpty();
        (await _kit.StockAsync(item)).Should().Be((50, 0));
    }

    // ---- the catalogue is checked again --------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnItemNoLongerSold_OrAQuantityTheOrderUnitsNowRefuse_StopsTheAcceptance()
    {
        var t = await _kit.TeamAsync();
        var (bearing, belt) = (await _kit.ItemAsync(), await _kit.ItemAsync());
        var gone = await _kit.SentAsync(t, [new Line(bearing), new Line(belt)]);
        var packed = await _kit.SentAsync(t, [new Line(belt, 2)]);
        var moved = await _kit.ItemAsync();
        var otherSeller = await _kit.SentAsync(t, [new Line(moved)]);

        await _kit.SqlAsync($"UPDATE product_variants SET status = 'inactive' WHERE id = {bearing.Variant}");
        (await CodeOf(() => _kit.AcceptAsync(t, gone, onCredit: true))).Should().Be("VARIANT_UNAVAILABLE");

        // The item is now sold by another seller than the one quoted (whose price and delivery fee the quote carries).
        await _kit.SqlAsync($"UPDATE products SET seller_id = {belt.Seller} WHERE id = (SELECT product_id FROM product_variants WHERE id = {moved.Variant})");
        (await CodeOf(() => _kit.AcceptAsync(t, otherSeller, onCredit: true))).Should().Be("VARIANT_UNAVAILABLE");

        await _kit.SqlAsync($"UPDATE product_variants SET pack_size = 3, min_order_qty = 3 WHERE id = {belt.Variant}");
        (await CodeOf(() => _kit.AcceptAsync(t, packed, onCredit: true))).Should().Be("VALIDATION_ERROR", "nothing is rounded: ask for a revision");
        (await _kit.StockAsync(belt)).Should().Be((50, 0));
    }

    [PostgresFact]
    public async Task AnAcceptRequest_WithoutItsTotalHashOrVersion_IsRefusedBeforeAnythingIsRead()
    {
        var t = await _kit.TeamAsync();
        var quote = await _kit.SentAsync(t, [new Line(await _kit.ItemAsync())]);
        var hash = (await _kit.VersionAsync(quote)).ContentHash;
        await using var db = _kit.Db();
        var orders = _kit.Orders(db, t.Buyer);
        async Task<string> FieldOf(AcceptQuoteRequest r) =>
            (await FluentActions.Awaiting(() => orders.PlaceFromQuoteAsync(r)).Should().ThrowAsync<ValidationException>()).Which.Errors.Single().Field;

        (await FieldOf(new AcceptQuoteRequest(t.Buyer, Key(), t.Company, quote, 1, hash, null))).Should().Be("expectedGrandTotalMinor");
        (await FieldOf(new AcceptQuoteRequest(t.Buyer, Key(), t.Company, quote, 1, " ", 1))).Should().Be("contentHash");
        (await FieldOf(new AcceptQuoteRequest(t.Buyer, Key(), t.Company, quote, null, hash, 1))).Should().Be("versionNo");
        (await FieldOf(new AcceptQuoteRequest(t.Buyer, "short", t.Company, quote, 1, hash, 1))).Should().Be("idempotencyKey");
    }
}
