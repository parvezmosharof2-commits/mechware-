using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Mechware.Infrastructure.Security;
using Xunit;

namespace Mechware.UnitTests.Security;

public class FieldProtectorTests
{
    private static readonly FieldProtector P = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void ARoundTripGivesBackTheValue_AndTheStoredFormHidesIt()
    {
        string stored = P.Protect("1234567890123");
        stored.Should().StartWith(FieldProtector.Prefix).And.NotContain("1234567890123");
        P.Unprotect(stored).Should().Be("1234567890123");
    }

    [Fact]
    public void TheSameValue_NeverEncryptsTheSameWayTwice() =>
        P.Protect("1234567890123").Should().NotBe(P.Protect("1234567890123"), "equal ciphertexts would reveal equal BINs");

    [Fact]
    public void AValueWrittenBeforeEncryption_IsReadAsItIs() => P.Unprotect("1234567890123").Should().Be("1234567890123");

    [Fact]
    public void AValueThatLooksEncrypted_IsStillEncrypted()
    {
        string looksEncrypted = P.Protect("TL-1");
        string stored = P.Protect(looksEncrypted);
        stored.Should().NotBe(looksEncrypted);
        P.Unprotect(stored).Should().Be(looksEncrypted, "what the user typed comes back exactly");
    }

    [Fact]
    public void AChangedByte_FailsInsteadOfReadingAsSomethingElse()
    {
        string stored = P.Protect("1234567890123");
        byte[] raw = Convert.FromBase64String(stored[P.CurrentPrefix.Length..]);
        raw[^1] ^= 0x01;
        var act = () => P.Unprotect(P.CurrentPrefix + Convert.ToBase64String(raw));
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void AnotherKey_CannotRead_AndGivesADifferentIndex()
    {
        var other = new FieldProtector(RandomNumberGenerator.GetBytes(32));
        var act = () => other.Unprotect(P.Protect("1234567890123"));
        act.Should().Throw<CryptographicException>();
        other.BlindIndex("1234567890123").Should().NotBe(P.BlindIndex("1234567890123"));
    }

    [Fact]
    public void TheBlindIndex_IsStable_AndIgnoresSpacesAndCase()
    {
        P.BlindIndex("tl-1234").Should().Be(P.BlindIndex(" TL-1234 "));
        P.BlindIndex("1234567890123").Should().MatchRegex("^[0-9a-f]{64}$").And.NotContain("1234567890123");
        P.BlindIndex("1234567890123").Should().NotBe(P.BlindIndex("1234567890124"));
    }

    [Fact]
    public void AfterRotation_OldValuesStayReadable_NewValuesUseTheNewKey_AndOnlyTheNewKeyCanReadThem()
    {
        byte[] oldKey = RandomNumberGenerator.GetBytes(32), newKey = RandomNumberGenerator.GetBytes(32);
        var before = new FieldProtector(oldKey);
        var rotated = new FieldProtector(newKey, [oldKey]);
        string old = before.Protect("1234567890123");

        rotated.Unprotect(old).Should().Be("1234567890123");
        rotated.IsCurrent(old).Should().BeFalse("the backfill must re-encrypt it");

        string fresh = rotated.Protect("1234567890123");
        fresh.Should().StartWith(rotated.CurrentPrefix);
        rotated.IsCurrent(fresh).Should().BeTrue();
        var oldReadsNew = () => before.Unprotect(fresh);
        oldReadsNew.Should().Throw<CryptographicException>();

        // Once the old key is retired, what it wrote cannot be read: it fails, never reads as something else.
        var retired = () => new FieldProtector(newKey).Unprotect(old);
        retired.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void BlindIndexes_CoverTheCurrentKeyFirst_ThenThePreviousOnes()
    {
        byte[] oldKey = RandomNumberGenerator.GetBytes(32), newKey = RandomNumberGenerator.GetBytes(32);
        var rotated = new FieldProtector(newKey, [oldKey]);
        rotated.BlindIndexes("1234567890123").Should().Equal(
            new FieldProtector(newKey).BlindIndex("1234567890123"), new FieldProtector(oldKey).BlindIndex("1234567890123"));
        rotated.BlindIndex("1234567890123").Should().Be(new FieldProtector(newKey).BlindIndex("1234567890123"));
    }

    [Fact]
    public void ValuesInTheFirstFormat_WrittenWithoutAKeyId_AreStillRead()
    {
        // The exact "enc:v1:" format released first: AES-256-GCM under HKDF(master, "mechware/field-encryption/v1").
        byte[] master = RandomNumberGenerator.GetBytes(32);
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("mechware/field-encryption/v1"));
        byte[] data = Encoding.UTF8.GetBytes("TL-2026-1"), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[data.Length], tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        string v1 = "enc:v1:" + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);

        new FieldProtector(master).Unprotect(v1).Should().Be("TL-2026-1");
        new FieldProtector(RandomNumberGenerator.GetBytes(32), [master]).Unprotect(v1).Should().Be("TL-2026-1");
        new FieldProtector(master).IsCurrent(v1).Should().BeFalse("old-format values are rewritten with a key id");
        var unknown = () => new FieldProtector(RandomNumberGenerator.GetBytes(32)).Unprotect(v1);
        unknown.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void AnUnknownFormat_IsRefused_NotGuessed()
    {
        string stored = P.Protect("x");
        var act = () => P.Unprotect(stored.Replace("enc:v2:", "enc:v9:"));
        act.Should().Throw<CryptographicException>().WithMessage("*format*");
    }

    [Fact]
    public void AValueUnderAKeyThatIsNotConfigured_IsRefused()
    {
        string stored = new FieldProtector(RandomNumberGenerator.GetBytes(32)).Protect("x");
        var act = () => P.Unprotect(stored);
        act.Should().Throw<CryptographicException>().WithMessage("*not configured*");
    }

    [Fact]
    public void ConfigurationWithPreviousKeys_ReadsWhatTheyWrote()
    {
        byte[] oldKey = RandomNumberGenerator.GetBytes(32), newKey = RandomNumberGenerator.GetBytes(32), spare = RandomNumberGenerator.GetBytes(32);
        string old = new FieldProtector(oldKey).Protect("BIN-1"), spareOld = new FieldProtector(spare).Protect("BIN-2");
        try
        {
            FieldEncryption.ConfigureFrom(Convert.ToBase64String(newKey),
                $"{Convert.ToBase64String(oldKey)}, {Convert.ToBase64String(spare)}", requireRealKey: true);
            FieldEncryption.Current.Unprotect(old).Should().Be("BIN-1");
            FieldEncryption.Current.Unprotect(spareOld).Should().Be("BIN-2");
            FieldEncryption.Current.Protect("x").Should().StartWith(new FieldProtector(newKey).CurrentPrefix);
        }
        finally
        {
            FieldEncryption.Configure(FieldEncryption.DevelopmentKey);
        }
    }

    [Fact]
    public void ARealKey_IsRequiredOutsideDevelopment()
    {
        var missing = () => FieldEncryption.ConfigureFrom(null, null, requireRealKey: true);
        missing.Should().Throw<InvalidOperationException>().WithMessage("*not set*");
        var published = () => FieldEncryption.ConfigureFrom(Convert.ToBase64String(FieldEncryption.DevelopmentKey), null, requireRealKey: true);
        published.Should().Throw<InvalidOperationException>().WithMessage("*published development key*");
        var badPrevious = () => FieldEncryption.ConfigureFrom(Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[8]), requireRealKey: true);
        badPrevious.Should().Throw<InvalidOperationException>();
        FieldEncryption.ConfigureFrom(null, null, requireRealKey: false); // Development without a key: keeps the development key.
    }

