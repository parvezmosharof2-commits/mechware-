using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.6 (+ D4.7 editor and D4.8 send / approval routes) permission matrix over HTTP (threat model QT1 `StaffQuoteEndpoints_WithoutPermission_Are403`): every staff desk route for
/// Support, Finance, Admin, Moderator, Seller and Buyer with real sign-ins, and anonymous. Reads need <c>quotes.view</c> (Support,
/// Finance, Admin), commands <c>quotes.edit</c> (Support, Admin), approval decisions and the approval queue <c>quotes.approve</c> (Finance,
/// Admin; approving also a fresh authenticator code, given to everyone here). Commands run on ids that do not exist, so a permitted role
/// reaches the service (404) and nothing is written. Its own class, so its own login budget.
/// </summary>
public sealed class StaffQuoteAccessEndpointsTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private readonly StaffQuoteKit _kit = new(factory);

    [PostgresFact]
    public async Task StaffQuoteEndpoints_WithoutPermission_Are403()
    {
        var roles = new[] { Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Admin, Permissions.Roles.Moderator,
            Permissions.Roles.Seller, Permissions.Roles.Buyer };
        var people = new Dictionary<string, RfqApiKit.Person>();
        foreach (var role in roles) people[role] = await _kit.Rfq.PersonAsync(role);
        string[] viewers = [Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Admin];
        string[] editors = [Permissions.Roles.Support, Permissions.Roles.Admin];
        string[] approvers = [Permissions.Roles.Finance, Permissions.Roles.Admin];
        var (id, company, contact) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var byClient = people.Values.ToDictionary(p => p.Client, p => p.Id);
        async Task<HttpResponseMessage> ApproveAsync(HttpClient c)
        {
            string? code = null;
            if (byClient.TryGetValue(c, out var who))
            {
                await using var db = _kit.Db();
                code = await QuoteIssueKit.CodeAsync(db, who);
            }

            return await QuoteIssueKit.ApproveWithAsync(c, id, 1, new { contentHash = new string('a', 64) }, code);
        }

        var routes = new (string Name, string[] Permitted, HttpStatusCode Allowed, Func<HttpClient, Task<HttpResponseMessage>> Call)[]
        {
            ("list rfqs", viewers, HttpStatusCode.OK, c => c.GetAsync("/api/v1/admin/rfqs")),
            ("rfq detail", viewers, HttpStatusCode.NotFound, c => c.GetAsync($"/api/v1/admin/rfqs/{id}")),
            ("assign rfq", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/rfqs/{id}/assign", new { assigneeId = (Guid?)null })),
            ("close rfq", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/rfqs/{id}/close", new { reason = "No bid today" })),
            ("create quote", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, "/api/v1/admin/quotes", new { companyId = company, contactUserId = contact })),
            ("list quotes", viewers, HttpStatusCode.OK, c => c.GetAsync("/api/v1/admin/quotes")),
            ("quote detail", viewers, HttpStatusCode.NotFound, c => c.GetAsync($"/api/v1/admin/quotes/{id}")),
            ("revise quote", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/revise")),
            ("close quote", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/close", new { reason = "Duplicate quote" })),
            ("edit draft (D4.7)", editors, HttpStatusCode.NotFound, c => c.PutAsJsonAsync($"/api/v1/admin/quotes/{id}/versions/1", new { rowVersion = 1, validityDays = 15 })),
            ("price preview (D4.7)", editors, HttpStatusCode.NotFound,
                c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/versions/1/price-preview", new { validityDays = 15 })),
            ("send (D4.8)", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/versions/1/send")),
            ("recall (D4.8)", editors, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/versions/1/recall")),
            ("withdraw (D4.8)", editors, HttpStatusCode.NotFound,
                c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/versions/1/withdraw", new { reason = "Supplier price went up" })),
            ("reject approval (D4.8)", approvers, HttpStatusCode.NotFound,
                c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/versions/1/reject-approval", new { comment = "Ask for a larger quantity" })),
            ("approve (D4.8)", approvers, HttpStatusCode.NotFound, ApproveAsync),
            ("approval queue (D4.8)", approvers, HttpStatusCode.OK, c => c.GetAsync("/api/v1/admin/quotes/approvals")),
        };

        foreach (var (name, permittedRoles, allowed, call) in routes)
        {
            foreach (var role in roles)
            {
                var permitted = permittedRoles.Contains(role);
                var response = await call(people[role].Client);
                response.StatusCode.Should().Be(permitted ? allowed : HttpStatusCode.Forbidden, $"{role} on {name}");
                if (permitted) response.Headers.CacheControl!.NoStore.Should().BeTrue($"desk answers carry negotiated prices ({role} on {name})");
            }

            using var anonymous = factory.CreateClient();
            (await call(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"anonymous on {name}");
        }

        await using var db = _kit.Db();
        (await db.AuditLogs.CountAsync(a => a.Action.StartsWith("quote.") || a.Action.StartsWith("rfq."))).Should().Be(0, "no refused or missing target wrote anything");
    }
}
