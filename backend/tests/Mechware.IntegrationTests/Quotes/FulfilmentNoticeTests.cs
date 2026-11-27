using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Common;
using Mechware.Application.Notifications;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Notifications;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteAcceptKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// F3b against a real database, with a capturing sender: shipped, delivered, invoice issued and the return mails reach the order's placer once,
/// in their language, with links to the order page and no attachment; a member who left, or a deactivated user, gets nothing; free text is
/// never markup. One English notice goes to the sales inbox for a return request.
/// </summary>
public sealed class FulfilmentNoticeTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Inbox = "sales-desk@mechware.test";
    private readonly QuoteAcceptKit _kit = new(fixture);

    private sealed class CaptureSender : INotificationSender
    {
        public ConcurrentBag<EmailMessage> Sent { get; } = [];
        public Task SendEmailAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private async Task PublishAsync(CaptureSender sender, OutboxEnvelope e)
    {
        await using var db = _kit.Db();
        await new FulfilmentNotificationPublisher(db,
            new NotificationDispatcher(db, sender, new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance), NullLogger<NotificationDispatcher>.Instance),
            new StorefrontLinks("http://localhost:3000"), new QuoteNoticeOptions(Inbox), new InAppNotifier(db, new StorefrontLinks("http://localhost:3000"))).PublishAsync(e);
    }

    private static OutboxEnvelope Env(string type, Guid aggregate, object payload) =>
        new(Guid.NewGuid(), type, "Test", aggregate, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTime.UtcNow);

    private async Task<(Team Team, Guid OrderId, string Number, Guid ItemId)> PlacedAsync()
    {
        var t = await _kit.TeamAsync();
        var quote = await _kit.SentAsync(t, [new Line(await _kit.ItemAsync())]);
        var placed = await _kit.AcceptAsync(t, quote);
        var order = await _kit.OrderAsync(placed.OrderId);
        return (t, placed.OrderId, placed.OrderNumber, order.SubOrders.First().Items.First().Id);
    }

    private async Task<string> EmailOf(Guid user)
    {
        await using var db = _kit.Db();
        return await db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => u.Email).SingleAsync();
    }

    private async Task<Guid> ReturnAsync(Guid company, Guid buyer, Guid orderId, string number, Guid itemId, bool reject, Guid? staff = null)
    {
        await using var db = _kit.Db();
        var r = ReturnRequest.Open(orderId, number, OrderStatus.Delivered, DateTime.UtcNow, company, buyer, ReturnReason.Damaged, null,
            [new ReturnLineRequest(itemId, 1)], [new ReturnableLine(itemId, 2, 0, true)], 30, DateTime.UtcNow, "ret-" + Guid.NewGuid().ToString("N"), new string('a', 64));
        db.ReturnRequests.Add(r);
        await db.SaveChangesAsync();
        if (reject)
        {
            await using var db2 = _kit.Db();
            (await db2.ReturnRequests.SingleAsync(x => x.Id == r.Id)).Reject(staff ?? buyer, "<b>Used</b> & worn", number, DateTime.UtcNow);
            await db2.SaveChangesAsync();
        }
        return r.Id;
    }

    [PostgresFact]
    public async Task EachMail_ReachesThePlacerOnce_InTheirLanguage_WithAnOrderLink_AndNoMarkup()
    {
        var (t, orderId, number, itemId) = await PlacedAsync();
        await using (var db = _kit.Db()) await db.Users.Where(u => u.Id == t.Buyer).ExecuteUpdateAsync(s => s.SetProperty(u => u.Locale, "bn"));
        var returnId = await ReturnAsync(t.Company, t.Buyer, orderId, number, itemId, reject: true, staff: t.Staff);
        var sender = new CaptureSender();
        var events = new[]
        {
            Env(nameof(OrderShippedEvent), orderId, new { orderId, courierName = "Pathao", trackingNumber = "T-1", trackingUrl = "https://pathao.com/t/1" }),
            Env(nameof(OrderDeliveredEvent), orderId, new { orderId }),
            Env("InvoiceIssuedEvent", orderId, new { orderId, number = "INV-7", mode = "Live" }),
            Env("InvoiceIssuedEvent", orderId, new { orderId, number = "SPEC-1", mode = "Specimen" }),
            Env(nameof(ReturnApprovedEvent), returnId, new { returnId }),
            Env(nameof(ReturnRejectedEvent), returnId, new { returnId }),
            Env(nameof(ReturnReceivedEvent), returnId, new { returnId }),
            Env(nameof(ReturnSettledEvent), returnId, new { returnId }),
            Env(nameof(ReturnRequestedEvent), returnId, new { returnId, orderNumber = number, quantity = 1 }),
            Env(nameof(OrderShippedEvent), orderId, new { orderId, courierName = "X", trackingNumber = "T-2", trackingUrl = "javascript:alert(1)" }),
        };
        foreach (var e in events) { await PublishAsync(sender, e); await PublishAsync(sender, e); }   // a retry sends nothing twice

        var buyer = await EmailOf(t.Buyer);
        var mine = sender.Sent.Where(m => m.To == buyer).ToList();
        mine.Should().HaveCount(8, "shipped x2, delivered, one live invoice, four return mails; the specimen invoice is not mailed");
        mine.Should().OnlyContain(m => m.HtmlBody.Contains($"/bn/account/orders/{number}") && !m.Subject.Contains('<'));
        mine.Should().ContainSingle(m => m.Subject.Contains("INV-7"));
        mine.Should().ContainSingle(m => m.TextBody.Contains("https://pathao.com/t/1") && m.TextBody.Contains("Pathao"));
        mine.Should().NotContain(m => m.TextBody.Contains("javascript:"));
        var rejected = mine.Single(m => m.TextBody.Contains("<b>Used</b> & worn"));
        rejected.HtmlBody.Should().Contain("&lt;b&gt;Used&lt;/b&gt; &amp; worn").And.NotContain("<b>Used</b>");
        rejected.Subject.Should().NotContain("Used");
        sender.Sent.Where(m => m.To == Inbox).Should().ContainSingle(m => m.TextBody.Contains(number));
        sender.Sent.Should().HaveCount(9).And.OnlyContain(m => m.To == buyer || m.To == Inbox);
    }

    [PostgresFact]
    public async Task AMemberWhoLeft_OrADeactivatedUser_GetsNothing()
    {
        var (t, orderId, number, itemId) = await PlacedAsync();
        var returnId = await ReturnAsync(t.Company, t.Buyer, orderId, number, itemId, reject: false);
        await using (var db = _kit.Db()) await db.CompanyMembers.Where(m => m.CompanyId == t.Company && m.UserId == t.Buyer).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        var sender = new CaptureSender();
        await PublishAsync(sender, Env(nameof(OrderDeliveredEvent), orderId, new { orderId }));
        await PublishAsync(sender, Env(nameof(ReturnApprovedEvent), returnId, new { returnId }));
        sender.Sent.Should().BeEmpty("the placer left the company");

        var (t2, order2, _, _) = await PlacedAsync();
        await using (var db = _kit.Db()) await db.Users.Where(u => u.Id == t2.Buyer).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Suspended));
        await PublishAsync(sender, Env(nameof(OrderShippedEvent), order2, new { orderId = order2, courierName = "Pathao", trackingNumber = "T-1" }));
        sender.Sent.Should().BeEmpty("a deactivated user is not mailed");
    }
}
