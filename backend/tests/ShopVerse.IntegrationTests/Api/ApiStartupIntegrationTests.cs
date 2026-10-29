using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ShopVerse.IntegrationTests.Api;

public class ApiStartupIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiStartupIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealthLive_ShouldReturn200Healthy()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Healthy");
    }

    [Fact]
    public async Task Request_ShouldIncludeCorrelationIdAndSecurityHeaders()
    {
        var customCorrelationId = "test-correlation-uuid-12345";
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", customCorrelationId);

        var response = await _client.SendAsync(request);

        response.Headers.Should().ContainKey("X-Correlation-Id");
        response.Headers.GetValues("X-Correlation-Id").First().Should().Be(customCorrelationId);

        response.Headers.Should().ContainKey("X-Content-Type-Options");
        response.Headers.GetValues("X-Content-Type-Options").First().Should().Be("nosniff");

        response.Headers.Should().ContainKey("X-Frame-Options");
        response.Headers.GetValues("X-Frame-Options").First().Should().Be("DENY");

        response.Headers.Should().ContainKey("Content-Security-Policy");
        response.Headers.GetValues("Content-Security-Policy").First().Should().Contain("default-src 'none'");
    }

    [Fact]
    public async Task MaliciousCorrelationId_ShouldBeSanitizedToNewGuid()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "invalid header with spaces & special chars!");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey("X-Correlation-Id");
        var returnedId = response.Headers.GetValues("X-Correlation-Id").First();
        returnedId.Should().MatchRegex("^[a-f0-9]{32}$");
    }

    [Fact]
    public async Task NonExistentRoute_ShouldReturn404()
    {
        var response = await _client.GetAsync("/api/v1/non-existent-endpoint");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
