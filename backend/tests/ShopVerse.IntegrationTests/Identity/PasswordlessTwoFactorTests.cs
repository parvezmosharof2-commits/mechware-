using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Identity;

/// <summary>
/// A one-time code or a Google sign-in proves who holds a phone or a mailbox, not the password and the authenticator. An account
/// that asked for two factors, or whose role demands them, must not get a session from either of those alone.
/// </summary>
public class PasswordlessTwoFactorTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string Ip = "203.112.218.10";

    public Task InitializeAsync() => TestInfra.PostgresAvailable
        ? IdentityTestKit.SeedAsync(fixture, new IdentitySeedPolicy(true))
        : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..28];

    private async Task<(string Email, string Phone, string Password, Guid UserId)> RegisterAsync()
    {
        string email = Unique("tfa") + "@mechware.com.bd";
        string phone = "+88019" + Random.Shared.Next(10_000_000, 99_999_999);
        const string password = "TwoFactor#2026!";
        var (reg, otp) = await IdentityTestKit.With(fixture, s => s.RegisterAsync(new RegisterRequestDto(email, phone, password, "Two Factor Tester"), Ip));
        var session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, otp, "PhoneVerify", reg.ReferenceId), Ip, "Mozilla/5.0"));
        return (email, phone, password, session.User.Id);
    }

    /// <summary>Registers (the phone is verified on sign-up) and also proves the e-mail, as Google linking requires.</summary>
    private async Task<(string Email, string Phone, string Password, Guid UserId)> RegisterWithVerifiedEmailAsync()
    {
        var account = await RegisterAsync();
        var (sent, code) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto(account.Email, "EmailVerify"), Ip));
        await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(account.Email, code, "EmailVerify", sent.ReferenceId), Ip, "Mozilla/5.0"));
        return account;
    }

    private async Task SetStatusAsync(Guid userId, UserStatus status)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET status = {status.ToString()} WHERE id = {userId}");
    }

    private async Task<(string Email, string Phone, string Password, Guid UserId)> RegisterWithTwoFactorAsync()
    {
        var account = await RegisterAsync();
        var setup = await IdentityTestKit.With(fixture, s => s.SetupTwoFactorAsync(account.UserId, Ip));
        await IdentityTestKit.With(fixture, s => s.VerifyAndEnableTwoFactorAsync(
            account.UserId, IdentityTestKit.Totp.ComputeTotpCode(setup.Secret, DateTimeOffset.UtcNow), Ip));
        return account;
    }

    /// <summary>A finance user who has not turned 2FA on: the role alone must demand it.</summary>
    private async Task<(string Email, string Phone, string Password, Guid UserId)> RegisterFinanceWithoutTwoFactorAsync()
    {
        var account = await RegisterAsync();
        await using var db = fixture.CreateContext();
        var user = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == account.UserId);
        user.TwoFactorEnabled.Should().BeFalse();
        user.AssignRole(Guid.NewGuid(), Permissions.Roles.Finance);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<int> SessionCountAsync(Guid userId)
    {
        await using var db = fixture.CreateContext();
        return await db.RefreshTokens.CountAsync(t => t.UserId == userId);
    }

    private static string GoogleToken(string subject, string email, bool? emailVerified = true)
    {
        var claims = IdentityTestKit.GoogleClaims(subject, email);
        if (emailVerified is null) claims.Remove("email_verified"); else claims["email_verified"] = emailVerified;
        return IdentityTestKit.GoogleToken(claims);
    }

    [PostgresFact]
    public async Task AOneTimeCode_DoesNotSignInAnAccountWithTwoFactor_ButStillVerifiesTheContact()
    {
        var account = await RegisterWithTwoFactorAsync();
        int before = await SessionCountAsync(account.UserId);

        var (sent, code) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto(account.Email, "EmailVerify"), Ip));
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(account.Email, code, "EmailVerify", sent.ReferenceId), Ip, "Mozilla/5.0"));

        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("PASSWORD_AND_2FA_REQUIRED");
        (await SessionCountAsync(account.UserId)).Should().Be(before, "no session may be issued without the second factor");

        await using var db = fixture.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == account.UserId);
        user.EmailVerifiedAtUtc.Should().NotBeNull("the code was right, so the mailbox is proven even though no session is given");
        (await db.OtpCodes.SingleAsync(o => o.ReferenceId == sent.ReferenceId)).IsConsumed.Should().BeTrue("a used code cannot be tried again");
    }

    [PostgresFact]
    public async Task AOneTimeCode_DoesNotSignInARoleThatMustUseTwoFactor()
    {
        var account = await RegisterFinanceWithoutTwoFactorAsync();
        int before = await SessionCountAsync(account.UserId);

        var (sent, code) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto(account.Phone, "Login"), Ip));
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(account.Phone, code, "Login", sent.ReferenceId), Ip, "Mozilla/5.0"));

        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("PASSWORD_AND_2FA_REQUIRED");
        (await SessionCountAsync(account.UserId)).Should().Be(before);
    }

    [PostgresFact]
    public async Task APasswordResetCode_CannotBeUsedToSignIn_AndStillResetsThePassword()
    {
        var account = await RegisterAsync();
        int before = await SessionCountAsync(account.UserId);

        var (refId, resetCode) = await IdentityTestKit.With(fixture, s => s.InitiatePasswordResetAsync(account.Email, Ip));
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(account.Email, resetCode, "EmailVerify", refId), Ip, "Mozilla/5.0"));
        await act.Should().ThrowAsync<ValidationException>();
        (await SessionCountAsync(account.UserId)).Should().Be(before);

        // The refused attempt neither spent the code nor counted against it: the owner can still reset.
        await IdentityTestKit.With(fixture, s => s.CompletePasswordResetAsync(new ResetPasswordRequestDto(account.Email, refId, resetCode, "Changed#Pass2026!"), Ip));
        var login = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(account.Email, "Changed#Pass2026!")));
        login.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task AnOrdinaryBuyer_StillSignsInWithAOneTimeCode()
    {
        var account = await RegisterAsync();
        var (sent, code) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto(account.Phone, "Login"), Ip));
        var session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(account.Phone, code, "Login", sent.ReferenceId), Ip, "Mozilla/5.0"));
        session.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task Google_DoesNotSignInOrLinkAnAccountWithTwoFactor()
    {
        var account = await RegisterWithTwoFactorAsync();
        int before = await SessionCountAsync(account.UserId);
        string subject = "google-uid-" + Guid.NewGuid().ToString("N");

        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto(GoogleToken(subject, account.Email)), Ip, "Mozilla/5.0"));
        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("PASSWORD_AND_2FA_REQUIRED");

        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == account.UserId)).GoogleSubjectId.Should().BeNull("the Google identity must not be attached to the account");
        (await SessionCountAsync(account.UserId)).Should().Be(before);
    }

    [PostgresFact]
    public async Task Google_DoesNotSignInARoleThatMustUseTwoFactor()
    {
        var account = await RegisterFinanceWithoutTwoFactorAsync();
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(
            new GoogleLoginRequestDto(GoogleToken("google-uid-" + Guid.NewGuid().ToString("N"), account.Email)), Ip, "Mozilla/5.0"));
        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("PASSWORD_AND_2FA_REQUIRED");

        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == account.UserId)).GoogleSubjectId.Should().BeNull();
    }

    [PostgresFact]
    public async Task Google_RespectsTheLockout()
    {
        var account = await RegisterAsync();
        for (int i = 0; i < 5; i++)
        {
            Func<Task> bad = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(account.Email, "Wrong#Pass2026!")));
            await bad.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(
            new GoogleLoginRequestDto(GoogleToken("google-uid-" + Guid.NewGuid().ToString("N"), account.Email)), Ip, "Mozilla/5.0"));
        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*temporarily locked*");
    }

    [PostgresFact]
    public async Task Google_DoesNotLinkByEmail_WhenGoogleDoesNotSayTheEmailIsVerified()
    {
        var account = await RegisterWithVerifiedEmailAsync();
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(
            new GoogleLoginRequestDto(GoogleToken("google-uid-" + Guid.NewGuid().ToString("N"), account.Email, emailVerified: null)), Ip, "Mozilla/5.0"));
        await act.Should().ThrowAsync<ValidationException>();

        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == account.UserId)).GoogleSubjectId.Should().BeNull();
    }

    [PostgresFact]
    public async Task Google_DoesNotMoveAnAccountToASecondGoogleIdentity()
    {
        var account = await RegisterWithVerifiedEmailAsync();
        string first = "google-uid-" + Guid.NewGuid().ToString("N");
        (await IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto(GoogleToken(first, account.Email)), Ip, "Mozilla/5.0")))
            .AccessToken.Should().NotBeNullOrWhiteSpace();

        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(
            new GoogleLoginRequestDto(GoogleToken("google-uid-" + Guid.NewGuid().ToString("N"), account.Email)), Ip, "Mozilla/5.0"));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("GOOGLE_ACCOUNT_MISMATCH");

        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == account.UserId)).GoogleSubjectId.Should().Be(first);
    }

    [PostgresFact]
    public async Task Google_DoesNotLinkAnAccountThatNeverVerifiedItsEmail()
    {
        // Someone registered this address with their own password and phone and never proved the mailbox. The real owner
        // signing in with Google must not inherit an account that stranger can still enter.
        var squatted = await RegisterAsync();
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(
            new GoogleLoginRequestDto(GoogleToken("google-uid-" + Guid.NewGuid().ToString("N"), squatted.Email)), Ip, "Mozilla/5.0"));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("EMAIL_NOT_VERIFIED");

        await using var db = fixture.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == squatted.UserId);
        user.GoogleSubjectId.Should().BeNull();
        user.EmailVerifiedAtUtc.Should().BeNull("a refused link must not mark the e-mail as proven");
    }

    [PostgresFact]
    public async Task Google_LinksAndSignsIn_AnAccountWithAVerifiedEmail()
    {
        var account = await RegisterWithVerifiedEmailAsync();
        string subject = "google-uid-" + Guid.NewGuid().ToString("N");
        var session = await IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto(GoogleToken(subject, account.Email)), Ip, "Mozilla/5.0"));
        session.User.Id.Should().Be(account.UserId);

        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == account.UserId)).GoogleSubjectId.Should().Be(subject);
    }

    [PostgresTheory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Banned)]
    public async Task ADisabledAccount_GetsNoSessionByAnyRoute(UserStatus status)
    {
        var account = await RegisterWithVerifiedEmailAsync();
        string subject = "google-uid-" + Guid.NewGuid().ToString("N");
        var linked = await IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto(GoogleToken(subject, account.Email)), Ip, "Mozilla/5.0"));
        await SetStatusAsync(account.UserId, status);
        int before = await SessionCountAsync(account.UserId);

        Func<Task> password = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(account.Email, account.Password)));
        (await password.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("ACCOUNT_DISABLED");

        var (sent, code) = await IdentityTestKit.With(fixture, s => s.SendOtpAsync(new SendOtpRequestDto(account.Phone, "Login"), Ip));
        Func<Task> otp = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(account.Phone, code, "Login", sent.ReferenceId), Ip, "Mozilla/5.0"));
        (await otp.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("ACCOUNT_DISABLED");

        Func<Task> google = () => IdentityTestKit.With(fixture, s => s.GoogleLoginAsync(new GoogleLoginRequestDto(GoogleToken(subject, account.Email)), Ip, "Mozilla/5.0"));
        (await google.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("ACCOUNT_DISABLED");

        Func<Task> refresh = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(linked.RefreshTokenCookieValue!, Ip, "Mozilla/5.0"));
        await refresh.Should().ThrowAsync<UnauthorizedAccessException>();

        (await SessionCountAsync(account.UserId)).Should().Be(before);
    }
}
