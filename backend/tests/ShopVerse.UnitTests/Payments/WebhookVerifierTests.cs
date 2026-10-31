using FluentAssertions;
using ShopVerse.Domain.Payments;

namespace ShopVerse.UnitTests.Payments;

public class WebhookVerifierTests
{
    private const string Secret = "whsec_test_0123456789abcdef";
    private const string Body = "{\"id\":\"evt_1\",\"type\":\"payment.captured\",\"amount\":125050}";
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static long Ts(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    private static WebhookVerdict Check(string? signature, string? timestamp, string body = Body, DateTime? now = null, TimeSpan? tolerance = null) =>
        WebhookVerifier.Verify(Secret, signature, timestamp, body, now ?? Now, tolerance);

    [Fact]
    public void ASignedFreshRequest_IsAccepted_WithOrWithoutThePrefix()
    {
        var ts = Ts(Now);
        var sig = WebhookVerifier.Sign(Secret, ts, Body);
        Check(sig, ts.ToString()).Should().Be(WebhookVerdict.Accepted);
        Check("sha256=" + sig, ts.ToString()).Should().Be(WebhookVerdict.Accepted);
        Check(sig.ToUpperInvariant(), ts.ToString()).Should().Be(WebhookVerdict.Accepted);
    }

    [Fact]
    public void ATamperedBody_IsRefused()
    {
        var ts = Ts(Now);
        var sig = WebhookVerifier.Sign(Secret, ts, Body);
        Check(sig, ts.ToString(), Body.Replace("125050", "1")).Should().Be(WebhookVerdict.BadSignature);
    }

    [Fact]
    public void AWrongSecret_IsRefused()
    {
        var ts = Ts(Now);
        var sig = WebhookVerifier.Sign("another-secret", ts, Body);
        Check(sig, ts.ToString()).Should().Be(WebhookVerdict.BadSignature);
    }

    [Fact]
    public void ReplayingACapturedRequestWithAFreshTimestamp_IsRefused()
    {
        var oldTs = Ts(Now.AddHours(-3));
        var sig = WebhookVerifier.Sign(Secret, oldTs, Body);

        // The attacker keeps the captured signature but claims it was sent now: the timestamp is signed, so it fails.
        Check(sig, Ts(Now).ToString()).Should().Be(WebhookVerdict.BadSignature);
        // Sent honestly, it is simply too old.
        Check(sig, oldTs.ToString()).Should().Be(WebhookVerdict.Expired);
    }

    [Fact]
    public void TheWindow_IsInclusiveAtTheEdge_AndClosedBeyondIt()
    {
        var tolerance = TimeSpan.FromMinutes(5);
        var atEdge = Ts(Now.AddMinutes(-5));
        var late = Ts(Now.AddMinutes(-5).AddSeconds(-1));
        Check(WebhookVerifier.Sign(Secret, atEdge, Body), atEdge.ToString(), tolerance: tolerance).Should().Be(WebhookVerdict.Accepted);
        Check(WebhookVerifier.Sign(Secret, late, Body), late.ToString(), tolerance: tolerance).Should().Be(WebhookVerdict.Expired);
    }

    [Fact]
    public void ARequestFromTheFuture_IsRefused_ButSmallClockSkewIsTolerated()
    {
        var skew = Ts(Now.AddMinutes(2));
        var far = Ts(Now.AddMinutes(30));
        Check(WebhookVerifier.Sign(Secret, skew, Body), skew.ToString()).Should().Be(WebhookVerdict.Accepted);
        Check(WebhookVerifier.Sign(Secret, far, Body), far.ToString()).Should().Be(WebhookVerdict.FromTheFuture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingSignature_IsRefused(string? signature) =>
        Check(signature, Ts(Now).ToString()).Should().Be(WebhookVerdict.MissingSignature);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("-5")]
    [InlineData("0")]
    public void AMissingOrMalformedTimestamp_IsRefused(string? timestamp) =>
        Check("abc", timestamp).Should().Be(WebhookVerdict.MissingTimestamp);

    [Theory]
    [InlineData("deadbeef")]
    [InlineData("zz")]
    [InlineData("sha256=")]
    public void GarbageSignatures_AreRefused_WithoutThrowing(string signature) =>
        Check(signature, Ts(Now).ToString()).Should().Be(WebhookVerdict.BadSignature);

    [Fact]
    public void ABlankSecret_IsAProgrammingError_NotAVerdict()
    {
        var act = () => WebhookVerifier.Verify("", "abc", "1", Body, Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheSignatureCoversTheTimestampAndTheBody_BothAreNeeded()
    {
        var a = WebhookVerifier.Sign(Secret, 1000, Body);
        a.Should().NotBe(WebhookVerifier.Sign(Secret, 1001, Body));
        a.Should().NotBe(WebhookVerifier.Sign(Secret, 1000, Body + " "));
        a.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }
}
