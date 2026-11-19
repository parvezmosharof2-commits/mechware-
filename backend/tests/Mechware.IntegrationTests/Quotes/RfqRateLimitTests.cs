using System.Net;
using FluentAssertions;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.RfqApiKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>The host with a small per-user budget for asking for quotes (the policy <c>QuoteRequests</c>, 10 a minute by default).</summary>
public sealed class RfqLimitedFactory() : DbBackedFactory(null, new Dictionary<string, string>
{
    ["RateLimiting:QuoteRequestsPermitPerMinute"] = "3",
    ["Quotes:MaxRfqsPerCompanyPerHour"] = "1000",
    ["Quotes:MaxOpenRfqsPerCompany"] = "1000",
});

/// <summary>D4.5, QT10: the HTTP limit is per signed-in user and counts creating only; the per-company caps are in <see cref="BuyerRfqServiceTests"/>.</summary>
public sealed class RfqRateLimitTests(RfqLimitedFactory factory) : IClassFixture<RfqLimitedFactory>
{
    private readonly RfqApiKit _kit = new(factory);

    [PostgresFact]
    public async Task RfqIsRateLimited()
    {
        var (asker, other) = (await _kit.PersonAsync(), await _kit.PersonAsync());
        var company = await _kit.CompanyAsync(asker.Id, "Rfq Limit Ltd", true, (other.Id, Mechware.Domain.Companies.CompanyRoles.Requester));
        var variant = await _kit.VariantAsync(null);
        object Body() => new { source = "product", variantId = variant.VariantId, quantity = 1 };

        for (var i = 0; i < 3; i++) (await PostAsync(asker.Client, company, Body(), Key())).StatusCode.Should().Be(HttpStatusCode.Created);
        var limited = await PostAsync(asker.Client, company, Body(), Key());
        (await ErrorAsync(limited, HttpStatusCode.TooManyRequests)).Code.Should().Be("RATE_LIMITED");

        // The budget is the user's own: a colleague still asks; reads are not counted.
        (await PostAsync(other.Client, company, Body(), Key())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await asker.Client.GetAsync(Url(company))).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
