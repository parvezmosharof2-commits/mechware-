using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace ShopVerse.Api.Configuration;

/// <summary>
/// Which reverse proxies may tell the API the real client address. Rate limits (login, the global limit) are counted per
/// client IP, so this decides whether they work at all behind Cloudflare or a load balancer.
///
/// Finding #6 of the 2026-11-04 test: only loopback was trusted, so behind a proxy on another host every visitor would
/// share the proxy's address, and five failed logins anywhere would lock everyone out of signing in for a minute.
///
/// Settings (section <c>ForwardedHeaders</c>), each a list or a comma-separated string:
/// <list type="bullet">
/// <item><c>KnownNetworks</c>: CIDR ranges of the proxies, e.g. the load balancer subnet or Cloudflare's published ranges.</item>
/// <item><c>KnownProxies</c>: single proxy addresses.</item>
/// <item><c>ForwardLimit</c>: how many proxy hops to unwrap (default 1).</item>
/// </list>
/// When either list is set it replaces the loopback default. A malformed entry stops startup: a typo here would silently
/// turn every client into one.
/// </summary>
public static class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public static void Apply(ForwardedHeadersOptions options, IConfiguration configuration, bool trustAnyProxy)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        var section = configuration.GetSection(SectionName);
        var limit = section.GetValue<int?>("ForwardLimit") ?? 1;
        if (limit is < 1 or > 5)
            throw new InvalidOperationException($"{SectionName}:ForwardLimit must be between 1 and 5, not {limit}.");
        options.ForwardLimit = limit;

        if (trustAnyProxy)
        {
            // Development and tests: there is no proxy to configure, and tests send X-Forwarded-For directly.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            return;
        }

        var networks = ReadList(section, "KnownNetworks");
        var proxies = ReadList(section, "KnownProxies");
        if (networks.Count == 0 && proxies.Count == 0) return;   // only loopback is trusted, the safe default

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var cidr in networks)
        {
            if (!System.Net.IPNetwork.TryParse(cidr, out var network))
                throw new InvalidOperationException($"{SectionName}:KnownNetworks has an entry that is not a CIDR range: '{cidr}'.");
            options.KnownIPNetworks.Add(network);
        }

        foreach (var ip in proxies)
        {
            if (!IPAddress.TryParse(ip, out var address))
                throw new InvalidOperationException($"{SectionName}:KnownProxies has an entry that is not an IP address: '{ip}'.");
            options.KnownProxies.Add(address);
        }
    }

    private static List<string> ReadList(IConfigurationSection section, string key)
    {
        var child = section.GetSection(key);
        var values = child.GetChildren().Any()
            ? child.GetChildren().Select(c => c.Value ?? string.Empty)
            : (child.Value ?? string.Empty).Split(',');
        return values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
    }
}
