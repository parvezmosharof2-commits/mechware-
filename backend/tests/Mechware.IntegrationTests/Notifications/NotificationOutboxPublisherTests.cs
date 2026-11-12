using System.Text.Json;
using FluentAssertions;
using Mechware.Application.Approvals;
using Mechware.Application.Common;
using Mechware.Application.Companies;
using Mechware.Application.Notifications;
using Mechware.Application.Orders;
using Mechware.Domain.Approvals;
using Mechware.Domain.Companies;
using Mechware.Infrastructure.Approvals;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Notifications;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mechware.IntegrationTests.Notifications;

/// <summary>Needs both PostgreSQL and Mailpit; skips (visibly) without either.</summary>
public sealed class PostgresMailpitFactAttribute : FactAttribute
{
    public PostgresMailpitFactAttribute()
    {
        if (!TestInfra.PostgresAvailable) Skip = "PostgreSQL not reachable (set MECHWARE_TEST_PG).";
        else if (!MailpitInfra.Available) Skip = "Mailpit not reachable (docker compose up -d mailpit).";
    }
}

/// <summary>F3a.3: outbox events become emails, once per recipient, with failures kept apart.</summary>
public class NotificationOutboxPublisherTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string BaseUrl = "http://localhost:3000";
    private static long _binSeq = 7_500_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");

    private sealed record Team(Guid Company, Guid Admin, Guid Buyer, Guid Approver);

    private static SmtpEmailSender MailpitSender() => new(
        new EmailOptions { Host = "localhost", Port = MailpitInfra.SmtpPort, UseTls = false, FromAddress = "no-reply@mechware.test", FromName = "Mechware" },
        NullLogger<SmtpEmailSender>.Instance);

    private static NotificationOutboxPublisher Publisher(MechwareDbContext db, INotificationSender? sender = null, INotificationTemplates? templates = null) =>
        new(db, new NotificationDispatcher(db, sender ?? MailpitSender(), templates ?? new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance),
            NullLogger<NotificationDispatcher>.Instance), new StorefrontLinks(BaseUrl));

    private async Task<Team> TeamAsync()
    {
        var (admin, buyer, approver) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Notified Buyers Ltd", "TL-NO-1", NextBin(), null, null))).Id;
        await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null));
        await companies.AdminActivateAsync(company);
        await new ApprovalService(db, TimeProvider.System).AddRuleAsync(admin, company, new ApprovalRuleRequest("Above 2,000", 200_000, null, null, [approver]));
        return new Team(company, admin, buyer, approver);
    }

    private async Task<PlacedOrder> PlaceAsync(Team t, int qty)
    {
        await using var db = fixture.CreateContext();
        var variant = (await CatalogTestData.SeedVariantAsync(db, 20, priceMinor: 100_000)).VariantId;
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System);
        return await orders.PlaceAsync(new PlaceOrderRequest(t.Buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variant, qty)], null, t.Company, false, null, null, null));
    }

    private async Task<OutboxEnvelope> EnvelopeAsync(string eventType, Guid aggregateId)
    {
        await using var db = fixture.CreateContext();
        var m = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.EventType == eventType && o.AggregateId == aggregateId);
        return new OutboxEnvelope(m.Id, m.EventType, m.AggregateType, m.AggregateId, m.Payload, m.OccurredAtUtc);
    }

    private async Task<string> EmailOf(Guid userId)
    {
        await using var db = fixture.CreateContext();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleAsync();
    }

    private async Task PublishAsync(OutboxEnvelope envelope)
    {
        await using var db = fixture.CreateContext();
        await Publisher(db).PublishAsync(envelope);
    }

    private async Task<int> LogRowsAsync(Guid eventId)
    {
        await using var db = fixture.CreateContext();
        return await db.NotificationLog.CountAsync(n => n.EventId == eventId);
    }

    /// <summary>Messages Mailpit holds for the address; waits until at least <paramref name="atLeast"/> arrive, then a moment longer so a duplicate would show.</summary>
    internal static async Task<List<JsonElement>> InboxAsync(string address, int atLeast)
    {
        using var http = new HttpClient();
        List<JsonElement> found = [];
        for (int i = 0; i < 20; i++)
        {
            using var list = JsonDocument.Parse(await http.GetStringAsync($"{MailpitInfra.Api}/api/v1/search?query={Uri.EscapeDataString("to:" + address)}"));
            found = list.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();
            if (found.Count >= atLeast) break;
            await Task.Delay(250);
        }

        if (atLeast > 0) await Task.Delay(600);
        using var again = JsonDocument.Parse(await http.GetStringAsync($"{MailpitInfra.Api}/api/v1/search?query={Uri.EscapeDataString("to:" + address)}"));
        return again.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();
    }

    private static async Task<JsonElement> BodyAsync(JsonElement summary)
    {
        using var http = new HttpClient();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{MailpitInfra.Api}/api/v1/message/{summary.GetProperty("ID").GetString()}"));
        return doc.RootElement.Clone();
    }

    [PostgresMailpitFact]
    public async Task PlacingAnOrder_SendsOneOrderConfirmedEmail_WithTheOrderNumberAndLink()
    {
        var t = await TeamAsync();
        var placed = await PlaceAsync(t, qty: 1);
        placed.Status.Should().Be("pending_payment");

        await PublishAsync(await EnvelopeAsync("OrderPlacedEvent", placed.OrderId));

        var inbox = await InboxAsync(await EmailOf(t.Buyer), 1);
        inbox.Should().HaveCount(1);
        var mail = await BodyAsync(inbox[0]);
        mail.GetProperty("Subject").GetString().Should().Contain(placed.OrderNumber);
        mail.GetProperty("Text").GetString().Should()
            .Contain($"{BaseUrl}/en/account/orders/{placed.OrderNumber}")
            .And.Contain("BDT 1,050.00");
    }

    [PostgresMailpitFact]
    public async Task AnApprovalRequest_EmailsTheApprover_AndTheBuyersOrderIsNotCalledConfirmedYet()
    {
        var t = await TeamAsync();
        var placed = await PlaceAsync(t, qty: 3);
        placed.Status.Should().Be("pending_approval");
        await using var db = fixture.CreateContext();
        var request = await db.ApprovalRequests.AsNoTracking().SingleAsync(r => r.SubjectId == placed.OrderId);

        await PublishAsync(await EnvelopeAsync(nameof(ApprovalRequestedEvent), request.Id));
        await PublishAsync(await EnvelopeAsync("OrderPlacedEvent", placed.OrderId));

        var approverMail = await InboxAsync(await EmailOf(t.Approver), 1);
        approverMail.Should().HaveCount(1);
        var mail = await BodyAsync(approverMail[0]);
        mail.GetProperty("Subject").GetString().Should().Contain(placed.OrderNumber);
        mail.GetProperty("Text").GetString().Should().Contain($"{BaseUrl}/en/account/company/{request.CompanyId}?tab=approvals");
        (await InboxAsync(await EmailOf(t.Buyer), 0)).Should().BeEmpty("an order waiting for approval is not confirmed");
    }

    [PostgresMailpitFact]
    public async Task RelayingTheSameMessageTwice_StillSendsOneEmailEach()
    {
        var t = await TeamAsync();
        var small = await PlaceAsync(t, qty: 1);
        var big = await PlaceAsync(t, qty: 3);
        await using var db = fixture.CreateContext();
        var request = await db.ApprovalRequests.AsNoTracking().SingleAsync(r => r.SubjectId == big.OrderId);
        var confirmed = await EnvelopeAsync("OrderPlacedEvent", small.OrderId);
        var approval = await EnvelopeAsync(nameof(ApprovalRequestedEvent), request.Id);

        foreach (var envelope in new[] { confirmed, approval, confirmed, approval })
            await PublishAsync(envelope);

        (await InboxAsync(await EmailOf(t.Buyer), 1)).Should().HaveCount(1);
        (await InboxAsync(await EmailOf(t.Approver), 1)).Should().HaveCount(1);
        (await LogRowsAsync(confirmed.Id)).Should().Be(1);
        (await LogRowsAsync(approval.Id)).Should().Be(1);
    }

    private sealed class ThrowingTemplates(string badName) : INotificationTemplates
    {
        private readonly EmbeddedTemplates _inner = new(NullLogger<EmbeddedTemplates>.Instance);
        public RenderedEmail Render(string templateKey, string locale, IReadOnlyDictionary<string, string> model) =>
            model["customerName"] == badName ? throw new NotificationTemplateException("boom") : _inner.Render(templateKey, locale, model);
    }

    private sealed class FlakySender(string failFor, INotificationSender inner) : INotificationSender
    {
        public bool Fail = true;
        public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Fail && message.To == failFor ? throw new IOException("connection reset") : inner.SendEmailAsync(message, cancellationToken);
    }

    private static NotificationTarget Target(Guid id, string email, string name) => new(id, email, "en", new Dictionary<string, string>
    {
        ["customerName"] = name,
        ["orderNumber"] = "MW-1",
        ["total"] = "BDT 1.00",
        ["orderUrl"] = BaseUrl + "/en/account/orders/MW-1"
    });

    [PostgresMailpitFact]
    public async Task ATemplateFailureForOneRecipient_DoesNotBlockTheOthers_AndIsNotRetried()
    {
        var (a, b) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        var (ea, eb) = (await EmailOf(a), await EmailOf(b));
        var eventId = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        var dispatcher = new NotificationDispatcher(db, MailpitSender(), new ThrowingTemplates("Bad Name"), NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(eventId, "order-confirmed", [Target(a, ea, "Bad Name"), Target(b, eb, "Good Name")]);
        await dispatcher.DispatchAsync(eventId, "order-confirmed", [Target(a, ea, "Bad Name"), Target(b, eb, "Good Name")]);

        (await InboxAsync(eb, 1)).Should().HaveCount(1);
        (await InboxAsync(ea, 0)).Should().BeEmpty();
        var rows = await fixture.CreateContext().NotificationLog.AsNoTracking().Where(n => n.EventId == eventId).ToListAsync();
        rows.Should().ContainSingle(r => r.RecipientUserId == a && r.Status == "failed");
        rows.Should().ContainSingle(r => r.RecipientUserId == b && r.Status == "sent");
    }

    [PostgresMailpitFact]
    public async Task ATransientSendFailure_IsRethrownForTheOutboxRetry_AfterTheOthersWereServed_AndThenSendsOnce()
    {
        var (a, b) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        var (ea, eb) = (await EmailOf(a), await EmailOf(b));
        var eventId = Guid.NewGuid();
        var sender = new FlakySender(ea, MailpitSender());
        await using var db = fixture.CreateContext();
        var dispatcher = new NotificationDispatcher(db, sender, new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance), NullLogger<NotificationDispatcher>.Instance);
        var targets = new[] { Target(a, ea, "A"), Target(b, eb, "B") };

        await FluentActions.Awaiting(() => dispatcher.DispatchAsync(eventId, "order-confirmed", targets)).Should().ThrowAsync<IOException>();
        (await InboxAsync(eb, 1)).Should().HaveCount(1);
        (await LogRowsAsync(eventId)).Should().Be(1, "the failed recipient's claim was released");

        sender.Fail = false;
        await dispatcher.DispatchAsync(eventId, "order-confirmed", targets); // the outbox retry
        (await InboxAsync(ea, 1)).Should().HaveCount(1);
        (await InboxAsync(eb, 1)).Should().HaveCount(1, "the recipient already served is not emailed again");
    }

    [PostgresMailpitFact]
    public async Task AMalformedAddress_IsMarkedFailedOnce_NotRetriedForever()
    {
        var a = await TestUsers.CreateAsync(fixture);
        var eventId = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        var dispatcher = new NotificationDispatcher(db, MailpitSender(), new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance), NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(eventId, "order-confirmed", [Target(a, "not an address", "A")]);

        (await fixture.CreateContext().NotificationLog.AsNoTracking().SingleAsync(n => n.EventId == eventId)).Status.Should().Be("failed");
    }
}
