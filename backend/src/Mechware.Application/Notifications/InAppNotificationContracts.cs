namespace Mechware.Application.Notifications;

/// <summary>
/// One in-app notice. It carries a translation key and its values, never rendered text or HTML: the web renders it in the buyer's language.
/// <see cref="Link"/> is always a path on the web app that starts with "/".
/// </summary>
public sealed record InAppNotificationDto(Guid Id, string Kind, string TitleKey, IReadOnlyDictionary<string, string> Params, string Link, DateTime CreatedAt, DateTime? ReadAt);

public sealed record InAppNotificationPage(IReadOnlyList<InAppNotificationDto> Items, int Page, int PageSize, int Total);

/// <summary>The buyer's own in-app notices. Rows of other users behave exactly like rows that do not exist.</summary>
public interface IInAppNotificationService
{
    public const int MaxPageSize = 50;

    Task<InAppNotificationPage> ListAsync(Guid userId, bool unreadOnly, int page, CancellationToken ct = default);
    Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Marks one notice read (already read is fine). Not yours or unknown: <see cref="KeyNotFoundException"/>.</summary>
    Task MarkReadAsync(Guid userId, Guid id, CancellationToken ct = default);

    /// <summary>Marks all of the caller's unread notices read; returns how many changed.</summary>
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default);
}
