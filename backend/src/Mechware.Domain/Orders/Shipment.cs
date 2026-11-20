using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Quotes;

namespace Mechware.Domain.Orders;

/// <summary>Staff started work on a confirmed order (F1.1). Published through the outbox for later mails.</summary>
public sealed record OrderProcessingStartedEvent(Guid OrderId, string OrderNumber, Guid UserId, Guid ActorId) : DomainEventBase;

/// <summary>The order is packed and waits for the courier (F1.1).</summary>
public sealed record OrderPackedEvent(Guid OrderId, string OrderNumber, Guid UserId, Guid ActorId) : DomainEventBase;

/// <summary>The order was handed to a courier (F1.1, manual tracking): what the buyer's "shipped" mail needs.</summary>
public sealed record OrderShippedEvent(
    Guid OrderId, string OrderNumber, Guid UserId, Guid ShipmentId, string CourierName, string TrackingNumber, string? TrackingUrl, Guid ActorId)
    : DomainEventBase;

/// <summary>The buyer received the order (F1.1).</summary>
public sealed record OrderDeliveredEvent(Guid OrderId, string OrderNumber, Guid UserId, Guid? ShipmentId, Guid ActorId) : DomainEventBase;

/// <summary>
/// One parcel handed to a courier (F1.1: manual, typed in by staff; courier adapters are F1.2). One per order for now (unique index);
/// partial shipments (F1.4) add more rows per order, so nothing here assumes there is only one.
/// </summary>
public sealed class Shipment : Entity<Guid>
{
    public const int CourierNameMinLength = 2;
    public const int CourierNameMaxLength = 80;
    public const int TrackingNumberMaxLength = 60;
    public const int TrackingUrlMaxLength = 500;

    /// <summary>
    /// Hosts a tracking link may point to (and their subdomains). The link is shown to buyers on our own pages, so a free URL would let a
    /// careless or stolen staff account plant a phishing link there; https on a known courier site cannot. Empty link = none.
    /// </summary>
    public static readonly IReadOnlyList<string> TrackingUrlHosts = ["steadfast.com.bd", "pathao.com", "redx.com.bd", "paperfly.com.bd", "ecourier.com.bd"];

    public Guid OrderId { get; private set; }
    public string CourierName { get; private set; } = string.Empty;
    public string TrackingNumber { get; private set; } = string.Empty;
    public string? TrackingUrl { get; private set; }
    public DateTime ShippedAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }
    public Guid CreatedBy { get; private set; }

    private Shipment() { }

    internal Shipment(Guid orderId, string courierName, string trackingNumber, string? trackingUrl, Guid actorId, DateTime nowUtc) : base(Guid.NewGuid())
    {
        OrderId = orderId;
        CourierName = courierName;
        TrackingNumber = trackingNumber;
        TrackingUrl = trackingUrl;
        ShippedAtUtc = nowUtc;
        CreatedBy = actorId;
        CreatedAtUtc = nowUtc;
    }

    internal void MarkDelivered(DateTime nowUtc)
    {
        DeliveredAtUtc ??= nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Plain text (no control or invisible characters), white space collapsed, 2 to 80 characters.</summary>
    public static string NormaliseCourierName(string? value)
    {
        var cleaned = PlainText.Clean(value) ?? string.Empty;
        var name = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is < CourierNameMinLength or > CourierNameMaxLength)
            throw new ValidationException("courierName", $"Give the courier's name ({CourierNameMinLength} to {CourierNameMaxLength} characters).");
        return name;
    }

    /// <summary>Trimmed; 1 to 60 ASCII letters, digits and dashes.</summary>
    public static string NormaliseTrackingNumber(string? value)
    {
        var t = value?.Trim() ?? string.Empty;
        if (t.Length is 0 or > TrackingNumberMaxLength || !t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ValidationException("trackingNumber", $"A tracking number is 1 to {TrackingNumberMaxLength} letters, digits or dashes.");
        return t;
    }

    /// <summary>Blank = no link. Otherwise an absolute https URL on <see cref="TrackingUrlHosts"/>, default port, no user name, at most 500 characters.</summary>
    public static string? NormaliseTrackingUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        if (t.Length > TrackingUrlMaxLength || !Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !IsAllowedHost(uri.IdnHost) || uri.AbsoluteUri.Length > TrackingUrlMaxLength)
            throw new ValidationException("trackingUrl",
                $"A tracking link is an https address on a known courier site ({string.Join(", ", TrackingUrlHosts)}), or leave it empty.");
        return uri.AbsoluteUri;
    }

    private static bool IsAllowedHost(string host)
    {
        var h = host.ToLowerInvariant();
        return TrackingUrlHosts.Any(a => h == a || h.EndsWith("." + a, StringComparison.Ordinal));
    }
}
