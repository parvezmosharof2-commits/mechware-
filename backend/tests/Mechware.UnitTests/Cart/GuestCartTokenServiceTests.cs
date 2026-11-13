using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Cart;
using Mechware.Domain.Cart;
using Mechware.Infrastructure;
using Mechware.Infrastructure.Cart;
using Mono.Cecil;
using DomainCart = Mechware.Domain.Cart.Cart;

namespace Mechware.UnitTests.Cart;

/// <summary>
/// E1.2: the signed guest-cart token. A token is only accepted when it is exactly what this service issued, under a key it
/// holds, before its signed expiry. Everything else is null, never an exception.
/// </summary>
public class GuestCartTokenServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly Guid Id = Guid.NewGuid();

    private static (GuestCartTokenService Service, Clock Clock) Make(byte[]? key = null, params byte[][] previous)
    {
        var clock = new Clock(T0);
        return (new GuestCartTokenService(key ?? Key, previous, clock), clock);
    }

    private static string ValidToken() => Make().Service.Issue(new GuestCartId(Id));

    /// <summary>A token with a correct MAC over an arbitrary payload, as only a holder of the key could make it.</summary>
    private static string Craft(Guid id, long expiry, byte version, byte[]? key = null)
    {
        byte[] payload = new byte[25];
        id.TryWriteBytes(payload.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), expiry);
        payload[24] = version;
        byte[] mac = HMACSHA256.HashData(GuestCartTokenService.DeriveKey(key ?? Key), payload);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
    }

    private static (byte[] Payload, byte[] Mac) Decode(string token) =>
        (Base64Url.DecodeFromChars(token.AsSpan(0, 34)), Base64Url.DecodeFromChars(token.AsSpan(35)));

    private static string Encode(byte[] payload, byte[] mac) => Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);

    [Fact]
    public void AnIssuedToken_ValidatesToItsGuestId_AndIsCookieSafe()
    {
        string token = ValidToken();
        Make().Service.Validate(token).Should().Be(new GuestCartId(Id));
        token.Should().HaveLength(GuestCartTokenService.TokenLength).And.HaveLength(78)
            .And.MatchRegex("^[A-Za-z0-9_-]{34}\\.[A-Za-z0-9_-]{43}$", "URL- and cookie-safe, no padding");
    }

    [Fact]
    public void ForgedSignature_IsRefused()
    {
        var (payload, _) = Decode(ValidToken());
        Make().Service.Validate(Encode(payload, RandomNumberGenerator.GetBytes(32))).Should().BeNull();
        Make().Service.Validate(Encode(payload, new byte[32])).Should().BeNull();
    }

    [Fact]
    public void AForgedGuestId_WithTheSignatureOfAnotherToken_IsRefused()
    {
        var (_, mac) = Decode(ValidToken());
        byte[] other = new byte[25];
        Guid.NewGuid().TryWriteBytes(other.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(other.AsSpan(16, 8), (T0 + ICartStore.GuestTtl).ToUnixTimeSeconds());
        other[24] = GuestCartTokenService.Version;
        Make().Service.Validate(Encode(other, mac)).Should().BeNull();
    }

    [Fact]
    public void FlippingAnyBitOfAnyByte_InPayloadOrSignature_IsRefused()
    {
        var (payload, mac) = Decode(ValidToken());
        var service = Make().Service;
        for (int i = 0; i < payload.Length + mac.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] p = (byte[])payload.Clone(), m = (byte[])mac.Clone();
                if (i < p.Length) p[i] ^= (byte)(1 << bit);
                else m[i - p.Length] ^= (byte)(1 << bit);
                service.Validate(Encode(p, m)).Should().BeNull($"byte {i} bit {bit} was flipped");
            }
        }
    }

    [Fact]
    public void ChangingAnyCharacter_IsRefused_IncludingSpellingsThatDecodeToTheSameBytes()
    {
        string token = ValidToken();
        var service = Make().Service;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.=+/ ";
        for (int i = 0; i < token.Length; i++)
        {
            foreach (char c in alphabet.Where(c => c != token[i]))
                service.Validate(token[..i] + c + token[(i + 1)..]).Should().BeNull($"position {i} was changed to '{c}'");
        }
    }

    [Fact]
    public void ANonCanonicalSpelling_OfAValidToken_IsRefused()
    {
        // The last payload character carries 4 unused bits and the last MAC character 2. A lenient decoder would map these
        // spellings to the same bytes (one token, many strings); .NET's Base64Url refuses them, and Validate re-encodes and
        // compares as a second layer in case a future decoder is lenient.
        string token = ValidToken();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        string Bump(string t, int at) => t[..at] + alphabet[alphabet.IndexOf(t[at]) + 1] + t[(at + 1)..];

        Make().Service.Validate(Bump(token, 33)).Should().BeNull();
        Make().Service.Validate(Bump(token, 77)).Should().BeNull();
    }

    [Fact]
    public void AnExpiredToken_IsRefused_ExactlyAtItsExpiry()
    {
        var (service, clock) = Make();
        string token = service.Issue(new GuestCartId(Id));

        clock.Now = T0 + ICartStore.GuestTtl - TimeSpan.FromSeconds(1);
        service.Validate(token).Should().Be(new GuestCartId(Id), "one second before expiry it still works");
        clock.Now = T0 + ICartStore.GuestTtl;
        service.Validate(token).Should().BeNull();
        clock.Now = T0 + TimeSpan.FromDays(365);
        service.Validate(token).Should().BeNull();
    }

    [Fact]
    public void ATokenThatClaimsToLiveLongerThanAnyIssuedToken_IsRefused()
    {
        long now = T0.ToUnixTimeSeconds();
        var service = Make().Service;
        service.Validate(Craft(Id, now + (long)ICartStore.GuestTtl.TotalSeconds, GuestCartTokenService.Version)).Should().NotBeNull("the control");
        service.Validate(Craft(Id, now + (long)ICartStore.GuestTtl.TotalSeconds + 3600, GuestCartTokenService.Version)).Should().BeNull();
        service.Validate(Craft(Id, long.MaxValue, GuestCartTokenService.Version)).Should().BeNull();
        service.Validate(Craft(Id, long.MinValue, GuestCartTokenService.Version)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void ASignedTokenOfAnotherVersion_IsRefused(byte version)
    {
        long expiry = T0.ToUnixTimeSeconds() + 3600;
        Make().Service.Validate(Craft(Id, expiry, GuestCartTokenService.Version)).Should().NotBeNull("the control");
        Make().Service.Validate(Craft(Id, expiry, version)).Should().BeNull();
    }

    [Fact]
    public void ASignedTokenForTheEmptyGuid_IsRefused() =>
        Make().Service.Validate(Craft(Guid.Empty, T0.ToUnixTimeSeconds() + 3600, GuestCartTokenService.Version)).Should().BeNull();

    [Fact]
    public void TruncatedOrExtendedTokens_AreRefused()
    {
        string token = ValidToken();
        var service = Make().Service;
        for (int length = 0; length < token.Length; length++)
            service.Validate(token[..length]).Should().BeNull($"truncated to {length}");
        foreach (string longer in new[] { token + "A", token + "=", token + ".", "A" + token, token + token })
            service.Validate(longer).Should().BeNull();
        service.Validate(token[..34]).Should().BeNull("the payload alone");
        service.Validate(token[35..]).Should().BeNull("the signature alone");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("a.b")]
    [InlineData("null")]
    [InlineData("undefined")]
    public void EmptyOrJunkInput_IsRefused(string? token) => Make().Service.Validate(token).Should().BeNull();

    [Fact]
    public void HugeInput_IsRefusedWithoutDecoding()
    {
        var service = Make().Service;
        service.Validate(new string('A', 10 * 1024 * 1024)).Should().BeNull();
        service.Validate(ValidToken() + new string(' ', 1_000_000)).Should().BeNull();
    }

    [Fact]
    public void NonBase64UrlCharacters_OfTheRightLength_AreRefused()
    {
        string token = ValidToken();
        var service = Make().Service;
        foreach (char bad in "+/=!\0\u00fc\u0985 \t\n%")
        {
            service.Validate(bad + token[1..]).Should().BeNull();
            service.Validate(token[..40] + bad + token[41..]).Should().BeNull();
        }
        service.Validate(token.Replace('.', '_')).Should().BeNull("the separator is required");
    }

    [Fact]
    public void RandomGarbage_NeverThrows_AndIsNeverAccepted()
    {
        var random = new Random(20261113);
        var service = Make().Service;
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.=+/ \u0985";
        for (int n = 0; n < 20_000; n++)
        {
            int length = n % 3 == 0 ? GuestCartTokenService.TokenLength : random.Next(0, 200);
            var text = new string(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());
            if (length == GuestCartTokenService.TokenLength) text = text[..34] + "." + text[35..];
            service.Validate(text).Should().BeNull();
        }
    }

    [Fact]
    public void ATokenSignedWithAnotherKey_IsRefused()
    {
        string theirs = Make(RandomNumberGenerator.GetBytes(32)).Service.Issue(new GuestCartId(Id));
        Make().Service.Validate(theirs).Should().BeNull();
    }

    [Fact]
    public void KeyRotation_OldTokensStillValidate_NewTokensUseTheNewKey()
    {
        byte[] oldKey = RandomNumberGenerator.GetBytes(32), newKey = RandomNumberGenerator.GetBytes(32);
        var before = Make(oldKey).Service;
        var during = Make(newKey, oldKey).Service;
        var newOnly = Make(newKey).Service;

        string oldToken = before.Issue(new GuestCartId(Id));
        during.Validate(oldToken).Should().Be(new GuestCartId(Id), "a token from before the rotation keeps working");

        string newToken = during.Issue(new GuestCartId(Id));
        before.Validate(newToken).Should().BeNull("new tokens are signed with the new key, not the old one");
        newOnly.Validate(newToken).Should().Be(new GuestCartId(Id));
        newOnly.Validate(oldToken).Should().BeNull("once the old key is removed its tokens stop working");
    }

    [Fact]
    public void Issue_RefusesTheEmptyGuid() =>
        ((Action)(() => Make().Service.Issue(new GuestCartId(Guid.Empty)))).Should().Throw<ArgumentException>();

    [Fact]
    public void Validate_ComparesTheSignatureInConstantTime()
    {
        // Timing cannot be measured reliably in a unit test; this pins the API: the MAC is compared with
        // CryptographicOperations.FixedTimeEquals and never with an early-exit byte comparison.
        using var module = ModuleDefinition.ReadModule(typeof(GuestCartTokenService).Assembly.Location);
        var validate = module.GetType(typeof(GuestCartTokenService).FullName).Methods.Single(m => m.Name == nameof(GuestCartTokenService.Validate));
        var calls = validate.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToList();

        calls.Should().Contain(m => m.DeclaringType.FullName == "System.Security.Cryptography.CryptographicOperations" && m.Name == "FixedTimeEquals");
        calls.Should().NotContain(m => m.Name == "SequenceEqual" || m.Name == "Equals" || m.Name == "SequenceCompareTo");
    }

    // ---- keys from configuration ----

    private static GuestCartTokenOptions Options(string? key, string? previous = null) => new() { TokenKey = key, PreviousTokenKeys = previous };

    private static readonly string RealKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string DevKey = Convert.ToBase64String(GuestCartTokenOptions.DevelopmentKey);

    [Theory]
    [InlineData(null, null, "GuestCart:TokenKey is not set")]
    [InlineData("", null, "GuestCart:TokenKey is not set")]
    [InlineData("dev", null, "published development key")]
    [InlineData("real", "dev", "published development key")]
    [InlineData("short", null, "at least 32 random bytes")]
    [InlineData("zeros", null, "at least 32 random bytes")]
    [InlineData("not base64!", null, "must be base64")]
    [InlineData("real", "not base64!", "must be base64")]
    public void OutsideDevelopment_AWeakOrMissingKey_RefusesStartup(string? key, string? previous, string message)
    {
        string? Resolve(string? v) => v switch
        {
            "dev" => DevKey,
            "real" => RealKey,
            "short" => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
            "zeros" => Convert.ToBase64String(new byte[32]),
            _ => v,
        };
        var act = () => Options(Resolve(key), Resolve(previous)).ResolveKeys(requireRealKey: true);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void InDevelopment_AMissingKey_UsesTheDevelopmentKey_ButAMalformedOneStillFails()
    {
        Options(null).ResolveKeys(requireRealKey: false).Current.Should().Equal(GuestCartTokenOptions.DevelopmentKey);
        ((Action)(() => Options("short").ResolveKeys(requireRealKey: false))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ARealKeyWithPreviousKeys_Resolves()
    {
        string previous = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var (current, old) = Options(RealKey, $" {previous} ,").ResolveKeys(requireRealKey: true);
        current.Should().Equal(Convert.FromBase64String(RealKey));
        old.Should().ContainSingle().Which.Should().Equal(Convert.FromBase64String(previous));
    }

    [Fact]
    public void AddCartStore_RefusesStartupWithoutAKey_AndRegistersAWorkingTokenService()
    {
        var empty = new ConfigurationBuilder().Build();
        ((Action)(() => new ServiceCollection().AddCartStore(empty, requireRealSetup: true)))
            .Should().Throw<InvalidOperationException>().WithMessage("*GuestCart:TokenKey is not set*");

        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GuestCart:TokenKey"] = RealKey }).Build();
        var services = new ServiceCollection().AddCartStore(configured, requireRealSetup: true);
        services.Should().Contain(d => d.ServiceType == typeof(ICartStore) && d.ImplementationType == typeof(RedisCartStore));
        using var provider = services.BuildServiceProvider();
        var tokens = provider.GetRequiredService<IGuestCartTokenService>();
        string token = tokens.Issue(new GuestCartId(Id));
        new GuestCartTokenService(Convert.FromBase64String(RealKey), null, TimeProvider.System).Validate(token).Should().Be(new GuestCartId(Id));
        new GuestCartTokenService(GuestCartTokenOptions.DevelopmentKey, null, TimeProvider.System).Validate(token).Should().BeNull();
    }

    // ---- the limits the store re-checks on save and on load ----

    private static DomainCart CartOf(params CartItem[] items) => new(items, 1);

    [Fact]
    public void StoreLimits_AcceptAFullCart_AtTheLimits() =>
        RedisCartStore.Violation(new DomainCart([.. Enumerable.Range(0, 100).Select(_ => new CartItem(Guid.NewGuid(), 10_000, 0))], 7))
            .Should().BeNull();

    [Fact]
    public void StoreLimits_RefuseWhatTheEngineNeverProduces()
    {
        var v = Guid.NewGuid();
        RedisCartStore.Violation(new DomainCart([.. Enumerable.Range(0, 101).Select(_ => new CartItem(Guid.NewGuid(), 1, 1))])).Should().Be("too many lines");
        RedisCartStore.Violation(CartOf(new CartItem(v, 10_001, 1))).Should().Be("quantity out of range");
        RedisCartStore.Violation(CartOf(new CartItem(v, 0, 1))).Should().Be("quantity out of range");
        RedisCartStore.Violation(CartOf(new CartItem(v, -1, 1))).Should().Be("quantity out of range");
        RedisCartStore.Violation(CartOf(new CartItem(v, 1, -1))).Should().Be("negative price");
        RedisCartStore.Violation(CartOf(new CartItem(Guid.Empty, 1, 1))).Should().Be("line without a variant");
        RedisCartStore.Violation(CartOf(new CartItem(v, 1, 1), new CartItem(v, 2, 1))).Should().Be("duplicate variant");
        RedisCartStore.Violation(new DomainCart([], -1)).Should().Be("negative version");
        RedisCartStore.Violation(new DomainCart(null!)).Should().Be("no items");
        RedisCartStore.Violation(new DomainCart([null!])).Should().Be("line without a variant");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
