using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.6 races over HTTP (ADR-0011 §9): two staff acting on one quote at the same instant. The quote row and its versions are locked
/// FOR NO KEY UPDATE, so the second request waits, then sees the first one's result: exactly one wins, the other gets the domain's
/// own 409 (not a concurrency or database error), and nothing is ever a 500. Its own class, so its own login budget.
/// </summary>
[Collection(nameof(ExclusiveTimingCollection))]
public sealed class StaffQuoteConcurrencyTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private const int Rounds = 6;
    private readonly StaffQuoteKit _kit = new(factory);

    private async Task<(RfqApiKit.Person A, RfqApiKit.Person B, Guid Company, Guid Requester, Guid Variant)> WorldAsync(string name)
    {
        var (a, b) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await _kit.Rfq.PersonAsync(Permissions.Roles.Admin));
        var (owner, requester) = (await _kit.BuyerAsync(), await _kit.BuyerAsync());
        var company = await _kit.Rfq.CompanyAsync(owner, name, true, (requester, CompanyRoles.Requester));
        return (a, b, company, requester, (await _kit.Rfq.VariantAsync(null)).VariantId);
    }

    /// <summary>Both requests start together; returns (status, error code or null) of each.</summary>
    private static async Task<(HttpStatusCode Status, string? Code)[]> AtOnceAsync(params Func<Task<HttpResponseMessage>>[] calls)
    {
        using var start = new SemaphoreSlim(0);
        var tasks = calls.Select(async call =>
        {
            await start.WaitAsync();
            var r = await call();
            var body = await r.Content.ReadAsStringAsync();
            var code = r.IsSuccessStatusCode ? null : System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();
            return (r.StatusCode, code);
        }).ToList();
        start.Release(calls.Length);
        return await Task.WhenAll(tasks);
    }

    [PostgresFact]
    public async Task TwoStaffRevisingTheSameQuoteAtOnce_ExactlyOneWins_AndNoneIsA500()
    {
        var (a, b, company, requester, variant) = await WorldAsync("Desk Race Revise Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await CreateAsync(a.Client, new { rfqId = await _kit.RfqAsync(company, requester, (variant, 1)) });
            await _kit.SendAsync(quote);
            var results = await AtOnceAsync(() => PostAsync(a.Client, $"/api/v1/admin/quotes/{quote}/revise"), () => PostAsync(b.Client, $"/api/v1/admin/quotes/{quote}/revise"));
            results.Select(r => r.Status).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], $"round {round}");
            results.Single(r => r.Status == HttpStatusCode.Conflict).Code.Should().Be("QUOTE_OPEN_VERSION_EXISTS", "the loser waited on the lock and saw the new draft");
            await using var db = _kit.Db();
            (await db.QuoteVersions.CountAsync(v => v.QuoteId == quote)).Should().Be(2);
            (await db.AuditLogs.CountAsync(x => x.Action == "quote.revised" && x.EntityId == quote.ToString())).Should().Be(1);
        }
    }

    [PostgresFact]
    public async Task TwoStaffClosingOrClosingAndRevisingTheSameQuoteAtOnce_NeverDoubleCount_AndNoneIsA500()
    {
        var (a, b, company, requester, variant) = await WorldAsync("Desk Race Close Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await CreateAsync(a.Client, new { rfqId = await _kit.RfqAsync(company, requester, (variant, 1)) });
            await _kit.SendAsync(quote);
            var results = await AtOnceAsync(() => PostAsync(a.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Closed by A" }),
                () => PostAsync(b.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Closed by B" }));
            results.Select(r => r.Status).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], $"round {round}");
            results.Single(r => r.Status == HttpStatusCode.Conflict).Code.Should().Be("QUOTE_CLOSED");
            await using var db = _kit.Db();
            (await db.OutboxMessages.CountAsync(m => m.EventType == nameof(QuoteWithdrawnEvent) && m.AggregateId == quote)).Should().Be(1, "withdrawn once");
            (await db.AuditLogs.CountAsync(x => x.Action == "quote.closed" && x.EntityId == quote.ToString())).Should().Be(1);
        }

        // Close racing revise: either order is valid (a closed quote takes no revision; a revised one may still be closed), never both
        // a new version AND a refusal that hides it, and never a 500.
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await CreateAsync(a.Client, new { rfqId = await _kit.RfqAsync(company, requester, (variant, 1)) });
            await _kit.SendAsync(quote);
            var results = await AtOnceAsync(() => PostAsync(a.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Closed by A" }),
                () => PostAsync(b.Client, $"/api/v1/admin/quotes/{quote}/revise"));
            results.Should().NotContain(r => (int)r.Status >= 500);
            results[0].Status.Should().Be(HttpStatusCode.OK, "closing always succeeds: before the revision or after it");
            await using var db = _kit.Db();
            var versions = await db.QuoteVersions.CountAsync(v => v.QuoteId == quote);
            (results[1].Status, results[1].Code, versions).Should().BeOneOf((HttpStatusCode.OK, null, 2), (HttpStatusCode.Conflict, "QUOTE_CLOSED", 1));
        }
    }

    [PostgresFact]
    public async Task TwoQuotesStartedFromOneRfqAtOnce_PrefillEachAskedLineOnce()
    {
        var (a, b, company, requester, variant) = await WorldAsync("Desk Race Prefill Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var rfq = await _kit.RfqAsync(company, requester, (variant, 1), ((await _kit.Rfq.VariantAsync(null)).VariantId, 2));
            var results = await AtOnceAsync(() => PostAsync(a.Client, "/api/v1/admin/quotes", new { rfqId = rfq }), () => PostAsync(b.Client, "/api/v1/admin/quotes", new { rfqId = rfq }));
            results.Select(r => r.Status).Should().AllBeEquivalentTo(HttpStatusCode.Created);
            await using var db = _kit.Db();
            var lines = await (from l in db.Set<QuoteLine>()
                               join v in db.QuoteVersions on l.VersionId equals v.Id
                               join q in db.Quotes on v.QuoteId equals q.Id
                               where q.RfqId == rfq
                               select l.RfqLineId).ToListAsync();
            lines.Should().HaveCount(2).And.OnlyHaveUniqueItems("each asked line is on one quote only");
        }
    }
}
