using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Common;
using Mechware.Application.Notifications;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Notifications;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteAcceptKit;

namespace Mechware.IntegrationTests.Notifications;

/// <summary>
/// F3c against a real database: one in-app row per event per recipient (a retry writes nothing more; quote withdrawn is in-app only), the
/// buyer reads own rows only (another user's id is the same not-found as an unknown one), unread count, read-all, the relative-link CHECK
/// and the 180-day retention of read rows.
/// </summary>
public sealed class InAppNotificationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private readonly QuoteAcceptKit _kit = new(fixture);
    private static readonly StorefrontLinks Links = new("http://localhost:3000");
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private sealed class CaptureSender : INotificationSender
    {
        public ConcurrentBag<EmailMessage> Sent { get; } = [];
        public Task SendEmailAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private static OutboxEnvelope Env(string type, object payload) =>
        new(Guid.NewGuid(), type, "Test", Guid.NewGuid(), JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTime.UtcNow);

    private async Task PublishAsync(CaptureSender sender, OutboxEnvelope e)
    {
        await using var db = _kit.Db();
        await new QuoteNotificationPublisher(db,
            new NotificationDispatcher(db, sender, new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance), NullLogger<NotificationDispatcher>.Instance),
            Links, new QuoteNoticeOptions(null), new InAppNotifier(db, Links)).PublishAsync(e);
    }

    private async Task<Guid> AddAsync(Guid user, string kind = "order-shipped", string link = "/en/account/orders/MW-1")
    {
        var id = Guid.NewGuid();
        await using var db = _kit.Db();
        await new InAppNotifier(db, Links).AddAsync(id, user, kind, link, NoValues);
        return id;
    }

    // Each row is keyed by its event id, so the row id is looked up by user and event.
    private async Task<Guid> RowOfAsync(Guid eventId)
    {
        await using var db = _kit.Db();
        return await db.InAppNotifications.AsNoTracking().Where(n => n.EventId == eventId).Select(n => n.Id).SingleAsync();
    }

    private async Task<List<(Guid Id, DateTime? ReadAt)>> RowsAsync(Guid user)
    {
        await using var db = _kit.Db();
        return (await db.InAppNotifications.AsNoTracking().Where(n => n.UserId == user).ToListAsync()).Select(n => (n.Id, n.ReadAtUtc)).ToList();
    }

    [PostgresFact]
    public async Task OneRowPerEventPerRecipient_EvenOnRetry_AndWithdrawnIsInAppOnly()
    {
        var t = await _kit.TeamAsync();
        var quote = await _kit.SentAsync(t, [new Line(await _kit.ItemAsync())]);
        var version = await _kit.VersionAsync(quote);
        var sender = new CaptureSender();
        var sent = Env(nameof(QuoteSentEvent), new { quoteId = quote, versionId = version.Id });
        var withdrawn = Env(nameof(QuoteWithdrawnEvent), new { quoteId = quote, versionId = version.Id, versionNo = 1 });
        foreach (var e in new[] { sent, sent, withdrawn, withdrawn }) await PublishAsync(sender, e);

        (await RowsAsync(t.Buyer)).Should().HaveCount(2, "one row for the sent mail and one for the withdrawal, however often the events are retried");
        await using var db = _kit.Db();
        var rows = await db.InAppNotifications.AsNoTracking().Where(n => n.UserId == t.Buyer).Select(n => new { n.Kind, n.TitleKey, n.LinkPath, n.ParamsJson }).ToListAsync();
        rows.Select(k => k.Kind).Should().BeEquivalentTo("quote-sent", "quote-withdrawn");
        rows.Should().OnlyContain(k => k.LinkPath == $"/en/account/company/{t.Company}/quotes/{quote}" && k.TitleKey.StartsWith("notifications.quote_") && !k.ParamsJson.Contains('<'));
        sender.Sent.Should().ContainSingle("only the sent notice is mailed; the withdrawal is in-app only");
    }

    [PostgresFact]
    public async Task OwnRowsOnly_AnotherUsersIdIsTheSameNotFoundAsAnUnknownOne_AndUnreadCountAndReadAllTouchOnlyYou()
    {
        var (me, other) = (await _kit.UserAsync(), await _kit.UserAsync());
        var mine = new List<Guid>();
        for (var i = 0; i < 3; i++) mine.Add(await AddAsync(me));
        await AddAsync(other);
        var theirs = (await RowsAsync(other)).Single().Id;
        mine.Clear();
        mine.AddRange((await RowsAsync(me)).Select(r => r.Id));

        await using var db = _kit.Db();
        var svc = new InAppNotificationService(db, TimeProvider.System);
        (await svc.UnreadCountAsync(me)).Should().Be(3);
        (await svc.ListAsync(me, unreadOnly: true, 1)).Items.Select(i => i.Id).Should().BeEquivalentTo(mine);

        var foreign = await FluentActions.Awaiting(() => svc.MarkReadAsync(me, theirs)).Should().ThrowAsync<KeyNotFoundException>();
        var unknown = await FluentActions.Awaiting(() => svc.MarkReadAsync(me, Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
        foreign.Which.Message.Should().Be(unknown.Which.Message);
        (await RowsAsync(other)).Single().ReadAt.Should().BeNull("a foreign id changes nothing");

        await svc.MarkReadAsync(me, mine[0]);
        await svc.MarkReadAsync(me, mine[0]);   // already read is fine
        (await svc.UnreadCountAsync(me)).Should().Be(2);

        (await svc.MarkAllReadAsync(me)).Should().Be(2);
        (await svc.UnreadCountAsync(me)).Should().Be(0);
        (await RowsAsync(other)).Single().ReadAt.Should().BeNull("read-all touches only the caller's rows");
        (await svc.UnreadCountAsync(other)).Should().Be(1);
    }

    [PostgresTheory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("account/orders/1")]
    [InlineData("/en\\evil")]
    [InlineData("")]
    public async Task TheLinkMustBeARelativePathStartingWithASlash(string link)
    {
        var user = await _kit.UserAsync();
        await using var db = _kit.Db();
        var act = () => new InAppNotifier(db, Links).AddAsync(Guid.NewGuid(), user, "order-shipped", link, NoValues);
        (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("ck_in_app_notifications_link_relative");
        (await RowsAsync(user)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Retention_DeletesOnlyReadRowsOlderThan180Days()
    {
        var user = await _kit.UserAsync();
        var (e1, e2, e3, e4) = (await AddAsync(user), await AddAsync(user), await AddAsync(user), await AddAsync(user));
        var (oldRead, newRead, oldUnread, justRead) = (await RowOfAsync(e1), await RowOfAsync(e2), await RowOfAsync(e3), await RowOfAsync(e4));
        var now = DateTime.UtcNow;
        await _kit.SqlAsync($"UPDATE in_app_notifications SET read_at = {now.AddDays(-181)}, created_at = {now.AddDays(-200)} WHERE id = {oldRead}");
        await _kit.SqlAsync($"UPDATE in_app_notifications SET read_at = {now.AddDays(-179)}, created_at = {now.AddDays(-200)} WHERE id = {newRead}");
        await _kit.SqlAsync($"UPDATE in_app_notifications SET created_at = {now.AddDays(-400)} WHERE id = {oldUnread}");
        await _kit.SqlAsync($"UPDATE in_app_notifications SET read_at = {now} WHERE id = {justRead}");

        await using var db = _kit.Db();
        (await new InAppNotificationService(db, TimeProvider.System).PurgeAsync(now)).Should().Be(1);
        (await RowsAsync(user)).Select(r => r.Id).Should().BeEquivalentTo([newRead, oldUnread, justRead]);
    }
}
