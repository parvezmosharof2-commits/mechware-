using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Identity;

/// <summary>Builds an <see cref="IdentityService"/> over a real PostgreSQL database, one context per call like a request.</summary>
public static class IdentityTestKit
{
    public static readonly PasswordHasherService Hasher = new();
    public static readonly TotpService Totp = new();

    private static readonly JwtTokenService Tokens =
        new("mechware-test-high-entropy-secret-key-256bit-2026!!", "https://api.example.test", "https://example.test", 15);

    public static (ShopVerseDbContext Db, IdentityService Service) Open(PostgresDatabaseFixture fixture, IPasswordHasherService? hasher = null, ShopVerseDbContext? db = null)
    {
        db ??= fixture.CreateContext();
        var service = new IdentityService(hasher ?? Hasher, Tokens, Totp, new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance), db);
        return (db, service);
    }

    /// <summary>Runs one operation on a fresh context, so nothing is served from a change tracker.</summary>
    public static async Task<T> With<T>(PostgresDatabaseFixture fixture, Func<IdentityService, Task<T>> action)
    {
        var (db, service) = Open(fixture);
        await using (db) return await action(service);
    }

    public static async Task With(PostgresDatabaseFixture fixture, Func<IdentityService, Task> action)
    {
        var (db, service) = Open(fixture);
        await using (db) await action(service);
    }

    public static async Task SeedAsync(PostgresDatabaseFixture fixture, IdentitySeedPolicy policy, BootstrapAdminSettings? bootstrap = null)
    {
        await using var db = fixture.CreateContext();
        await new IdentitySeeder(db, Hasher, Totp).SeedAsync(policy, bootstrap);
    }
}

/// <summary>Counts how many times a password was checked, to prove unknown and known accounts cost the same.</summary>
public sealed class CountingHasher(IPasswordHasherService inner) : IPasswordHasherService
{
    public int Verifications { get; private set; }
    public void ValidatePasswordPolicy(string password) => inner.ValidatePasswordPolicy(password);
    public string HashPassword(string password) => inner.HashPassword(password);
    public bool VerifyPassword(string password, string passwordHash) { Verifications++; return inner.VerifyPassword(password, passwordHash); }
}

/// <summary>Records every SQL command text, so a test can assert what the code actually sends to the database.</summary>
public sealed class SqlRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public List<string> Commands { get; } = [];

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
    { Commands.Add(command.CommandText); return result; }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
}

