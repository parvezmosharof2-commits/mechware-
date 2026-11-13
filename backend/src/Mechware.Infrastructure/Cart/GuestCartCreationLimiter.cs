using System.Collections.Concurrent;
using Mechware.Application.Cart;

namespace Mechware.Infrastructure.Cart;

/// <summary>
/// At most <see cref="DefaultPerHour"/> new guest carts per IP per hour (CT11), counted when a cart id is minted, never on
/// reads. Fixed window, in memory: each API replica counts on its own. The number is an owner-reviewable default
/// (<c>Cart:NewGuestCartsPerIpPerHour</c>); an office behind one NAT address shares it, and signed-in users are not counted.
/// </summary>
public sealed class GuestCartCreationLimiter(int perHour, TimeProvider clock) : IGuestCartCreationLimiter
{
    public const int DefaultPerHour = 20;
    private const int PruneAbove = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _counts = new();

    public bool TryAcquire(string? ipAddress)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        string key = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress;
        if (_counts.Count > PruneAbove)
        {
            foreach (var (k, v) in _counts) if (now - v.WindowStart >= Window) _counts.TryRemove(k, out _);
        }

        bool allowed = false;
        _counts.AddOrUpdate(key,
            _ => { allowed = true; return (now, 1); },
            (_, current) =>
            {
                if (now - current.WindowStart >= Window) { allowed = true; return (now, 1); }
                allowed = current.Count < Math.Max(1, perHour);
                return allowed ? (current.WindowStart, current.Count + 1) : current;
            });
        return allowed;
    }
}
