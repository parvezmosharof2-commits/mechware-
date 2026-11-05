using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using ShopVerse.Api.Configuration;
using Xunit;

namespace ShopVerse.UnitTests.Configuration;

/// <summary>Finding #6 of the 2026-11-04 test: behind a proxy on another host every visitor shared one rate-limit bucket.</summary>
public class ForwardedHeadersSettingsTests
{
    private static ForwardedHeadersOptions Apply(Dictionary<string, string?> settings, bool trustAnyProxy = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersSettings.Apply(options, configuration, trustAnyProxy);
        return options;
    }

    [Fact]
    public void WithNothingConfigured_OnlyLoopbackIsTrusted()
    {
        var options = Apply([]);

        options.KnownProxies.Should().Contain(IPAddress.IPv6Loopback);
        options.KnownIPNetworks.Should().ContainSingle().Which.Contains(IPAddress.Loopback).Should().BeTrue();
        options.ForwardLimit.Should().Be(1);
    }

    [Fact]
    public void ConfiguredNetworksAndProxies_ReplaceTheLoopbackDefault()
    {
        var options = Apply(new()
        {
            ["ForwardedHeaders:KnownNetworks"] = "10.0.0.0/8, 173.245.48.0/20",
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10",
            ["ForwardedHeaders:ForwardLimit"] = "2"
        });

        options.KnownIPNetworks.Should().HaveCount(2);
        options.KnownIPNetworks.Should().Contain(n => n.Contains(IPAddress.Parse("10.20.30.40")));
        options.KnownIPNetworks.Should().Contain(n => n.Contains(IPAddress.Parse("173.245.50.1")));
        options.KnownIPNetworks.Should().NotContain(n => n.Contains(IPAddress.Loopback), "the default is replaced, not extended");
        options.KnownProxies.Should().Equal(IPAddress.Parse("192.0.2.10"));
        options.ForwardLimit.Should().Be(2);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownNetworks", "10.0.0.0/33")]
    [InlineData("ForwardedHeaders:KnownNetworks", "not-a-range")]
    [InlineData("ForwardedHeaders:KnownProxies", "10.0.0.300")]
    [InlineData("ForwardedHeaders:ForwardLimit", "0")]
    [InlineData("ForwardedHeaders:ForwardLimit", "9")]
    public void AMalformedEntry_StopsStartup(string key, string value)
    {
        var act = () => Apply(new() { [key] = value });

        act.Should().Throw<InvalidOperationException>().WithMessage("*ForwardedHeaders*");
    }

    [Fact]
    public void DevelopmentAndTests_TrustAnyProxy()
    {
        var options = Apply([], trustAnyProxy: true);

        options.KnownIPNetworks.Should().BeEmpty();
        options.KnownProxies.Should().BeEmpty();
    }
}
