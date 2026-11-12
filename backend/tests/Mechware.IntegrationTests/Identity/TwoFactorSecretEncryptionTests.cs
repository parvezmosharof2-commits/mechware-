using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Security;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Identity;

/// <summary>
/// The authenticator secret and the recovery-code hashes are encrypted at rest, bound to their user (2026-11-13, finding
/// from H3a.2: a database dump or backup gave every user's TOTP secret, staff with refund and price rights included).
/// </summary>
public class TwoFactorSecretEncryptionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.112.218.10";
    private const string Password = "SecretPass#2026!";

    private sealed record Enrolled(Guid UserId, string Email, string Secret, IReadOnlyList<string> RecoveryCodes);

    /// <summary>A buyer who turned on an authenticator a minute ago (so a code for now is not a replay).</summary>
    private async Task<Enrolled> EnrolAsync()
    {
        string email = $"totp.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        string phone = "+88017" + Random.Shared.Next(10_000_000, 99_999_999);
        var (reg, otp) = await IdentityTestKit.With(fixture, s => s.RegisterAsync(new RegisterRequestDto(email, phone, Password, "Totp Tester"), Ip));
        var session = await IdentityTestKit.With(fixture, s => s.VerifyOtpAsync(new VerifyOtpRequestDto(phone, otp, "PhoneVerify", reg.ReferenceId), Ip, "Mozilla/5.0"));
        Guid userId = session.User.Id;

        var setup = await IdentityTestKit.With(fixture, s => s.SetupTwoFactorAsync(userId, Ip));
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
        await IdentityTestKit.With(fixture, s => s.VerifyAndEnableTwoFactorAsync(
            userId, IdentityTestKit.Totp.ComputeTotpCode(setup.Secret, earlier), Ip, earlier));
        return new Enrolled(userId, email, setup.Secret, setup.RecoveryCodes);
    }

    private async Task<(string? Secret, string? RecoveryCodes)> RawAsync(Guid userId)
    {
        await using var db = fixture.CreateContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT two_factor_secret, two_factor_recovery_codes_json FROM users WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = userId;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task SetRawAsync(Guid userId, string? secret, string? recoveryCodes)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE users SET two_factor_secret = {secret}, two_factor_recovery_codes_json = {recoveryCodes} WHERE id = {userId}");
    }

    private Task<AuthTokenResponseDto> LoginAsync(string email, string code, DateTime? at = null) =>
        IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, Password, code), Ip, "Mozilla/5.0", at));

    private static string CodeAt(string secret, DateTime at) => IdentityTestKit.Totp.ComputeTotpCode(secret, new DateTimeOffset(at, TimeSpan.Zero));

    [PostgresFact]
    public async Task Enrolment_StoresNeitherTheSecretNorTheRecoveryHashesInClear_AndSignInStillWorks()
    {
        var user = await EnrolAsync();

        var (secret, codes) = await RawAsync(user.UserId);
        secret.Should().StartWith(FieldEncryption.Current.CurrentPrefix).And.NotContain(user.Secret);
        codes.Should().StartWith(FieldEncryption.Current.CurrentPrefix);
        foreach (string code in user.RecoveryCodes)
        {
            codes.Should().NotContain(IdentityTestKit.Totp.HashRecoveryCode(code), "the 40-bit codes would be brute-forced from their hashes");
        }

        (await LoginAsync(user.Email, CodeAt(user.Secret, DateTime.UtcNow))).AccessToken.Should().NotBeNullOrWhiteSpace();

        // A recovery code works once, and the shortened list is written back encrypted.
        (await LoginAsync(user.Email, user.RecoveryCodes[0])).AccessToken.Should().NotBeNullOrWhiteSpace();
        var (_, after) = await RawAsync(user.UserId);
        after.Should().StartWith(FieldEncryption.Current.CurrentPrefix).And.NotBe(codes);
        JsonSerializer.Deserialize<List<string>>(TwoFactorSecrets.OpenRecoveryCodes(user.UserId, after)!).Should().HaveCount(9);
        Func<Task> again = () => LoginAsync(user.Email, user.RecoveryCodes[0]);
        await again.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [PostgresFact]
    public async Task AnotherUsersCiphertext_CopiedIntoARow_DoesNotPassTheSecondFactor()
    {
        var attacker = await EnrolAsync();
        var victim = await EnrolAsync();
        var (attackerSecret, attackerCodes) = await RawAsync(attacker.UserId);

        // Someone who can write the database but has no key copies their own authenticator into the victim's row.
        await SetRawAsync(victim.UserId, attackerSecret, attackerCodes);

        Func<Task> totp = () => LoginAsync(victim.Email, CodeAt(attacker.Secret, DateTime.UtcNow));
        await totp.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> recovery = () => LoginAsync(victim.Email, attacker.RecoveryCodes[1]);
        await recovery.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [PostgresFact]
    public async Task ASecretWrittenInClear_IsRefused_UntilTheConversionEncryptsIt_WhichIsIdempotent()
    {
        var user = await EnrolAsync();
        var hashes = user.RecoveryCodes.Select(IdentityTestKit.Totp.HashRecoveryCode).ToList();

        // How every row looked before this release (and what someone with write access could plant).
        await SetRawAsync(user.UserId, user.Secret, JsonSerializer.Serialize(hashes));
        var now = DateTime.UtcNow;
        Func<Task> refused = () => LoginAsync(user.Email, CodeAt(user.Secret, now), now);
        await refused.Should().ThrowAsync<UnauthorizedAccessException>("a value in clear is never trusted");

        await using (var db = fixture.CreateContext())
        {
            var first = await TwoFactorSecrets.EncryptStoredAsync(db, CancellationToken.None, batchSize: 2);
            first.Rewritten.Should().BeGreaterThanOrEqualTo(1);
            first.Unreadable.Should().Be(0);
        }

        var (secret, codes) = await RawAsync(user.UserId);
        secret.Should().StartWith(FieldEncryption.Current.CurrentPrefix).And.NotContain(user.Secret);
        TwoFactorSecrets.OpenSecret(user.UserId, secret).Should().Be(user.Secret);
        TwoFactorSecrets.OpenRecoveryCodes(user.UserId, codes).Should().Be(JsonSerializer.Serialize(hashes));

        await using (var db = fixture.CreateContext())
        {
            (await TwoFactorSecrets.EncryptStoredAsync(db, CancellationToken.None)).Should().Be(new TwoFactorSecrets.ConversionResult(0, 0));
        }
        (await RawAsync(user.UserId)).Should().Be((secret, codes), "a second run changes nothing");

        var later = now.AddSeconds(30);
        (await LoginAsync(user.Email, CodeAt(user.Secret, later), later)).AccessToken.Should().NotBeNullOrWhiteSpace();
        (await LoginAsync(user.Email, user.RecoveryCodes[2], later.AddSeconds(1))).AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task TheConversion_CountsAndLeavesAValueThatDoesNotDecryptForItsUser()
    {
        var a = await EnrolAsync();
        var b = await EnrolAsync();
        var (aSecret, _) = await RawAsync(a.UserId);
        // Under a previous key, so the conversion has to decrypt it, and bound to the other user, so it cannot.
        string foreign = new FieldProtector(TestEncryption.PreviousKey).Protect(a.Secret);
        await SetRawAsync(b.UserId, foreign, null);

        await using (var db = fixture.CreateContext())
        {
            (await TwoFactorSecrets.EncryptStoredAsync(db, CancellationToken.None)).Unreadable.Should().Be(1);
        }
        (await RawAsync(b.UserId)).Secret.Should().Be(foreign);
        (await RawAsync(a.UserId)).Secret.Should().Be(aSecret);

        // The class shares one database: leave no unreadable row for the idempotence test.
        await SetRawAsync(b.UserId, null, null);
    }

    [PostgresFact]
    public async Task TheGdprExport_CarriesNoSecretOrRecoveryCode_AndErasureClearsThem()
    {
        var user = await EnrolAsync();
        var (secret, codes) = await RawAsync(user.UserId);

        var export = await IdentityTestKit.With(fixture, s => s.ExportUserDataAsync(user.UserId, Ip));
        string json = JsonSerializer.Serialize(export);
        json.Should().NotContain(user.Secret).And.NotContain(secret!).And.NotContain(codes!).And.NotContain(FieldProtector.Prefix);
        json.Should().NotContain(IdentityTestKit.Totp.HashRecoveryCode(user.RecoveryCodes[0]));

        await IdentityTestKit.With(fixture, s => s.AnonymizeAndEraseUserAsync(user.UserId, Ip));
        (await RawAsync(user.UserId)).Should().Be(((string?)null, (string?)null));
    }
}
