using System.Security.Cryptography;
using FluentAssertions;
using ShopVerse.Infrastructure.Security;
using Xunit;

namespace ShopVerse.UnitTests.Security;

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
    public void ProtectingTwice_DoesNotDoubleEncrypt()
    {
        string once = P.Protect("TL-1");
        P.Protect(once).Should().Be(once);
    }

    [Fact]
    public void AChangedByte_FailsInsteadOfReadingAsSomethingElse()
    {
        string stored = P.Protect("1234567890123");
        byte[] raw = Convert.FromBase64String(stored[FieldProtector.Prefix.Length..]);
        raw[^1] ^= 0x01;
        var act = () => P.Unprotect(FieldProtector.Prefix + Convert.ToBase64String(raw));
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
    public void AKeyOfTheWrongSize_IsRefused()
    {
        var act = () => new FieldProtector(new byte[16]);
        act.Should().Throw<ArgumentException>();
        var parse = () => FieldEncryption.ParseKey(Convert.ToBase64String(new byte[16]));
        parse.Should().Throw<InvalidOperationException>();
    }
}
