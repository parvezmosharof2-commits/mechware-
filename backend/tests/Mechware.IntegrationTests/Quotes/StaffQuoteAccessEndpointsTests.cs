using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.6 (+ D4.7 editor routes) permission matrix over HTTP (threat model QT1 `StaffQuoteEndpoints_WithoutPermission_Are403`): every staff desk route for
/// Support, Finance, Admin, Moderator, Seller and Buyer with real sign-ins, and anonymous. Reads need <c>quotes.view</c> (Support,
/// Finance, Admin), commands <c>quotes.edit</c> (Support, Admin). Commands run on ids that do not exist, so a permitted role reaches the
/// service (404) and nothing is written. Its own class, so its own login budget.
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
        var (id, company, contact) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var routes = new (string Name, bool Edit, HttpStatusCode Allowed, Func<HttpClient, Task<HttpResponseMessage>> Call)[]
        {
            ("list rfqs", false, HttpStatusCode.OK, c => c.GetAsync("/api/v1/admin/rfqs")),
            ("rfq detail", false, HttpStatusCode.NotFound, c => c.GetAsync($"/api/v1/admin/rfqs/{id}")),
            ("assign rfq", true, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/rfqs/{id}/assign", new { assigneeId = (Guid?)null })),
            ("close rfq", true, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/rfqs/{id}/close", new { reason = "No bid today" })),
            ("create quote", true, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, "/api/v1/admin/quotes", new { companyId = company, contactUserId = contact })),
            ("list quotes", false, HttpStatusCode.OK, c => c.GetAsync("/api/v1/admin/quotes")),
            ("quote detail", false, HttpStatusCode.NotFound, c => c.GetAsync($"/api/v1/admin/quotes/{id}")),
            ("revise quote", true, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/revise")),
            ("close quote", true, HttpStatusCode.NotFound, c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/close", new { reason = "Duplicate quote" })),
            ("edit draft (D4.7)", true, HttpStatusCode.NotFound, c => c.PutAsJsonAsync($"/api/v1/admin/quotes/{id}/versions/1", new { rowVersion = 1, validityDays = 15 })),
            ("price preview (D4.7)", true, HttpStatusCode.NotFound,
                c => StaffQuoteKit.PostAsync(c, $"/api/v1/admin/quotes/{id}/versions/1/price-preview", new { validityDays = 15 })),
        };

        foreach (var (name, edit, allowed, call) in routes)
        {
            foreach (var role in roles)
            {
                var permitted = (edit ? editors : viewers).Contains(role);
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
