using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Identity;

/// <summary>
/// M1: every staff role signs in with a password and an authenticator. Staff who have none yet get, for a correct password,
/// only a short-lived single-use token that can enrol one; the session starts with the first correct authenticator code.
/// </summary>
public class StaffTwoFactorEnrollmentTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string Ip = "203.112.218.10";

    public Task InitializeAsync() => TestInfra.PostgresAvailable
        ? IdentityTestKit.SeedAsync(fixture, new IdentitySeedPolicy(true))
        : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(string Email, string Password, Guid UserId)> StaffAsync(string role)
    {
        string email = $"staff.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        string phone = "+88016" + Random.Shared.Next(10_000_000, 99_999_999);
        const string password = "StaffPass#2026!";
        var (reg, otp) = await IdentityTestKit.With(fixture, s => s.RegisterAsync(new RegisterRequestDto(email, phone, password, "Staff Member"), Ip));
        var session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, otp, "PhoneVerify", reg.ReferenceId), Ip, "Mozilla/5.0"));

        await using var db = fixture.CreateContext();
        var user = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == session.User.Id);
        user.AssignRole(Guid.NewGuid(), role);
        await db.SaveChangesAsync();
        return (email, password, user.Id);
    }

    private Task<AuthTokenResponseDto> Login(string email, string password, string? totp = null, string? setupCode = null) =>
        IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, password, totp, setupCode), Ip, "Mozilla/5.0"));

    private async Task<Guid> AdminIdAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.Users.Where(u => u.Email == PublishedDemoCredentials.AdminEmail).Select(u => u.Id).SingleAsync();
    }

    private async Task<string> IssueSetupCodeAsync(Guid userId)
    {
        Guid admin = await AdminIdAsync();
        return (await IdentityTestKit.With(fixture, s => s.IssueTwoFactorSetupCodeAsync(admin, userId, Ip))).SetupCode;
    }

    /// <summary>Password + setup code: the secret, recovery codes and the 10-minute token for the first code.</summary>
    private async Task<AuthTokenResponseDto> RedeemAsync((string Email, string Password, Guid UserId) staff) =>
        await Login(staff.Email, staff.Password, setupCode: await IssueSetupCodeAsync(staff.UserId));

    private async Task<int> FailedLoginsAsync(Guid userId)
    {
        await using var db = fixture.CreateContext();
        return (await db.Users.SingleAsync(u => u.Id == userId)).FailedLoginAttempts;
    }

    private async Task<int> SessionCountAsync(Guid userId)
    {
        await using var db = fixture.CreateContext();
        return await db.RefreshTokens.CountAsync(t => t.UserId == userId);
    }

    private static string Code(string secret, DateTime? at = null) =>
        IdentityTestKit.Totp.ComputeTotpCode(secret, new DateTimeOffset(at ?? DateTime.UtcNow, TimeSpan.Zero));

    [PostgresTheory]
    [InlineData(Permissions.Roles.Support)]
    [InlineData(Permissions.Roles.Moderator)]
    [InlineData(Permissions.Roles.Finance)]
    [InlineData(Permissions.Roles.Admin)]
    public async Task EveryStaffRole_WithoutAnAuthenticator_GetsNothingFromThePasswordAlone(string role)
    {
        var staff = await StaffAsync(role);
        int before = await SessionCountAsync(staff.UserId);

        var result = await Login(staff.Email, staff.Password);

        result.RequiresTwoFactorEnrollment.Should().BeTrue();
        result.EnrollmentToken.Should().BeNull("a leaked password alone must not be able to enrol an attacker's phone");
        result.TwoFactorSetup.Should().BeNull();
        result.AccessToken.Should().BeEmpty();
        (await SessionCountAsync(staff.UserId)).Should().Be(before);
    }

    [PostgresFact]
    public async Task PasswordAndSetupCode_ThenTheFirstCode_TurnTwoFactorOn_AndStartTheSession()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        int before = await SessionCountAsync(staff.UserId);
        var redeemed = await RedeemAsync(staff);

        redeemed.AccessToken.Should().BeEmpty();
        (await SessionCountAsync(staff.UserId)).Should().Be(before, "no session before the authenticator is proven");
        redeemed.TwoFactorSetup!.RecoveryCodes.Should().HaveCount(10);

        var session = await IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup.Secret), Ip, "Mozilla/5.0"));
        session.AccessToken.Should().NotBeNullOrWhiteSpace();
        session.User.TwoFactorEnabled.Should().BeTrue();

        Func<Task> again = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup.Secret, DateTime.UtcNow.AddSeconds(30)), Ip, "Mozilla/5.0"));
        await again.Should().ThrowAsync<UnauthorizedAccessException>("the token works once");

        (await Login(staff.Email, staff.Password)).RequiresTwoFactor.Should().BeTrue();
    }

    [PostgresFact]
    public async Task ASetupCode_CanRestartAnUnfinishedEnrolment_ButIsSpentOnceEnrolmentSucceeds()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        string code = await IssueSetupCodeAsync(staff.UserId);
        var first = await Login(staff.Email, staff.Password, setupCode: code);

        // Ran out of time: the same code starts again, and the earlier token stops working.
        var second = await Login(staff.Email, staff.Password, setupCode: code);
        Func<Task> stale = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            first.EnrollmentToken!, Code(second.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));
        await stale.Should().ThrowAsync<UnauthorizedAccessException>();

        await IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            second.EnrollmentToken!, Code(second.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));

        // Enrolled: the code is spent (and the password alone now asks for the authenticator).
        var after = await Login(staff.Email, staff.Password, setupCode: code);
        after.RequiresTwoFactor.Should().BeTrue();
        after.EnrollmentToken.Should().BeNull();
        await using var db = fixture.CreateContext();
        (await db.OtpCodes.Where(o => o.Recipient == staff.UserId.ToString("N")).AllAsync(o => o.IsConsumed)).Should().BeTrue();
    }

    [PostgresFact]
    public async Task ACorrectPasswordAndSetupCode_ClearEarlierFailures()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        string code = await IssueSetupCodeAsync(staff.UserId);
        Func<Task> typo = () => Login(staff.Email, staff.Password, setupCode: "AAAAA-AAAAA");
        await typo.Should().ThrowAsync<UnauthorizedAccessException>();
        (await FailedLoginsAsync(staff.UserId)).Should().Be(1);

        await Login(staff.Email, staff.Password, setupCode: code);
        (await FailedLoginsAsync(staff.UserId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task StaffCannotSelfEnrolThroughTheSessionEndpoints()
    {
        // e.g. a session from before the role was given: /2fa/setup and /2fa/verify must not bypass the setup code.
        var staff = await StaffAsync(Permissions.Roles.Support);
        Func<Task> setup = () => IdentityTestKit.With(fixture, s => s.SetupTwoFactorAsync(staff.UserId, Ip));
        (await setup.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("STAFF_ENROLL_WITH_SETUP_CODE");

        Func<Task> verify = () => IdentityTestKit.With(fixture, s => s.VerifyAndEnableTwoFactorAsync(staff.UserId, "123456", Ip));
        (await verify.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("STAFF_ENROLL_WITH_SETUP_CODE");
    }

    [PostgresFact]
    public async Task ACodeIsNotIssuedForASuspendedAccount()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET status = {UserStatus.Suspended.ToString()} WHERE id = {staff.UserId}");
        Guid admin = await AdminIdAsync();
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.IssueTwoFactorSetupCodeAsync(admin, staff.UserId, Ip));
        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("ACCOUNT_DISABLED");
    }

    [PostgresFact]
    public async Task EnrollingEndsEverySessionBegunWithoutTheAuthenticator()
    {
        var person = await StaffAsync(Permissions.Roles.Buyer);
        var old = await Login(person.Email, person.Password);
        await using (var db = fixture.CreateContext())
        {
            var user = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == person.UserId);
            user.AssignRole(Guid.NewGuid(), Permissions.Roles.Support);
            await db.SaveChangesAsync();
        }

        var redeemed = await RedeemAsync(person);
        var fresh = await IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));

        Func<Task> oldRefresh = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(old.RefreshTokenCookieValue!, Ip, "Mozilla/5.0"));
        await oldRefresh.Should().ThrowAsync<UnauthorizedAccessException>("it never passed a second factor");
        (await IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(fresh.RefreshTokenCookieValue!, Ip, "Mozilla/5.0")))
            .AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task ASetupCode_IsAcceptedWithoutTheDashOrInLowerCase()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        string code = await IssueSetupCodeAsync(staff.UserId);
        code.Should().MatchRegex("^[A-Z2-9]{5}-[A-Z2-9]{5}$");
        (await Login(staff.Email, staff.Password, setupCode: code.Replace("-", " ").ToLowerInvariant())).EnrollmentToken.Should().NotBeNull();
    }

    [PostgresFact]
    public async Task WrongSetupCodes_CountTowardsTheLockout_AndEndTheCode()
    {
        var staff = await StaffAsync(Permissions.Roles.Moderator);
        string code = await IssueSetupCodeAsync(staff.UserId);

        for (int i = 0; i < 3; i++)
        {
            Func<Task> wrong = () => Login(staff.Email, staff.Password, setupCode: "AAAAA-AAAAA");
            await wrong.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        (await FailedLoginsAsync(staff.UserId)).Should().Be(3);

        Func<Task> right = () => Login(staff.Email, staff.Password, setupCode: code);
        await right.Should().ThrowAsync<UnauthorizedAccessException>("three wrong tries spend the code");
    }

    [PostgresFact]
    public async Task ANewSetupCode_RetiresTheOldOne_AndAnyOpenToken()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        string first = await IssueSetupCodeAsync(staff.UserId);
        string second = await IssueSetupCodeAsync(staff.UserId);

        Func<Task> old = () => Login(staff.Email, staff.Password, setupCode: first);
        await old.Should().ThrowAsync<UnauthorizedAccessException>();

        var redeemed = await Login(staff.Email, staff.Password, setupCode: second);
        await IssueSetupCodeAsync(staff.UserId);
        Func<Task> stale = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));
        await stale.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [PostgresFact]
    public async Task WrongAuthenticatorCodes_CountTowardsTheLockout_AndThreeEndTheToken()
    {
        var staff = await StaffAsync(Permissions.Roles.Moderator);
        int before = await SessionCountAsync(staff.UserId);
        var redeemed = await RedeemAsync(staff);
        int failedBefore = await FailedLoginsAsync(staff.UserId);

        for (int i = 0; i < 3; i++)
        {
            Func<Task> wrong = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(redeemed.EnrollmentToken!, "000000", Ip, "Mozilla/5.0"));
            await wrong.Should().ThrowAsync<Exception>();
        }
        (await FailedLoginsAsync(staff.UserId)).Should().Be(failedBefore + 3);

        Func<Task> right = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));
        await right.Should().ThrowAsync<UnauthorizedAccessException>();
        (await SessionCountAsync(staff.UserId)).Should().Be(before);
        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == staff.UserId)).TwoFactorEnabled.Should().BeFalse();
    }

    [PostgresFact]
    public async Task AForgedOrExpiredToken_IsRefused()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        var redeemed = await RedeemAsync(staff);
        string[] parts = redeemed.EnrollmentToken!.Split('.');
        string code = Code(redeemed.TwoFactorSetup!.Secret);

        Func<Task> forged = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync($"{parts[0]}.{parts[1]}.forged", code, Ip, "Mozilla/5.0"));
        await forged.Should().ThrowAsync<UnauthorizedAccessException>();

        Func<Task> late = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup.Secret, DateTime.UtcNow.AddMinutes(11)), Ip, "Mozilla/5.0", DateTime.UtcNow.AddMinutes(11)));
        await late.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [PostgresFact]
    public async Task AnExpiredSetupCode_IsRefused()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        string code = await IssueSetupCodeAsync(staff.UserId);
        Func<Task> late = () => IdentityTestKit.With(fixture, s => s.LoginAsync(
            new LoginRequestDto(staff.Email, staff.Password, null, code), Ip, "Mozilla/5.0", DateTime.UtcNow.AddHours(73)));
        await late.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [PostgresFact]
    public async Task TheSetupCodeAndTokenAreNotOneTimeSignInCodes()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        string code = await IssueSetupCodeAsync(staff.UserId);
        await using (var db = fixture.CreateContext())
        {
            string reference = await db.OtpCodes.Where(o => o.Recipient == staff.UserId.ToString("N") && o.Type == OtpType.TwoFactorSetupCode)
                .Select(o => o.ReferenceId).SingleAsync();
            Func<Task> act = () => IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(
                new VerifyOtpRequestDto(staff.UserId.ToString("N"), code, "Login", reference), Ip, "Mozilla/5.0"));
            await act.Should().ThrowAsync<ValidationException>();
        }
    }

    [PostgresFact]
    public async Task OnlyAnotherAdministrator_CanIssueACode_AndOnlyForStaffWithoutAnAuthenticator()
    {
        Guid admin = await AdminIdAsync();
        Func<Task> self = () => IdentityTestKit.With(fixture, s => s.IssueTwoFactorSetupCodeAsync(admin, admin, Ip));
        (await self.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("SETUP_CODE_FOR_SELF");

        Func<Task> enrolled = () => IdentityTestKit.With(fixture, s => s.IssueTwoFactorSetupCodeAsync(Guid.NewGuid(), admin, Ip));
        (await enrolled.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("TWO_FACTOR_ENROLLMENT_NOT_NEEDED");

        var buyer = await StaffAsync(Permissions.Roles.Buyer);
        Func<Task> notStaff = () => IdentityTestKit.With(fixture, s => s.IssueTwoFactorSetupCodeAsync(admin, buyer.UserId, Ip));
        (await notStaff.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("TWO_FACTOR_ENROLLMENT_NOT_NEEDED");
    }

    [PostgresFact]
    public async Task ASuspendedStaffMember_CannotFinishEnrolling()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        var redeemed = await RedeemAsync(staff);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET status = {UserStatus.Suspended.ToString()} WHERE id = {staff.UserId}");

        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));
        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("ACCOUNT_DISABLED");
    }

    [PostgresFact]
    public async Task SessionsBegunWithoutAnAuthenticator_EndWhenTheRoleDemandsOne()
    {
        // A buyer signed in with a password, then was made support staff: the old session must not live on.
        var person = await StaffAsync(Permissions.Roles.Buyer);
        var session = await Login(person.Email, person.Password);
        await using (var db = fixture.CreateContext())
        {
            var user = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == person.UserId);
            user.AssignRole(Guid.NewGuid(), Permissions.Roles.Support);
            await db.SaveChangesAsync();
        }

        Func<Task> refresh = () => IdentityTestKit.With(fixture, s => s.RotateRefreshTokenAsync(session.RefreshTokenCookieValue!, Ip, "Mozilla/5.0"));
        (await refresh.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*two-factor*");

        await using var check = fixture.CreateContext();
        (await check.RefreshTokens.Where(t => t.UserId == person.UserId).AllAsync(t => t.IsRevoked)).Should().BeTrue("refused sessions are revoked, not left to revive later");
    }

    [PostgresFact]
    public async Task SetupCannotReplaceAnAuthenticatorThatIsAlreadyOn()
    {
        var staff = await StaffAsync(Permissions.Roles.Support);
        var redeemed = await RedeemAsync(staff);
        await IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(
            redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup!.Secret), Ip, "Mozilla/5.0"));

        // A stolen access token must not be able to swap the secret and recovery codes.
        Func<Task> act = () => IdentityTestKit.With(fixture, s => s.SetupTwoFactorAsync(staff.UserId, Ip));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("TWO_FACTOR_ALREADY_ENABLED");

        await using var db = fixture.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == staff.UserId)).TwoFactorSecret.Should().Be(redeemed.TwoFactorSetup.Secret);
    }

    [PostgresFact]
    public async Task TheConsoleList_ShowsStaffAwaitingAnAuthenticator_AndWhetherACodeIsOut()
    {
        var waiting = await StaffAsync(Permissions.Roles.Support);
        var enrolled = await StaffAsync(Permissions.Roles.Moderator);
        var suspended = await StaffAsync(Permissions.Roles.Finance);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET status = {UserStatus.Suspended.ToString()} WHERE id = {suspended.UserId}");
        var redeemed = await RedeemAsync(enrolled);
        await IdentityTestKit.With(fixture, s => s.CompleteTwoFactorEnrollmentAsync(redeemed.EnrollmentToken!, Code(redeemed.TwoFactorSetup!.Secret), Ip));

        var list = await IdentityTestKit.With(fixture, s => s.ListStaffAwaitingTwoFactorAsync());
        var row = list.Should().ContainSingle(r => r.UserId == waiting.UserId).Subject;
        row.Email.Should().Be(waiting.Email);
        row.Roles.Should().Contain(Permissions.Roles.Support);
        row.HasActiveSetupCode.Should().BeFalse();
        list.Should().NotContain(r => r.UserId == enrolled.UserId, "they already use an authenticator");
        list.Should().NotContain(r => r.UserId == suspended.UserId, "no code can be issued for them");
        list.Should().OnlyContain(r => r.Roles.Any(Permissions.Roles.RequiresMandatoryTwoFactor), "buyers and sellers need no code");

        // Issued and used to start enrolment: an enrolment token for the same person now exists next to the code.
        await RedeemAsync(waiting);
        async Task<bool> CodeOut() => (await IdentityTestKit.With(fixture, s => s.ListStaffAwaitingTwoFactorAsync()))
            .Single(r => r.UserId == waiting.UserId).HasActiveSetupCode;
        (await CodeOut()).Should().BeTrue();

        // An expired, spent or exhausted code is not "out": the admin should issue a new one.
        string recipient = waiting.UserId.ToString("N");
        string setupType = nameof(OtpType.TwoFactorSetupCode);
        foreach (var (spoil, restore) in new (FormattableString, FormattableString)[]
                 {
                     ($"UPDATE otp_codes SET expires_at = now() - interval '1 minute' WHERE recipient = {recipient} AND type = {setupType}",
                      $"UPDATE otp_codes SET expires_at = now() + interval '1 day' WHERE recipient = {recipient} AND type = {setupType}"),
                     ($"UPDATE otp_codes SET is_consumed = TRUE WHERE recipient = {recipient} AND type = {setupType}",
                      $"UPDATE otp_codes SET is_consumed = FALSE WHERE recipient = {recipient} AND type = {setupType}"),
                     ($"UPDATE otp_codes SET attempts = {OtpCode.MaxVerificationAttempts} WHERE recipient = {recipient} AND type = {setupType}",
                      $"UPDATE otp_codes SET attempts = 0 WHERE recipient = {recipient} AND type = {setupType}")
                 })
        {
            await using (var db = fixture.CreateContext()) await db.Database.ExecuteSqlAsync(spoil);
            (await CodeOut()).Should().BeFalse(spoil.Format);
            await using (var db = fixture.CreateContext()) await db.Database.ExecuteSqlAsync(restore);
            (await CodeOut()).Should().BeTrue();
        }
    }

    [PostgresFact]
    public async Task ABuyer_StillSignsInWithAPasswordAlone()
    {
        var login = await Login(PublishedDemoCredentials.BuyerEmail, "BuyerPass#2026!");
        login.AccessToken.Should().NotBeNullOrWhiteSpace();
        login.RequiresTwoFactorEnrollment.Should().BeFalse();
    }
}
