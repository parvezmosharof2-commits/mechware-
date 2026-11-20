using System.Globalization;
using System.Text.Json;
using Mechware.Application.Common;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Notifications;

/// <summary>Where the staff notices without a named person go (config <c>Quotes:SalesInboxEmail</c>, owner data; empty = nobody).</summary>
public sealed record QuoteNoticeOptions(string? SalesInboxEmail = null);

/// <summary>
/// Outbox consumer for the quote notices (ADR-0011 §14, D4.12). E-mail only: there is no SMS provider at first launch and no in-app
/// store yet. Recipients and values are read at send time (ids only in the payload). Buyer notices (<c>quote-sent</c>,
/// <c>quote-expiring</c>) go to the quote's contact only while they are an effective member of the quote's company (active, accepted,
/// QT6); when they are not, the assigned staff and the sales inbox are told instead. Staff notices use the English template
/// <c>quote-staff-notice</c>; approval-needed goes to staff holding <c>quotes.approve</c> minus everyone who wrote, contributed to or
/// submitted that version (four-eyes). Quote text is only ever a value of a template, so the engine encodes it (QT17) and it never
/// reaches a subject. Mail contains a link to the quote page, never the PDF.
/// </summary>
public sealed class QuoteNotificationPublisher(MechwareDbContext db, NotificationDispatcher dispatcher, StorefrontLinks links, QuoteNoticeOptions options) : IOutboxPublisher
{
    public const string QuoteSent = "quote-sent";
    public const string QuoteExpiring = "quote-expiring";
    public const string StaffNotice = "quote-staff-notice";
    public const string ExpiringEvent = "QuoteExpiringEvent";
    public const string ExpiredEvent = "QuoteExpiredEvent";
    private const int MaxDetail = 500;

    /// <summary>Stable pseudo user id of the sales inbox in <c>notification_log</c> (it has no user row).</summary>
    public static readonly Guid SalesInboxId = new("00000000-0000-0000-0000-00005a1e5000");

