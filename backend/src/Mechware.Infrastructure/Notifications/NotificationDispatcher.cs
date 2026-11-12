using System.Net.Mail;
using Mechware.Application.Notifications;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Notifications;

/// <summary>One recipient of one notification; the model is built at send time from the database.</summary>
public sealed record NotificationTarget(Guid UserId, string Email, string Locale, IReadOnlyDictionary<string, string> Model);

/// <summary>
/// Sends one template to each target exactly once per (outbox event, recipient, template). The claim is a row in
/// <c>notification_log</c> written with <c>ON CONFLICT DO NOTHING</c> before sending, so a retry or a second relay skips
/// recipients already handled. A transient failure (network, 4xx from the mail server) releases the claim and is rethrown
/// after the other recipients were served, so the outbox retries it. A permanent failure (bad address, 5xx, template error)
/// is marked <c>failed</c> and never retried. Addresses are only ever logged masked.
/// </summary>
public sealed class NotificationDispatcher(
    MechwareDbContext db, INotificationSender sender, INotificationTemplates templates, ILogger<NotificationDispatcher> logger)
{
    public async Task DispatchAsync(Guid eventId, string templateKey, IEnumerable<NotificationTarget> targets, CancellationToken ct = default)
    {
        List<Exception>? transient = null;
        foreach (var target in targets)
        {
            try { await DeliverAsync(eventId, templateKey, target, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (transient ??= []).Add(ex);
            }
        }

        if (transient is not null)
            throw transient.Count == 1 ? transient[0] : new AggregateException("Some notifications could not be sent; they will be retried.", transient);
    }

    private async Task DeliverAsync(Guid eventId, string templateKey, NotificationTarget target, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO notification_log (id, event_id, recipient_user_id, template_key, status, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {eventId}, {target.UserId}, {templateKey}, {NotificationLogOutcome.Sending}, {now}, {now})
            ON CONFLICT (event_id, recipient_user_id, template_key) DO NOTHING", ct);
        if (claimed == 0)
        {
            logger.LogInformation("Notification {Template} for event {EventId} to user {UserId} already handled; skipped", templateKey, eventId, target.UserId);
            return;
        }

        try
        {
            var rendered = templates.Render(templateKey, NotificationFormat.Locale(target.Locale), target.Model);
            await sender.SendEmailAsync(new EmailMessage(target.Email, rendered.Subject, rendered.Html, rendered.Text), ct);
        }
        catch (Exception ex) when (IsPermanent(ex))
        {
            await SetStatusAsync(eventId, target.UserId, templateKey, NotificationLogOutcome.Failed, ex.GetType().Name, ct);
            logger.LogError("Notification {Template} for event {EventId} to {Recipient} failed permanently ({Error}); not retried",
                templateKey, eventId, SmtpEmailSender.Mask(target.Email), ex.GetType().Name);
            return;
        }
        catch
        {
            // Release the claim so the outbox retry sends it.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM notification_log WHERE event_id = {eventId} AND recipient_user_id = {target.UserId} AND template_key = {templateKey}", CancellationToken.None);
            logger.LogWarning("Notification {Template} for event {EventId} to {Recipient} failed; will be retried", templateKey, eventId, SmtpEmailSender.Mask(target.Email));
            throw;
        }

        await SetStatusAsync(eventId, target.UserId, templateKey, NotificationLogOutcome.Sent, null, ct);
    }

    private Task SetStatusAsync(Guid eventId, Guid userId, string templateKey, string status, string? error, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE notification_log SET status = {status}, error = {error}, updated_at = {DateTime.UtcNow}
            WHERE event_id = {eventId} AND recipient_user_id = {userId} AND template_key = {templateKey}", ct);

    /// <summary>Retrying cannot help: a template that does not render, an address that is malformed or refused (5xx).</summary>
    internal static bool IsPermanent(Exception ex) => ex switch
    {
        NotificationTemplateException => true,
        FormatException => true,
        ArgumentException => true, // empty/invalid address
        SmtpFailedRecipientException f => IsPermanentStatus(f.StatusCode),
        SmtpException s => IsPermanentStatus(s.StatusCode),
        _ => false
    };

    private static bool IsPermanentStatus(SmtpStatusCode code) => code is
        SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.UserNotLocalTryAlternatePath or SmtpStatusCode.ExceededStorageAllocation
        or SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.TransactionFailed or SmtpStatusCode.UserNotLocalWillForward
        or SmtpStatusCode.BadCommandSequence or SmtpStatusCode.CommandNotImplemented or SmtpStatusCode.CommandUnrecognized
        or SmtpStatusCode.CommandParameterNotImplemented or SmtpStatusCode.SyntaxError;
}

internal static class NotificationLogOutcome
{
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}
