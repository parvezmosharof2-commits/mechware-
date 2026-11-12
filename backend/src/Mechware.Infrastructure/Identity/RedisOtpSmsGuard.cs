using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Security;
using StackExchange.Redis;

namespace Mechware.Infrastructure.Identity;

/// <summary>Limits on OTP text messages (configuration section <c>OtpSms</c>). Defaults are the F3a.2a values.</summary>
public sealed class OtpSmsLimitsOptions
{
    public const string SectionName = "OtpSms";

    /// <summary>Minimum time between two codes to one number.</summary>
    public int ResendIntervalSeconds { get; set; } = 60;

    /// <summary>Codes to one number per short window (<see cref="PerNumberShortWindowSeconds"/>).</summary>
    public int PerNumberShortLimit { get; set; } = 3;
    public int PerNumberShortWindowSeconds { get; set; } = 600;

    /// <summary>Codes to one number per 24 hours.</summary>
    public int PerNumberDailyLimit { get; set; } = 10;

    /// <summary>Codes requested from one client IP per hour, across all numbers.</summary>
    public int PerIpHourlyLimit { get; set; } = 20;

    /// <summary>Circuit breaker: codes per minute for the whole system (protects the SMS bill).</summary>
    public int GlobalPerMinuteLimit { get; set; } = 100;

    /// <summary>Redis key prefix. Kept in one hash tag so the multi-key script also works on a Redis cluster.</summary>
    public string KeyPrefix { get; set; } = "otp-sms";

    public bool IsValid() =>
        ResendIntervalSeconds >= 0 && PerNumberShortLimit > 0 && PerNumberShortWindowSeconds > 0 && PerNumberDailyLimit > 0
        && PerIpHourlyLimit > 0 && GlobalPerMinuteLimit > 0 && !string.IsNullOrWhiteSpace(KeyPrefix)
        && !KeyPrefix.Contains('{') && !KeyPrefix.Contains('}');
}

/// <summary>
/// <see cref="IOtpSmsGuard"/> on Redis fixed-window counters, shared by every API instance. One Lua script checks every
/// limit and, only when all pass, counts the send in all of them (INCR + EXPIRE), so concurrent requests cannot both take
/// the last slot and a refused request costs nothing. Numbers and IPs appear in keys only as HMAC-SHA256 under the
/// application's blind-index key (<see cref="FieldEncryption"/>), never in clear.
/// Fails closed: without Redis no code is sent, because an unlimited SMS endpoint is a billing and fraud risk while
/// "try again shortly" only delays a sign-in.
/// </summary>
public sealed class RedisOtpSmsGuard : IOtpSmsGuard
{
    // KEYS: 1 resend marker, 2 number short window, 3 number day, 4 IP hour, 5 global minute.
    // ARGV: 1 resend seconds, 2 short limit, 3 short window, 4 day limit, 5 ip limit, 6 global limit.
    // Returns 0 = reserved, 1 resend too soon, 2 number short window, 3 number day, 4 IP, 5 global breaker.
    private const string Script = """
        if tonumber(ARGV[1]) > 0 and redis.call('EXISTS', KEYS[1]) == 1 then return 1 end
        if tonumber(redis.call('GET', KEYS[2]) or '0') >= tonumber(ARGV[2]) then return 2 end
        if tonumber(redis.call('GET', KEYS[3]) or '0') >= tonumber(ARGV[4]) then return 3 end
        if tonumber(redis.call('GET', KEYS[4]) or '0') >= tonumber(ARGV[5]) then return 4 end
        if tonumber(redis.call('GET', KEYS[5]) or '0') >= tonumber(ARGV[6]) then return 5 end
        local function hit(key, ttl)
          redis.call('INCR', key)
          if redis.call('TTL', key) < 0 then redis.call('EXPIRE', key, ttl) end
        end
        if tonumber(ARGV[1]) > 0 then redis.call('SET', KEYS[1], '1', 'EX', ARGV[1]) end
        hit(KEYS[2], ARGV[3])
        hit(KEYS[3], 86400)
        hit(KEYS[4], 3600)
        hit(KEYS[5], 60)
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly OtpSmsLimitsOptions _options;
    private readonly ILogger<RedisOtpSmsGuard> _logger;

    public RedisOtpSmsGuard(IConnectionMultiplexer redis, IOptions<OtpSmsLimitsOptions> options, ILogger<RedisOtpSmsGuard> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    public async Task AcquireAsync(string bdMobileE164, string? clientIp, CancellationToken cancellationToken = default)
    {
        string prefix = "{" + _options.KeyPrefix + "}";
        string number = Fingerprint("phone", bdMobileE164);
        string ip = Fingerprint("ip", string.IsNullOrWhiteSpace(clientIp) ? "none" : clientIp);
        RedisKey[] keys =
        [
            $"{prefix}:resend:{number}",
            $"{prefix}:n-short:{number}",
            $"{prefix}:n-day:{number}",
            $"{prefix}:ip:{ip}",
            $"{prefix}:global",
        ];
        RedisValue[] args =
        [
            _options.ResendIntervalSeconds, _options.PerNumberShortLimit, _options.PerNumberShortWindowSeconds,
            _options.PerNumberDailyLimit, _options.PerIpHourlyLimit, _options.GlobalPerMinuteLimit,
        ];

        int outcome;
        try
        {
            outcome = (int)await _redis.GetDatabase().ScriptEvaluateAsync(Script, keys, args);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)   // RedisTimeoutException is a TimeoutException
        {
            _logger.LogError(ex, "OTP SMS limits could not be checked (Redis unavailable); the code was not sent (fail closed).");
            throw new ServiceUnavailableException("SMS_UNAVAILABLE", "The code could not be sent right now. Please try again shortly.");
        }

        switch (outcome)
        {
            case 0:
                return;
            case 5:
                _logger.LogError(
                    "OTP SMS circuit breaker open: more than {Limit} codes in a minute system-wide; refusing sends until the window ends.",
                    _options.GlobalPerMinuteLimit);
                break;
            default:
                _logger.LogWarning("OTP SMS refused by abuse limit {Limit} (number {NumberKey}, ip {IpKey}).",
                    outcome switch { 1 => "resend-interval", 2 => "per-number-short", 3 => "per-number-day", _ => "per-ip-hour" },
                    number[..8], ip[..8]);
                break;
        }

        throw new TooManyRequestsException();
    }

    private static string Fingerprint(string kind, string value) =>
        FieldEncryption.Current.BlindIndex($"otp-sms|{kind}|{value}")[..32];
}