    [Fact]
    public void AKeyOfTheWrongSize_IsRefused()
    {
        var act = () => new FieldProtector(new byte[16]);
        act.Should().Throw<ArgumentException>();
        var parse = () => FieldEncryption.ParseKey(Convert.ToBase64String(new byte[16]));
        parse.Should().Throw<InvalidOperationException>();
        var previous = () => new FieldProtector(new byte[32], [new byte[16]]);
        previous.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheKeyDerivation_IsPinned_SoStoredIndexesKeepMatching()
    {
        // Known answer, computed independently (RFC 5869 HKDF-SHA256, then HMAC-SHA256). If the derivation or the use of the
        // separate index key changes, every bin_index already in the database stops matching and duplicate BINs slip through.
        var pinned = new FieldProtector([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);
        pinned.CurrentPrefix.Should().Be("enc:v2:7dd75252:");
        pinned.BlindIndex(" bin-000123 ").Should().Be("74827eefb70818eac6fee0cdda2a62f8a69c3480b9b44968206e6c47e0795e12");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(27)]   // one byte short of nonce + tag
    public void ATruncatedValue_IsRefusedAsCorrupt_NotAsAnUnrelatedError(int bytes)
    {
        // Callers treat CryptographicException as "this stored value is unreadable"; an OverflowException from a negative
        // buffer length would escape that handling.
        string stored = P.CurrentPrefix + Convert.ToBase64String(new byte[bytes]);
        var act = () => P.Unprotect(stored);
        act.Should().Throw<CryptographicException>().WithMessage("*truncated*");
    }
}
