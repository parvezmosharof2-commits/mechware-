using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.8 races over HTTP (ADR-0011 §9, threat model QT7 `TwoParallelApprovals_ApproveAndSendOnce`): two staff deciding on one version at the
/// same instant. The quote row and its versions are locked FOR NO KEY UPDATE before anything is read, so the second request waits, then sees
/// the first one's result: exactly one approval sends, the other gets the domain's own 409, and nothing is ever a 500. Its own class, so its
/// own login budget.
/// </summary>
[Collection(ExclusiveTimingCollection.Name)]
public sealed class QuoteIssueConcurrencyTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private const int Rounds = 6;
    private readonly QuoteIssueKit _kit = new(factory);

    private sealed record Staff(RfqApiKit.Person Author, RfqApiKit.Person Editor, RfqApiKit.Person Finance, RfqApiKit.Person Admin, World World, Item Item);

    private async Task<Staff> WorldAsync(string name)
    {
        var staff = new Staff(await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Support, "editor"),
            await _kit.StaffAsync(Permissions.Roles.Finance), await _kit.StaffAsync(Permissions.Roles.Admin), await _kit.CompanyAsync(name), await _kit.ItemAsync());
        await _kit.TenPercentFloorAsync();
        return staff;
    }

    /// <summary>All requests start together; returns (status, error code or null) of each.</summary>
    private static async Task<(HttpStatusCode Status, string? Code)[]> AtOnceAsync(params Func<Task<HttpResponseMessage>>[] calls)
    {
        using var start = new SemaphoreSlim(0);
        var tasks = calls.Select(async call =>
        {
            await start.WaitAsync();
            var r = await call();
            var body = await r.Content.ReadAsStringAsync();
            var code = r.IsSuccessStatusCode ? null : JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();
            return (r.StatusCode, code);
        }).ToList();
        start.Release(calls.Length);
        return await Task.WhenAll(tasks);
    }

    /// <summary>A pending version (over the authority) and, ready to send, the exact approval body of each approver.</summary>
    private async Task<(Guid Quote, object Body)> PendingAsync(Staff s, int quantity = 2)
    {
        var quote = await _kit.DraftAsync(s.Author.Client, s.World, [Line(s.Item, s.Item.Net * 90 / 100, quantity)]);
        var v = (await SentAsync(s.Author.Client, quote, "pending_approval")).GetProperty("quote").GetProperty("versions")[0];
        return (quote, new
        {
            contentHash = v.GetProperty("contentHash").GetString(),
            belowFloorLineIds = Array.Empty<Guid>(),
            overAuthorityLineIds = new[] { v.GetProperty("lines")[0].GetProperty("id").GetGuid() },
        });
    }

    private async Task<int> CountAsync(Guid quote, string? action = null, string? eventType = null)
    {
        await using var db = _kit.Db();
        return action is not null
            ? await db.AuditLogs.CountAsync(a => a.Action == action && a.EntityId == quote.ToString())
            : await db.OutboxMessages.CountAsync(o => o.EventType == eventType && o.AggregateId == quote);
    }

    [PostgresFact]
    public async Task TwoParallelApprovals_ApproveAndSendOnce()
    {
        var s = await WorldAsync("Race Approve Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var (quote, body) = await PendingAsync(s);
            var (financeCode, adminCode) = (await _kit.CodeAsync(s.Finance.Id), await _kit.CodeAsync(s.Admin.Id));
            var results = await AtOnceAsync(() => ApproveWithAsync(s.Finance.Client, quote, 1, body, financeCode), () => ApproveWithAsync(s.Admin.Client, quote, 1, body, adminCode));

            results.Select(r => r.Status).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], $"round {round}");
            results.Single(r => r.Status == HttpStatusCode.Conflict).Code.Should().Be("QUOTE_INVALID_TRANSITION", "the loser waited on the lock and saw it sent");
            var v = (await _kit.LoadAsync(quote)).Version(1);
            v.Status.Should().Be(QuoteVersionStatus.Sent);
            ((await CountAsync(quote, "quote.approved")), await CountAsync(quote, eventType: nameof(QuoteSentEvent))).Should().Be((1, 1), "approved and sent once");
        }
    }

    [PostgresFact]
    public async Task AnApprovalRacingARejectionOrARecall_ExactlyOneWins_AndNoneIsA500()
    {
        var s = await WorldAsync("Race Reject Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var (quote, body) = await PendingAsync(s);
            var code = await _kit.CodeAsync(s.Admin.Id);
            Func<Task<HttpResponseMessage>> back = round % 2 == 0
                ? () => PostAsync(s.Finance.Client, Url(quote, 1, "reject-approval"), new { comment = "Ask for a larger quantity" })
                : () => PostAsync(s.Editor.Client, Url(quote, 1, "recall"));
            var results = await AtOnceAsync(() => ApproveWithAsync(s.Admin.Client, quote, 1, body, code), back);

            results.Select(r => r.Status).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], $"round {round}");
            results.Single(r => r.Status == HttpStatusCode.Conflict).Code.Should().Be("QUOTE_INVALID_TRANSITION");
            var v = (await _kit.LoadAsync(quote)).Version(1);
            v.Status.Should().Be(results[0].Status == HttpStatusCode.OK ? QuoteVersionStatus.Sent : QuoteVersionStatus.Draft);
            (await CountAsync(quote, eventType: nameof(QuoteSentEvent))).Should().Be(results[0].Status == HttpStatusCode.OK ? 1 : 0);
        }
    }

    [PostgresFact]
    public async Task AnApprovalRacingAWithdrawalOfTheLiveVersion_NeverA500_AndOneVersionStaysLive()
    {
        var s = await WorldAsync("Race Withdraw Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await _kit.DraftAsync(s.Author.Client, s.World, [Line(s.Item, s.Item.Net)]);
            await SentAsync(s.Author.Client, quote, "sent");
            await DataAsync(await PostAsync(s.Author.Client, $"/api/v1/admin/quotes/{quote}/revise"));
            await EditAsync(s.Author.Client, quote, 2, [Line(s.Item, s.Item.Net * 90 / 100)]);
            var v2 = (await SentAsync(s.Author.Client, quote, "pending_approval", 2)).GetProperty("quote").GetProperty("versions")[1];
            var body = new
            {
                contentHash = v2.GetProperty("contentHash").GetString(),
                belowFloorLineIds = Array.Empty<Guid>(),
                overAuthorityLineIds = new[] { v2.GetProperty("lines")[0].GetProperty("id").GetGuid() },
            };
            var code = await _kit.CodeAsync(s.Finance.Id);

            var results = await AtOnceAsync(() => ApproveWithAsync(s.Finance.Client, quote, 2, body, code),
                () => PostAsync(s.Editor.Client, Url(quote, 1, "withdraw"), new { reason = "Supplier price went up" }));

            results.Should().NotContain(r => r.Status == HttpStatusCode.InternalServerError, $"round {round}");
            results[0].Status.Should().Be(HttpStatusCode.OK, "the approval never depends on the old version");
            var stored = await _kit.LoadAsync(quote);
            stored.Versions.Count(v => v.Status is QuoteVersionStatus.Sent or QuoteVersionStatus.Accepted).Should().Be(1);
            stored.Version(2).Status.Should().Be(QuoteVersionStatus.Sent);
            // The approval first: version 1 was superseded and the withdrawal finds nothing to withdraw. The withdrawal first: both succeed.
            (results[1].Status, stored.Version(1).Status).Should().BeOneOf((HttpStatusCode.OK, QuoteVersionStatus.Withdrawn),
                (HttpStatusCode.Conflict, QuoteVersionStatus.Superseded));
            if (results[1].Status == HttpStatusCode.Conflict) results[1].Code.Should().Be("QUOTE_INVALID_TRANSITION");
        }
    }

    [PostgresFact]
    public async Task TwoParallelSendsOfOneDraft_SendOnce()
    {
        var s = await WorldAsync("Race Send Ltd");
        for (var round = 0; round < Rounds; round++)
        {
            var quote = await _kit.DraftAsync(s.Author.Client, s.World, [Line(s.Item, s.Item.Net, quantity: round + 1)]);
            var results = await AtOnceAsync(() => SendAsync(s.Author.Client, quote), () => SendAsync(s.Editor.Client, quote));

            results.Select(r => r.Status).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], $"round {round}");
            results.Single(r => r.Status == HttpStatusCode.Conflict).Code.Should().Be("QUOTE_INVALID_TRANSITION");
            ((await CountAsync(quote, "quote.sent")), await CountAsync(quote, eventType: nameof(QuoteSentEvent))).Should().Be((1, 1));
        }
    }
}