    public Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default) => message.EventType switch
    {
        nameof(RfqSubmittedEvent) => RfqAsync(message, ct),
        nameof(QuoteApprovalRequestedEvent) => ApprovalAsync(message, ct),
        nameof(QuoteSentEvent) => BuyerAsync(message, QuoteSent, ct),
        ExpiringEvent => BuyerAsync(message, QuoteExpiring, ct),
        nameof(QuoteAcceptedEvent) => StaffAboutQuoteAsync(message, "Quote accepted", "The buyer accepted version {0} of this quote; an order was placed.", ct),
        nameof(QuoteRejectedEvent) => StaffAboutQuoteAsync(message, "Quote rejected", "The buyer rejected version {0} of this quote.", ct),
        ExpiredEvent => StaffAboutQuoteAsync(message, "Quote expired", "Version {0} of this quote passed its valid-until time without an answer.", ct),
        _ => Task.CompletedTask
    };

    // ---- buyer notices -------------------------------------------------------------------------------------------------------

    private async Task BuyerAsync(OutboxEnvelope message, string template, CancellationToken ct)
    {
        var (quoteId, versionId) = (PayloadGuid(message, "quoteId"), PayloadGuid(message, "versionId"));
        var q = await LoadAsync(quoteId, versionId, ct);
        if (q is null) return;
        // An expiring notice is moot when the version is no longer open for an answer (accepted, rejected, withdrawn, superseded).
        if (template == QuoteExpiring && q.Version.Status != QuoteVersionStatus.Sent) return;

        var contact = await db.Users.AsNoTracking().Where(u => u.Id == q.Quote.ContactUserId && u.Status == UserStatus.Active)
            .Select(u => new Person(u.Id, u.Email, u.FullName, u.Locale)).SingleOrDefaultAsync(ct);
        var member = contact is not null && await db.CompanyMembers.AsNoTracking()
            .AnyAsync(m => m.CompanyId == q.Quote.CompanyId && m.UserId == contact.Id && m.IsActive && m.AcceptedAtUtc != null, ct);
        if (contact is null || !member)
        {
            await StaffAsync(message.Id, q.Quote.AssignedTo, "Quote notice not sent",
                $"The contact of quote {q.Quote.Number} is no longer an active member of {q.Company}, so the buyer was not e-mailed.", q.Quote.Number, q.Company, ct);
            return;
        }

        var locale = NotificationFormat.Locale(contact.Locale);
        var model = new Dictionary<string, string>
        {
            ["customerName"] = contact.FullName,
            ["quoteNumber"] = q.Quote.Number,
            ["validUntil"] = ValidUntilText(q.Version),
            ["quoteUrl"] = links.Url(locale, $"account/company/{q.Quote.CompanyId}/quotes/{q.Quote.Id}"),
        };
        if (template == QuoteSent) model["total"] = NotificationFormat.Money(q.Version.GrandTotalMinor ?? q.Version.GoodsTotalMinor);
        await dispatcher.DispatchAsync(message.Id, template, [new NotificationTarget(contact.Id, contact.Email, locale, model)], ct);
    }

    private static string ValidUntilText(QuoteVersion v) =>
        v.ValidUntilUtc is { } until
            ? QuoteValidity.DhakaDate(until.AddTicks(-1)).ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : "the date on the quote";

    // ---- staff notices -------------------------------------------------------------------------------------------------------

    private async Task RfqAsync(OutboxEnvelope message, CancellationToken ct)
    {
        var rfqId = PayloadGuid(message, "rfqId");
        var rfq = await db.Rfqs.AsNoTracking().Where(r => r.Id == rfqId).Select(r => new { r.Number, r.CompanyId, r.LineCount }).SingleOrDefaultAsync(ct);
        if (rfq is null) return;
        var company = await CompanyNameAsync(rfq.CompanyId, ct);
        var targets = await InboxOrDeskAsync(ct);
        await SendStaffAsync(message.Id, targets, "RFQ received", $"{company} asked for a quote ({rfq.LineCount} lines).", rfq.Number, company, ct);
    }

    private async Task ApprovalAsync(OutboxEnvelope message, CancellationToken ct)
    {
        var (quoteId, versionId) = (PayloadGuid(message, "quoteId"), PayloadGuid(message, "versionId"));
        var q = await LoadAsync(quoteId, versionId, ct);
        if (q is null || q.Version.Status != QuoteVersionStatus.PendingApproval) return;

        var authors = new HashSet<Guid> { q.Quote.CreatedBy, q.Version.CreatedBy };
        if (q.Version.SubmittedBy is { } s) authors.Add(s);
        foreach (var c in q.Version.Contributors) authors.Add(c.UserId);

        var approvers = await (from u in db.Users.AsNoTracking()
                               where u.Status == UserStatus.Active && !authors.Contains(u.Id)
                                     && (from ur in db.UserRoles
                                         join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                                         join p in db.Permissions on rp.PermissionId equals p.Id
                                         where ur.UserId == u.Id && p.Code == Permissions.QuotesApprove
                                         select 1).Any()
                               select new Person(u.Id, u.Email, u.FullName, u.Locale)).ToListAsync(ct);
        await SendStaffAsync(message.Id, approvers, "Approval needed",
            $"Version {q.Version.VersionNo} of this quote for {q.Company} is waiting for your approval in the console.", q.Quote.Number, q.Company, ct);
    }

    private async Task StaffAboutQuoteAsync(OutboxEnvelope message, string headline, string detailFormat, CancellationToken ct)
    {
        var (quoteId, versionId) = (PayloadGuid(message, "quoteId"), PayloadGuid(message, "versionId"));
        var q = await LoadAsync(quoteId, versionId, ct);
        if (q is null) return;
        var detail = string.Format(CultureInfo.InvariantCulture, detailFormat, q.Version.VersionNo);
        if (message.EventType == nameof(QuoteRejectedEvent) && !string.IsNullOrWhiteSpace(q.Version.RejectReason))
            detail += " Reason: " + Cut(q.Version.RejectReason!);
        await StaffAsync(message.Id, q.Quote.AssignedTo, headline, detail, q.Quote.Number, q.Company, ct);
    }

    /// <summary>The assigned staff member (when active) and the sales inbox.</summary>
    private async Task StaffAsync(Guid eventId, Guid? assignedTo, string headline, string detail, string reference, string company, CancellationToken ct)
    {
        var targets = new List<Person>();
        if (assignedTo is { } id)
            targets.AddRange(await db.Users.AsNoTracking().Where(u => u.Id == id && u.Status == UserStatus.Active)
                .Select(u => new Person(u.Id, u.Email, u.FullName, u.Locale)).ToListAsync(ct));
        if (Inbox() is { } inbox) targets.Add(inbox);
        await SendStaffAsync(eventId, targets, headline, detail, reference, company, ct);
    }

    private async Task<List<Person>> InboxOrDeskAsync(CancellationToken ct)
    {
        if (Inbox() is { } inbox) return [inbox];
        return await (from u in db.Users.AsNoTracking()
                      where u.Status == UserStatus.Active
                            && (from ur in db.UserRoles
                                join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                                join p in db.Permissions on rp.PermissionId equals p.Id
                                where ur.UserId == u.Id && p.Code == Permissions.QuotesEdit
                                select 1).Any()
                      select new Person(u.Id, u.Email, u.FullName, u.Locale)).ToListAsync(ct);
    }

    private Person? Inbox() =>
        string.IsNullOrWhiteSpace(options.SalesInboxEmail) ? null : new Person(SalesInboxId, options.SalesInboxEmail.Trim(), "Sales desk", "en");

    private Task SendStaffAsync(Guid eventId, IEnumerable<Person> people, string headline, string detail, string reference, string company, CancellationToken ct) =>
        dispatcher.DispatchAsync(eventId, StaffNotice, [.. people.DistinctBy(p => p.Id).Select(p => new NotificationTarget(p.Id, p.Email, "en", new Dictionary<string, string>
        {
            ["staffName"] = p.FullName,
            ["headline"] = headline,
            ["reference"] = reference,
            ["company"] = company,
            ["detail"] = detail,
        }))], ct);

    // ---- reads ---------------------------------------------------------------------------------------------------------------

    private async Task<Loaded?> LoadAsync(Guid quoteId, Guid versionId, CancellationToken ct)
    {
        var quote = await db.Quotes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == quoteId, ct);
        var version = await db.QuoteVersions.AsNoTracking().Include(v => v.Contributors).AsSplitQuery()
            .SingleOrDefaultAsync(v => v.Id == versionId && v.QuoteId == quoteId, ct);
        return quote is null || version is null ? null : new Loaded(quote, version, await CompanyNameAsync(quote.CompanyId, ct));
    }

    private async Task<string> CompanyNameAsync(Guid companyId, CancellationToken ct) =>
        await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.LegalName).SingleOrDefaultAsync(ct) ?? "a company";

    private static string Cut(string s) => s.Length <= MaxDetail ? s : s[..MaxDetail] + "...";

    private static Guid PayloadGuid(OutboxEnvelope m, string name)
    {
        using var doc = JsonDocument.Parse(m.Payload);
        return doc.RootElement.GetProperty(name).GetGuid();
    }

    private sealed record Person(Guid Id, string Email, string FullName, string Locale);
    private sealed record Loaded(Quote Quote, QuoteVersion Version, string Company);
}
