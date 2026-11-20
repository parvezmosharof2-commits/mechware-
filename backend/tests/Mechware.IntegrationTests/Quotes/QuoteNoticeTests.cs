using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Common;
using Mechware.Application.Notifications;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Notifications;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteAcceptKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.12 against a real database, with a capturing sender instead of a mail server (ADR-0011 §7, §14; threat model QT6, QT17 e-mail half):
/// the quote-sent mail, notices only to effective members of the quote's company, approval-needed never to the author, quote text never
/// markup, and the expiry worker's notices once per version however many passes and workers run, without ever changing a status.
/// </summary>
public sealed class QuoteNoticeTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Inbox = "sales-desk@mechware.test";
    private readonly QuoteAcceptKit _kit = new(fixture);

    private sealed class CaptureSender : INotificationSender
    {
        public ConcurrentBag<EmailMessage> Sent { get; } = [];
        public Task SendEmailAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private static QuoteNotificationPublisher Publisher(MechwareDbContext db, CaptureSender sender, string? inbox = Inbox) =>
        new(db, new NotificationDispatcher(db, sender, new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance), NullLogger<NotificationDispatcher>.Instance),
            new StorefrontLinks("http://localhost:3000"), new QuoteNoticeOptions(inbox));

    private async Task<List<OutboxEnvelope>> EnvelopesAsync(Guid quote, string eventType)
    {
        await using var db = _kit.Db();
        var rows = await db.OutboxMessages.AsNoTracking().Where(o => o.AggregateId == quote && o.EventType == eventType).OrderBy(o => o.OccurredAtUtc).ToListAsync();
        return [.. rows.Select(m => new OutboxEnvelope(m.Id, m.EventType, m.AggregateType, m.AggregateId, m.Payload, m.OccurredAtUtc))];
    }

    private async Task PublishAllAsync(CaptureSender sender, Guid quote, string eventType)
    {
        foreach (var e in await EnvelopesAsync(quote, eventType))
        {
            await using var db = _kit.Db();
            await Publisher(db, sender).PublishAsync(e);
        }
    }

    private async Task<string> EmailOf(Guid user)
    {
        await using var db = _kit.Db();
        return await db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => u.Email).SingleAsync();
    }

    private async Task GrantAsync(Guid user, string permission)
    {
        await using var db = _kit.Db();
        var perm = await db.Permissions.FirstOrDefaultAsync(p => p.Code == permission);
        if (perm is null) db.Permissions.Add(perm = new Permission(Guid.NewGuid(), permission, "quotes"));
        var role = new Role(Guid.NewGuid(), "tmp-" + Guid.NewGuid().ToString("N"), null, isSystem: false);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission(role.Id, perm.Id));
        db.UserRoles.Add(new UserRole(user, role.Id, role.Name));
        await db.SaveChangesAsync();
    }

    private async Task<(Team Team, Guid Quote)> SentQuoteAsync(DateTime? at = null, int validityDays = 15)
    {
        var t = await _kit.TeamAsync();
        var quote = await _kit.SentAsync(t, [new Line(await _kit.ItemAsync())], validityDays: validityDays, at: at);
        return (t, quote);
    }

    [PostgresFact]
    public async Task QuoteSent_IsMailedToTheContact_WithALinkToTheQuotePage_AndNoAttachment()
    {
        var (t, quote) = await SentQuoteAsync();
        var sender = new CaptureSender();
        await PublishAllAsync(sender, quote, nameof(QuoteSentEvent));
        var buyer = await EmailOf(t.Buyer);
        var mail = sender.Sent.Should().ContainSingle(m => m.To == buyer).Subject;
        mail.Subject.Should().Contain((await _kit.QuoteAsync(quote)).Number);
        mail.HtmlBody.Should().Contain($"/en/account/company/{t.Company}/quotes/{quote}");
        mail.TextBody.Should().Contain($"/en/account/company/{t.Company}/quotes/{quote}");
        sender.Sent.Should().OnlyContain(m => m.To != Inbox, "the buyer notice does not go to the sales inbox");
    }

    [PostgresFact]
    public async Task QuoteNotices_GoOnlyToEffectiveMembersOfTheQuotesCompany()
    {
        var (t, quote) = await SentQuoteAsync();
        var buyer = await EmailOf(t.Buyer);
        // The contact leaves the company: no mail to them, and the sales inbox is told instead.
        await _kit.SqlAsync($"UPDATE company_members SET is_active = false WHERE company_id = {t.Company} AND user_id = {t.Buyer}");
        var left = new CaptureSender();
        await PublishAllAsync(left, quote, nameof(QuoteSentEvent));
        left.Sent.Should().NotContain(m => m.To == buyer);
        left.Sent.Should().ContainSingle(m => m.To == Inbox).Which.TextBody.Should().Contain("no longer an active member");

        // A membership that was never accepted grants nothing either.
        await _kit.SqlAsync($"UPDATE company_members SET is_active = true, accepted_at = NULL WHERE company_id = {t.Company} AND user_id = {t.Buyer}");
        var invited = new CaptureSender();
        var e = (await EnvelopesAsync(quote, nameof(QuoteSentEvent))).Single();
        await using (var db = _kit.Db()) await Publisher(db, invited).PublishAsync(e with { Id = Guid.NewGuid() });
        invited.Sent.Should().NotContain(m => m.To == buyer);

        // An effective member gets the mail.
        await _kit.SqlAsync($"UPDATE company_members SET accepted_at = now() WHERE company_id = {t.Company} AND user_id = {t.Buyer}");
        var back = new CaptureSender();
        await using (var db = _kit.Db()) await Publisher(db, back).PublishAsync(e with { Id = Guid.NewGuid() });
        back.Sent.Should().ContainSingle(m => m.To == buyer);
    }

    [PostgresFact]
    public async Task ApprovalNeededMail_NeverGoesToTheAuthor()
    {
        var t = await _kit.TeamAsync();
        var otherApprover = await _kit.UserAsync();
        await GrantAsync(t.Staff, Permissions.QuotesApprove);   // the author holds the permission too
        await GrantAsync(otherApprover, Permissions.QuotesApprove);
        Guid quote;
        await using (var db = _kit.Db())
        {
            var desk = _kit.Desk(db, t.Staff);
            quote = (await desk.CreateQuoteAsync(new Mechware.Application.Quotes.CreateStaffQuoteRequest(null, t.Company, t.Buyer))).Quote.Id;
            await EditAsync(desk, quote, 1, [new Line(await _kit.ItemAsync())], validityDays: 31);   // over 30 days: waits for approval
            (await desk.SendAsync(quote, 1)).Status.Should().Be("pending_approval");
        }
        (await _kit.QuoteAsync(quote)).Versions.Single().Status.Should().Be(QuoteVersionStatus.PendingApproval);

        var sender = new CaptureSender();
        await PublishAllAsync(sender, quote, nameof(QuoteApprovalRequestedEvent));
        var to = sender.Sent.Select(m => m.To).ToList();
        to.Should().Contain(await EmailOf(otherApprover));
        to.Should().NotContain(await EmailOf(t.Staff), "four-eyes: the author does not approve their own quote");
    }

    [PostgresFact]
    public async Task QuoteTextInEmails_IsNeverInterpretedAsMarkup_EmailHalf()
    {
        var (t, quote) = await SentQuoteAsync();
        const string evil = "<script>alert(1)</script><b>cheaper</b>\r\nBcc: x@evil.test";
        await using (var db = _kit.Db())
        {
            var q = await db.Quotes.Include(x => x.Versions).SingleAsync(x => x.Id == quote);
            q.Reject(1, t.Buyer, evil, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var sender = new CaptureSender();
        await PublishAllAsync(sender, quote, nameof(QuoteRejectedEvent));
        var mail = sender.Sent.Should().ContainSingle(m => m.To == Inbox).Subject;
        mail.HtmlBody.Should().NotContain("<script>").And.NotContain("<b>cheaper").And.Contain("&lt;script&gt;");
        mail.Subject.Should().NotContain("<").And.NotContain("\n").And.NotContain("evil");
    }

    [PostgresFact]
    public async Task QuoteNoticeWorker_TimeTravel_OneExpiringNoticeOneExpiredNotice_NeverTwice_NeverFlipsAStatus()
    {
        var (t, quote) = await SentQuoteAsync(DateTime.UtcNow.AddHours(-1));
        var validUntil = (await _kit.VersionAsync(quote)).ValidUntilUtc!.Value;
        var buyer = await EmailOf(t.Buyer);
        var sender = new CaptureSender();

        // The database is shared with other tests, so count only this quote's events.
        async Task<int> PassAsync(DateTime now)
        {
            await using var a = _kit.Db();
            await using var b = _kit.Db();
            var before = (await EnvelopesAsync(quote, QuoteNotificationPublisher.ExpiringEvent)).Count + (await EnvelopesAsync(quote, QuoteNotificationPublisher.ExpiredEvent)).Count;
            for (var i = 0; i < 2; i++)   // two workers, two passes each
            {
                await new QuoteNoticeService(a).SweepAsync(now, 200);
                await new QuoteNoticeService(b).SweepAsync(now, 200);
            }
            await PublishAllAsync(sender, quote, QuoteNotificationPublisher.ExpiringEvent);
            await PublishAllAsync(sender, quote, QuoteNotificationPublisher.ExpiredEvent);
            return (await EnvelopesAsync(quote, QuoteNotificationPublisher.ExpiringEvent)).Count + (await EnvelopesAsync(quote, QuoteNotificationPublisher.ExpiredEvent)).Count - before;
        }

        (await PassAsync(validUntil.AddDays(-10))).Should().Be(0, "far from the end: nothing to say");
        sender.Sent.Should().BeEmpty();

        (await PassAsync(validUntil.AddHours(-47))).Should().Be(1, "inside the 48 h window: one expiring event however many passes");
        sender.Sent.Should().ContainSingle(m => m.To == buyer && m.Subject.Contains("expires soon"));
        (await PassAsync(validUntil.AddHours(-2))).Should().Be(0);
        sender.Sent.Count(m => m.To == buyer).Should().Be(1, "never the same notice twice");

        (await PassAsync(validUntil.AddHours(1))).Should().Be(1, "past the end: one expired event");
        sender.Sent.Should().ContainSingle(m => m.To == Inbox && m.Subject.StartsWith("Quote expired"));
        (await PassAsync(validUntil.AddHours(5))).Should().Be(0);
        (await PassAsync(validUntil.AddDays(5))).Should().Be(0, "an old expiry is not announced");
        sender.Sent.Count(m => m.To == Inbox).Should().Be(1);

        (await _kit.VersionAsync(quote)).Status.Should().Be(QuoteVersionStatus.Sent, "the worker only sends notices; expiry is computed");
        (await EnvelopesAsync(quote, QuoteNotificationPublisher.ExpiringEvent)).Should().ContainSingle();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        QuoteNoticeService.EventId(id, "quote.expiring").Should().Be(QuoteNoticeService.EventId(id, "quote.expiring"))
            .And.NotBe(QuoteNoticeService.EventId(id, "quote.expired"));
    }
}
