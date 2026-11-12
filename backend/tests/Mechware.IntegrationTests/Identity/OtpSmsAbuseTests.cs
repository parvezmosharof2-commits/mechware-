using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;

namespace Mechware.IntegrationTests.Identity;

/// <summary>
/// F3a.2a: OTP SMS abuse limits (SMS pumping / toll fraud, OTP brute force by re-sending, account enumeration) over real
/// PostgreSQL and Redis. Every guard gets its own Redis key prefix, so tests and repeated runs never share counters.
/// </summary>
public class OtpSmsAbuseTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string Ip = "203.112.218.10";
    private static readonly Lazy<ConnectionMultiplexer> Redis = new(() => ConnectionMultiplexer.Connect(TestInfra.RedisConnection));

    public Task InitializeAsync() => TestInfra.PostgresAvailable
        ? IdentityTestKit.SeedAsync(fixture, new IdentitySeedPolicy(true))
        : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private static string RandomPhone() => "+88017" + Random.Shared.Next(10_000_000, 99_999_999);

    private static RedisOtpSmsGuard Guard(OtpSmsLimitsOptions? options = null, ILogger<RedisOtpSmsGuard>? logger = null, IConnectionMultiplexer? redis = null)
    {
        options ??= new OtpSmsLimitsOptions();
        options.KeyPrefix = "otp-sms-test-" + Guid.NewGuid().ToString("N");
        return new RedisOtpSmsGuard(redis ?? Redis.Value, Options.Create(options), logger ?? NullLogger<RedisOtpSmsGuard>.Instance);
    }

    private async Task<SendOtpResponseDto> SendAsync(IOtpSmsGuard guard, RecordingSms sms, string recipient, string ip = Ip)
    {
        var (db, service) = IdentityTestKit.Open(fixture, sms: sms, smsGuard: guard);
        await using (db) return (await service.SendOtpAsync(new SendOtpRequestDto(recipient, "Login"), ip)).Response;
    }

    private async Task<(SendOtpResponseDto Response, string Code)> SendWithCodeAsync(string recipient)
    {
        var (db, service) = IdentityTestKit.Open(fixture);
        await using (db) return await service.SendOtpAsync(new SendOtpRequestDto(recipient, "Login"), Ip);
    }

    private async Task<RegisterResponseDto> RegisterAsync(string phone, IOtpSmsGuard? guard = null, RecordingSms? sms = null)
    {
        var (db, service) = IdentityTestKit.Open(fixture, sms: sms, smsGuard: guard);
        await using (db)
        {
            return (await service.RegisterAsync(
                new RegisterRequestDto($"otp.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd", phone, "OtpLimits#2026!", "Otp Tester"), Ip)).Response;
        }
    }

    /// <summary>A number that has an account (registered without limits, so the test's own guard starts empty).</summary>
    private async Task<string> RegisteredPhoneAsync()
    {
        string phone = RandomPhone();
        await RegisterAsync(phone);
        return phone;
    }

    /// <summary>Every stored code for the recipient (a registered number already holds its registration code).</summary>
    private async Task<int> CodesFor(string recipient)
    {
        await using var db = fixture.CreateContext();
        return await db.OtpCodes.CountAsync(o => o.Recipient == recipient);
    }

    [PostgresAndRedisFact]
    public async Task FourthCode_WithinTenMinutes_IsRefused_AndNoSmsIsSent()
    {
        string phone = await RegisteredPhoneAsync();
        var guard = Guard(new OtpSmsLimitsOptions { ResendIntervalSeconds = 0 });
        var sms = new RecordingSms();

        for (int i = 0; i < 3; i++) await SendAsync(guard, sms, phone);
        sms.Sent.Should().HaveCount(3);

        Func<Task> fourth = () => SendAsync(guard, sms, phone);
        await fourth.Should().ThrowAsync<TooManyRequestsException>();
        sms.Sent.Should().HaveCount(3, "a refused request sends nothing");
        (await CodesFor(phone)).Should().Be(1 + 3, "and creates no code (registration code + three sent)");
    }

    [PostgresAndRedisFact]
    public async Task SecondCode_WithinTheResendInterval_IsRefused()
    {
        string phone = await RegisteredPhoneAsync();
        var guard = Guard();
        var sms = new RecordingSms();

        await SendAsync(guard, sms, phone);
        Func<Task> again = () => SendAsync(guard, sms, phone);
        await again.Should().ThrowAsync<TooManyRequestsException>();
        sms.Sent.Should().ContainSingle();
    }

    [PostgresAndRedisFact]
    public async Task DailyCap_PerNumber_HoldsEvenWhenTheShortWindowAllowsMore()
    {
        string phone = await RegisteredPhoneAsync();
        var guard = Guard(new OtpSmsLimitsOptions { ResendIntervalSeconds = 0, PerNumberShortLimit = 100, PerNumberDailyLimit = 2 });
        var sms = new RecordingSms();

        await SendAsync(guard, sms, phone);
        await SendAsync(guard, sms, phone);
        Func<Task> third = () => SendAsync(guard, sms, phone);
        await third.Should().ThrowAsync<TooManyRequestsException>();
        sms.Sent.Should().HaveCount(2);
    }

    [PostgresAndRedisFact]
    public async Task EverySpellingOfANumber_SharesOneBudget()
    {
        string phone = await RegisteredPhoneAsync();   // +88017XXXXXXXX
        string local = "0" + phone[4..];
        var guard = Guard(new OtpSmsLimitsOptions { ResendIntervalSeconds = 0 });
        var sms = new RecordingSms();

        (await SendAsync(guard, sms, local)).Recipient.Should().Be(phone);
        await SendAsync(guard, sms, phone[1..]);
        await SendAsync(guard, sms, $"+880 {phone[4..8]}-{phone[8..]}");
        Func<Task> fourth = () => SendAsync(guard, sms, phone);
        await fourth.Should().ThrowAsync<TooManyRequestsException>();
        sms.Sent.Should().HaveCount(3).And.OnlyContain(p => p == phone);
    }

    [PostgresAndRedisFact]
    public async Task PerIpCap_CountsAcrossDifferentNumbers_AndOtherIpsAreUnaffected()
    {
        var guard = Guard(new OtpSmsLimitsOptions { ResendIntervalSeconds = 0, PerIpHourlyLimit = 2 });
        var sms = new RecordingSms();
        string[] phones = [await RegisteredPhoneAsync(), await RegisteredPhoneAsync(), await RegisteredPhoneAsync()];

        await SendAsync(guard, sms, phones[0]);
        await SendAsync(guard, sms, phones[1]);
        Func<Task> third = () => SendAsync(guard, sms, phones[2]);
        await third.Should().ThrowAsync<TooManyRequestsException>();
        sms.Sent.Should().HaveCount(2);

        await SendAsync(guard, sms, phones[2], ip: "103.4.145.2");
        sms.Sent.Should().HaveCount(3);
    }

    [PostgresAndRedisFact]
    public async Task GlobalCircuitBreaker_RefusesEveryoneOverTheSystemLimit_AndLogsAnError()
    {
        var logger = new ListLogger<RedisOtpSmsGuard>();
        var guard = Guard(new OtpSmsLimitsOptions { GlobalPerMinuteLimit = 2 }, logger);
        var sms = new RecordingSms();
        string[] phones = [await RegisteredPhoneAsync(), await RegisteredPhoneAsync(), await RegisteredPhoneAsync()];

        await SendAsync(guard, sms, phones[0], ip: "103.4.145.11");
        await SendAsync(guard, sms, phones[1], ip: "103.4.145.12");
        Func<Task> third = () => SendAsync(guard, sms, phones[2], ip: "103.4.145.13");
        await third.Should().ThrowAsync<TooManyRequestsException>();

        sms.Sent.Should().HaveCount(2);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("circuit breaker"));
    }

    [PostgresAndRedisFact]
    public async Task NonBangladeshiNumbers_AreRefused_BeforeAnyCodeOrMessageExists()
    {
        var guard = new CountingGuard();
        var sms = new RecordingSms();

        foreach (string foreign in new[] { "+14155550123", "+447700900123", "+880211000001" })
        {
            Func<Task> send = () => SendAsync(guard, sms, foreign);
            (await send.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().Contain(e => e.Field == "recipient");
            (await CodesFor(foreign)).Should().Be(0);
            (await CodesFor(foreign.ToLowerInvariant())).Should().Be(0);
        }

        Func<Task> register = () => RegisterAsync("+447700900123", guard, sms);
        (await register.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().Contain(e => e.Field == "phone");

        sms.Sent.Should().BeEmpty();
        guard.Calls.Should().Be(0, "a refused destination spends no limit");
    }

    [PostgresAndRedisFact]
    public async Task KnownAndUnknownNumbers_GetTheSameAnswer_AndTheSameThrottle()
    {
        string known = await RegisteredPhoneAsync();
        string unknown = RandomPhone();
        var guard = Guard();
        var sms = new RecordingSms();

        SendOtpResponseDto a = await SendAsync(guard, sms, known);
        SendOtpResponseDto b = await SendAsync(guard, sms, unknown);
        (a.Type, a.ExpiresInSeconds, a.MaxAttempts, a.ReferenceId.Length).Should().Be((b.Type, b.ExpiresInSeconds, b.MaxAttempts, b.ReferenceId.Length));
        a.Recipient.Should().Be(known);
        b.Recipient.Should().Be(unknown);

        Func<Task> againKnown = () => SendAsync(guard, sms, known);
        Func<Task> againUnknown = () => SendAsync(guard, sms, unknown);
        await againKnown.Should().ThrowAsync<TooManyRequestsException>();
        await againUnknown.Should().ThrowAsync<TooManyRequestsException>();

        // Only the account holder's phone gets a text: nobody could use a code sent to a number without an account.
        sms.Sent.Should().Equal(known);
    }

    [PostgresAndRedisFact]
    public async Task Registration_ThrottlesANumberWithAnAccountExactlyLikeANewOne()
    {
        string taken = await RegisteredPhoneAsync();
        string fresh = RandomPhone();
        var guard = Guard(new OtpSmsLimitsOptions { PerNumberShortLimit = 1, ResendIntervalSeconds = 0 });

        RegisterResponseDto first = await RegisterAsync(taken, guard);
        RegisterResponseDto second = await RegisterAsync(fresh, guard);
        (first.Status, first.VerificationChannel).Should().Be((second.Status, second.VerificationChannel));

        Func<Task> takenAgain = () => RegisterAsync(taken, guard);
        Func<Task> freshAgain = () => RegisterAsync(fresh, guard);
        await takenAgain.Should().ThrowAsync<TooManyRequestsException>();
        await freshAgain.Should().ThrowAsync<TooManyRequestsException>();
    }

    [PostgresAndRedisFact]
    public async Task AfterThreeWrongCodes_TheRightCodeIsRefused_TheCodeIsBurned()
    {
        string phone = await RegisteredPhoneAsync();
        (SendOtpResponseDto sent, string code) = await SendWithCodeAsync(phone);
        string wrong = code == "000000" ? "111111" : "000000";

        for (int i = 0; i < OtpCode.MaxVerificationAttempts; i++)
        {
            Func<Task> guess = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, wrong, "Login", sent.ReferenceId)));
            (await guess.Should().ThrowAsync<ValidationException>()).WithMessage("Invalid verification code.");
        }

        Func<Task> right = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, code, "Login", sent.ReferenceId)));
        (await right.Should().ThrowAsync<ValidationException>()).WithMessage("*Maximum OTP verification attempts*");
    }

    [PostgresAndRedisFact]
    public async Task ACodeSentToTheLocalSpelling_VerifiesWithTheInternationalOne()
    {
        string phone = await RegisteredPhoneAsync();
        (SendOtpResponseDto sent, string code) = await SendWithCodeAsync("0" + phone[4..]);

        AuthTokenResponseDto session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, code, "Login", sent.ReferenceId), Ip, "Mozilla/5.0"));
        session.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task WithoutRedis_NoCodeIsSent_FailClosed()
    {
        string phone = await RegisteredPhoneAsync();
        await using var down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=500,asyncTimeout=500");
        var logger = new ListLogger<RedisOtpSmsGuard>();
        var guard = Guard(logger: logger, redis: down);
        var sms = new RecordingSms();

        Func<Task> send = () => SendAsync(guard, sms, phone);
        (await send.Should().ThrowAsync<ServiceUnavailableException>()).Which.Code.Should().Be("SMS_UNAVAILABLE");
        sms.Sent.Should().BeEmpty();
        (await CodesFor(phone)).Should().Be(1, "only the registration code; the refused send created none");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [PostgresAndRedisFact]
    public async Task OverTheLimit_TheApiAnswers429_InTheSameEnvelopeAsTheEndpointLimiter()
    {
        using var factory = new DbBackedFactory(settings: new Dictionary<string, string>
        {
            ["ConnectionStrings:Redis"] = TestInfra.RedisConnection,
            ["OtpSms:KeyPrefix"] = "otp-sms-api-" + Guid.NewGuid().ToString("N"),
        });
        var client = factory.CreateClient();
        var body = new { recipient = RandomPhone(), type = "Login" };

        using var first = await client.PostAsJsonAsync("/api/v1/auth/otp/send", body);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await client.PostAsJsonAsync("/api/v1/auth/otp/send", body);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("RATE_LIMITED");
        json.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be(TooManyRequestsException.GenericMessage);

        using var foreign = await client.PostAsJsonAsync("/api/v1/auth/otp/send", new { recipient = "+14155550123", type = "Login" });
        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class RecordingSms : ISmsProvider
    {
        private readonly ConcurrentQueue<string> _sent = new();
        public IReadOnlyCollection<string> Sent => _sent.ToArray();
        public Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
        {
            _sent.Enqueue(phoneNumber);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingGuard : IOtpSmsGuard
    {
        public int Calls { get; private set; }
        public Task AcquireAsync(string bdMobileE164, string? clientIp, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