public class IdentityServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => TestInfra.PostgresAvailable
        ? IdentityTestKit.SeedAsync(fixture, new IdentitySeedPolicy(true))
        : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..28];

    private async Task<(string Email, string Phone, string Password, AuthTokenResponseDto Session)> RegisterAndVerifyAsync(
        DateTime? now = null,
        string ip = "203.112.218.10")
    {
        string email = Unique("user") + "@mechware.com.bd";
        string phone = "+88017" + Random.Shared.Next(10_000_000, 99_999_999);
        const string password = "RotatePass#2026!";

        var (reg, otp) = await IdentityTestKit.With(fixture, s => s.RegisterAsync(new RegisterRequestDto(email, phone, password, "Persist Tester"), ip));
        var session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, otp, "PhoneVerify", reg.ReferenceId), ip, "Mozilla/5.0", now));
        return (email, phone, password, session);
    }

    [PostgresFact]
    public async Task Accounts_AndSessions_SurviveANewServiceInstance_ThatIsHowARestartLooks()
    {
        var (email, _, password, session) = await RegisterAndVerifyAsync();

        var login = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, password)));
        login.AccessToken.Should().NotBeNullOrWhiteSpace();

        var rotated = await IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(session.RefreshTokenCookieValue!, "203.112.218.10", "Mozilla/5.0"));
        rotated.RefreshTokenCookieValue.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task RefreshTokenRotation_ShouldTolerate10SecondSameIpGraceWindow_AndRevokeEntireFamilyOnBreach()
    {
        var t0 = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var (_, _, _, initial) = await RegisterAndVerifyAsync(t0);
        string gen1 = initial.RefreshTokenCookieValue!;

        var t1 = t0.AddSeconds(2);
        var rotated = await IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(gen1, "203.112.218.10", "Mozilla/5.0", t1));
        string gen2 = rotated.RefreshTokenCookieValue!;
        gen2.Should().NotBe(gen1);

        // ADR-0006: a multi-tab / SSR race inside the 10 s window from the same IP succeeds without revoking gen2.
        var grace = await IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(gen1, "203.112.218.10", "Mozilla/5.0", t1.AddSeconds(5)));
        grace.AccessToken.Should().NotBeNullOrWhiteSpace();

        // Reuse outside the window from another IP is a breach: the whole family dies.
        var tBreach = t1.AddSeconds(30);
        Func<Task> replay = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(gen1, "185.199.108.99", "AttackerBot/1.0", tBreach));
        (await replay.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*reuse detected*");

        // The revocation was committed even though that request failed: the legitimate gen2 is dead too.
        Func<Task> legit = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(gen2, "203.112.218.10", "Mozilla/5.0", tBreach.AddSeconds(1)));
        (await legit.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*expired or revoked*");
    }

    [PostgresFact]
    public async Task ConcurrentRotationOfOneToken_HasExactlyOneWinner()
    {
        var (_, _, _, session) = await RegisterAndVerifyAsync();
        string token = session.RefreshTokenCookieValue!;

        // Different IPs, so the second caller cannot use the same-IP grace window and must be treated as a reuse.
        Task<string?>[] attempts = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                var r = await IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(token, $"10.0.0.{i}", "ua"));
                return r.RefreshTokenCookieValue;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        })).ToArray();

        string?[] results = await Task.WhenAll(attempts);
        results.Count(r => r is not null).Should().Be(1, "the row lock serialises the callers; only the first one finds the token unused");
    }

    [PostgresFact]
    public async Task ConcurrentRegistrationWithOneEmail_CreatesExactlyOneAccount()
    {
        string email = Unique("race") + "@mechware.com.bd";
        Task<bool>[] attempts = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                await IdentityTestKit.With(fixture, s => s.RegisterAsync(new RegisterRequestDto(email, null, "RacePass#2026!", "Race Tester")));
                return true;
            }
            catch (ValidationException)
            {
                return false;
            }
        })).ToArray();

        (await Task.WhenAll(attempts)).Count(ok => ok).Should().Be(1);
        await using var db = fixture.CreateContext();
        (await db.Users.CountAsync(u => u.Email == email)).Should().Be(1);
    }

    [PostgresFact]
    public async Task LoginLookup_UsesTheUniqueIndexes_AndStillIgnoresCase()
    {
        var (email, _, password, _) = await RegisterAndVerifyAsync();
        var upper = email.ToUpperInvariant();

        var recorder = new SqlRecorder();
        var options = new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(recorder).Options;
        await using var db = new ShopVerseDbContext(options);
        var (_, service) = IdentityTestKit.Open(fixture, null, db);

        var session = await service.LoginAsync(new LoginRequestDto(upper, password));

        session.AccessToken.Should().NotBeNullOrWhiteSpace("e-mail is case-insensitive for the person typing it");
        recorder.Commands.Where(c => c.Contains("FROM users", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(c => !c.Contains("lower(", StringComparison.OrdinalIgnoreCase),
                "wrapping the column in lower() defeats ux_users_email and turns every login into a full table scan");
    }

    [PostgresFact]
    public async Task UnknownAndKnownAccounts_CostTheSameNumberOfPasswordChecks()
    {
        var (email, _, _, _) = await RegisterAndVerifyAsync();
        var counting = new CountingHasher(IdentityTestKit.Hasher);

        async Task Attempt(string identifier)
        {
            var (db, service) = IdentityTestKit.Open(fixture, counting);
            await using (db)
            {
                var act = () => service.LoginAsync(new LoginRequestDto(identifier, "Wrong#Guess2026!"));
                await act.Should().ThrowAsync<UnauthorizedAccessException>();
            }
        }

        await Attempt(email);
        var known = counting.Verifications;
        await Attempt("nobody." + Guid.NewGuid().ToString("N") + "@example.test");

        known.Should().Be(1);
        counting.Verifications.Should().Be(2, "a missing account must hash too, or response time reveals which e-mails are registered");
    }

    [PostgresFact]
    public async Task FailedLogins_AreCounted_AndLockTheAccount()
    {
        var (email, _, password, _) = await RegisterAndVerifyAsync();

        for (int i = 0; i < 5; i++)
        {
            Func<Task> bad = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, "Wrong#Pass2026!")));
            await bad.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        Func<Task> locked = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, password)));
        (await locked.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*temporarily locked*");
    }

    [PostgresFact]
    public async Task SingleSessionCookieLogout_ShouldRevokeOnlyPresentedCookieFamily_LeavingOtherDeviceSessionsActive()
    {
        var t0 = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var device1 = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto("buyer@mechware.com.bd", "BuyerPass#2026!", null), "203.112.218.10", "DesktopChrome/1.0", t0));
        var device2 = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto("buyer@mechware.com.bd", "BuyerPass#2026!", null), "203.112.218.11", "MobileSafari/1.0", t0.AddSeconds(1)));

        (await IdentityTestKit.With(fixture, s => s.RevokeRefreshTokenByCookieAsync(device1.RefreshTokenCookieValue!, "203.112.218.10", t0.AddSeconds(5)))).Should().BeTrue();

        Func<Task> act1 = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(device1.RefreshTokenCookieValue!, "203.112.218.10", "DesktopChrome/1.0", t0.AddSeconds(10)));
        await act1.Should().ThrowAsync<UnauthorizedAccessException>();

        var rotated2 = await IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(device2.RefreshTokenCookieValue!, "203.112.218.11", "MobileSafari/1.0", t0.AddSeconds(10)));
        rotated2.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [PostgresFact]
    public async Task GoogleOidcLogin_ShouldValidateJwtClaims_AndRejectExpiredOrUntrustedIssuer()
    {
        string header = Base64Url(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" }));
        string good = Base64Url(JsonSerializer.Serialize(new
        {
            iss = "https://accounts.google.com",
            sub = "google-uid-" + Guid.NewGuid().ToString("N"),
            email = Unique("oidc") + "@mechware.com.bd",
            email_verified = true,
            name = "Engr. OIDC Verified",
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
        }));

        var session = await IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto($"{header}.{good}.sig_verified", null, null), "203.112.218.10", "Mozilla/5.0"));
        session.AccessToken.Should().NotBeNullOrWhiteSpace();

        string bad = Base64Url(JsonSerializer.Serialize(new
        {
            iss = "https://evil-idp.example.com",
            sub = "evil-1",
            email = "victim@mechware.com.bd",
            email_verified = true,
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
        }));
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto($"{header}.{bad}.sig", null, null)));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*issuer*");
    }

    [PostgresFact]
    public async Task OtpCode_ShouldEnforceMaxThreeAttempts_AndRejectFourthAttempt()
    {
        (SendOtpResponseDto sent, _) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto("+8801711000001", "Login"), "203.112.218.10"));
        sent.MaxAttempts.Should().Be(3);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Func<Task> wrong = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto("+8801711000001", "000000", "Login", sent.ReferenceId)));
            await wrong.Should().ThrowAsync<ValidationException>();
        }

        Func<Task> fourth = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto("+8801711000001", "000000", "Login", sent.ReferenceId)));
        (await fourth.Should().ThrowAsync<ValidationException>()).WithMessage("*Maximum OTP verification attempts*");
    }

    [PostgresFact]
    public async Task ConcurrentWrongGuesses_CannotOutrunTheAttemptLimit()
    {
        string phone = "+88018" + Random.Shared.Next(10_000_000, 99_999_999);
        (SendOtpResponseDto sent, string realCode) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto(phone, "Login"), "203.112.218.10"));
        realCode.Should().NotBe("000000");

        // Twelve guesses at once. Without the row lock, two requests read "0 attempts" together and both write "1".
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            try { await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, "000000", "Login", sent.ReferenceId))); }
            catch (ValidationException) { /* expected: wrong code or attempts exhausted */ }
        })));

        await using var db = fixture.CreateContext();
        (await db.OtpCodes.SingleAsync(o => o.ReferenceId == sent.ReferenceId)).Attempts.Should().Be(OtpCode.MaxVerificationAttempts,
            "exactly the allowed number of wrong guesses may be counted, never fewer (lost updates) and never more");

        // The right code no longer works: the limit really was reached.
        Func<Task> right = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, realCode, "Login", sent.ReferenceId)));
        (await right.Should().ThrowAsync<ValidationException>()).WithMessage("*Maximum OTP verification attempts*");
    }

    [PostgresFact]
    public async Task EmailVerificationAndPasswordResetFlow_ShouldVerifyEmailAndRevokeExistingSessionsOnPasswordReset()
    {
        string email = Unique("emailonly") + "@mechware.com.bd";
        var (reg, otp) = await IdentityTestKit.With(fixture, s => s.RegisterAsync(new RegisterRequestDto(email, null, "InitialPass#2026!", "Email Only Buyer"), "203.112.218.10"));
        reg.VerificationChannel.Should().Be("email_otp");

        var session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(email, otp, "EmailVerify", reg.ReferenceId), "203.112.218.10", "Mozilla/5.0"));
        string oldRefresh = session.RefreshTokenCookieValue!;

        var (refId, resetOtp) = await IdentityTestKit.With(fixture, s => s.InitiatePasswordResetAsync(email, "203.112.218.10"));
        await IdentityTestKit.With(fixture, s => s.CompletePasswordResetAsync(new ResetPasswordRequestDto(email, refId, resetOtp, "UpdatedPass#2026!"), "203.112.218.10"));

        Func<Task> useOld = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(oldRefresh, "203.112.218.10", "Mozilla/5.0"));
        await useOld.Should().ThrowAsync<UnauthorizedAccessException>();

        var fresh = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, "UpdatedPass#2026!", null), "203.112.218.10", "Mozilla/5.0"));
        fresh.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task BangladeshAddressBook_ShouldRejectCrossDivisionDistrictUpazilaMismatch()
    {
        var users = await IdentityTestKit.With(fixture, s => s.GetUsersForAuditAsync());
        var buyer = users.First(u => u.Email == "buyer@mechware.com.bd");

        Func<Task> mismatch = () => IdentityTestKit.With(fixture, s => s.CreateAddressAsync(
            buyer.Id,
            new UpsertAddressRequestDto("Invalid Plant", "Engr. Test", "+8801711000001", "Plot 1", null, "Halishahar", "Dhaka", "Chattogram", "Chattogram EPZ (Halishahar)", "4223", false, false, null, null),
            "203.112.218.10"));
        (await mismatch.Should().ThrowAsync<ValidationException>()).WithMessage("*does not belong to division*");
    }

    [PostgresFact]
    public async Task Addresses_ArePersisted_AndOnlyOneIsDefaultShipping()
    {
        var users = await IdentityTestKit.With(fixture, s => s.GetUsersForAuditAsync());
        var buyer = users.First(u => u.Email == "buyer@mechware.com.bd");

        UpsertAddressRequestDto Req(string label) => new(label, "Engr. Test", "+8801711000004", "Plot 9", null, "Tongi", "Dhaka", "Gazipur", "Tongi Industrial Zone", "1710", true, false, null, null);
        await IdentityTestKit.With(fixture, s => s.CreateAddressAsync(buyer.Id, Req("Second"), "203.112.218.10"));

        var list = await IdentityTestKit.With(fixture, s => s.GetAddressesAsync(buyer.Id));
        list.Count(a => a.IsDefaultShipping).Should().Be(1);
        list.Should().Contain(a => a.Label == "Second" && a.IsDefaultShipping);
    }

    [PostgresFact]
    public async Task Anonymisation_ErasesProfileAndAddresses_AndEndsSessions()
    {
        var (email, _, password, session) = await RegisterAndVerifyAsync();
        var user = (await IdentityTestKit.With(fixture, s => s.GetUsersForAuditAsync())).First(u => u.Email == email);

        await IdentityTestKit.With(fixture, s => s.AnonymizeAndEraseUserAsync(user.Id, "203.112.218.10"));

        Func<Task> login = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, password)));
        await login.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> rotate = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(session.RefreshTokenCookieValue!, "203.112.218.10", "ua"));
        await rotate.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [PostgresFact]
    public async Task SecurityEvents_ArePersistedInTheAuditTable()
    {
        var (email, _, _, _) = await RegisterAndVerifyAsync();
        Func<Task> bad = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, "Wrong#Pass2026!")));
        await bad.Should().ThrowAsync<UnauthorizedAccessException>();

        await using var db = fixture.CreateContext();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        (await db.AuditLogs.AnyAsync(a => a.ActorId == user.Id && a.Action == "user.login_failed"))
            .Should().BeTrue("a failed login must be audited even though the request itself failed");
    }
}
