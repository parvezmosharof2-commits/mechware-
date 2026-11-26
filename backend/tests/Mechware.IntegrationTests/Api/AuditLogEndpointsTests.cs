using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Audit;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;
using Mechware.IntegrationTests.Quotes;
using Mechware.IntegrationTests.Support;
using Xunit.Abstractions;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;

namespace Mechware.IntegrationTests.Api;

/// <summary>
/// G5.2b over HTTP: <c>GET /api/v1/admin/audit</c> (filters, paging, Dhaka dates, newest first) and <c>GET /api/v1/admin/audit/{id}</c>
/// (redacted values, nothing decrypted), both behind audit.view, <c>private, no-store</c>, and each read audited itself.
/// Rows are written straight into audit_logs, each test under its own entity name, so tests never see each other's rows.
/// </summary>
public sealed class AuditLogEndpointsTests(QuoteIssueFactory factory, ITestOutputHelper output) : IClassFixture<QuoteIssueFactory>
{
    private readonly QuoteIssueKit _kit = new(factory);

    private static readonly HashSet<string> ListFields =
        ["id", "createdAtUtc", "actorId", "actorEmail", "actorIp", "action", "entityName", "entityId", "correlationId", "chainPosition"];

    private static string Tag() => "t_" + Guid.NewGuid().ToString("N")[..12];

    private async Task<Guid> RowAsync(string entityName, DateTime atUtc, Guid? actor = null, string action = "test.happened", string entityId = "e1",
        string? correlationId = null, string? oldJson = null, string? newJson = null)
    {
        await using var db = _kit.Db();
        var id = Guid.NewGuid();
        db.AuditLogs.Add(new AuditLog(id, actor, actor is null ? null : "198.51.100.7", action, entityName, entityId, oldJson, newJson, correlationId,
            DateTime.SpecifyKind(atUtc, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static void NoStore(HttpResponseMessage r)
    {
        r.Headers.CacheControl!.NoStore.Should().BeTrue();
        r.Headers.CacheControl.Private.Should().BeTrue();
    }

    private static async Task<List<Guid>> IdsAsync(HttpClient c, string query)
    {
        var data = await DataAsync(await c.GetAsync("/api/v1/admin/audit" + query));
        return [.. data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }

    // ---- permission matrix ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Permission_matrix_anonymous_401_without_audit_view_403_finance_and_admin_200()
    {
        var row = await RowAsync(Tag(), DateTime.UtcNow);
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/admin/audit")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/v1/admin/audit/{row}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator, Permissions.Roles.Support })
        {
            var who = await _kit.StaffAsync(role, "apm-" + role);
            (await who.Client.GetAsync("/api/v1/admin/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await who.Client.GetAsync($"/api/v1/admin/audit/{row}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        foreach (var role in new[] { Permissions.Roles.Finance, Permissions.Roles.Admin })
        {
            var who = await _kit.StaffAsync(role, "apm-" + role);
            (await who.Client.GetAsync("/api/v1/admin/audit?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK, role);
            (await who.Client.GetAsync($"/api/v1/admin/audit/{row}")).StatusCode.Should().Be(HttpStatusCode.OK, role);
        }
    }

    // ---- list -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Every_filter_narrows_the_list_newest_first_with_the_actor_email_and_only_list_fields()
    {
        var admin = await _kit.StaffAsync(Permissions.Roles.Admin, "filter-admin");
        var a = await _kit.StaffAsync(Permissions.Roles.Support, "filter-a");
        var b = await _kit.StaffAsync(Permissions.Roles.Finance, "filter-b");
        var tag = Tag();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        var r1 = await RowAsync(tag, t0, a.Id, "x.one", "e1", "c-1");
        var r2 = await RowAsync(tag, t0.AddMinutes(1), b.Id, "x.two", "e2", "c-2");
        var r3 = await RowAsync(tag, t0.AddMinutes(2), null, "x.one", "e2");

        (await IdsAsync(admin.Client, $"?entityName={tag}")).Should().Equal(r3, r2, r1);
        (await IdsAsync(admin.Client, $"?entityName={tag}&actorId={a.Id}")).Should().Equal(r1);
        (await IdsAsync(admin.Client, $"?entityName={tag}&action=x.one")).Should().Equal(r3, r1);
        (await IdsAsync(admin.Client, $"?entityName={tag}&entityId=e2")).Should().Equal(r3, r2);
        (await IdsAsync(admin.Client, $"?entityName={tag}&correlationId=c-2")).Should().Equal(r2);
        (await IdsAsync(admin.Client, $"?entityName={tag}&action=x.two&entityId=e1")).Should().BeEmpty();
        (await IdsAsync(admin.Client, $"?actorId={a.Id}&entityName={tag}&action=x.one&entityId=e1&correlationId=c-1")).Should().Equal(r1);

        var response = await admin.Client.GetAsync($"/api/v1/admin/audit?entityName={tag}");
        NoStore(response);
        var data = await DataAsync(response);
        data.GetProperty("totalCount").GetInt32().Should().Be(3);
        var items = data.GetProperty("items").EnumerateArray().ToList();
        foreach (var item in items) item.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(ListFields);
        await using (var db = _kit.Db())
        {
            var emailB = await db.Users.Where(u => u.Id == b.Id).Select(u => u.Email).SingleAsync();
            items[1].GetProperty("actorEmail").GetString().Should().Be(emailB);
        }

        items[1].GetProperty("actorIp").GetString().Should().Be("198.51.100.7");
        items[1].GetProperty("chainPosition").ValueKind.Should().Be(JsonValueKind.Null, "not sealed yet");
        items[0].GetProperty("actorEmail").ValueKind.Should().Be(JsonValueKind.Null, "a system row has no actor");
    }

    [PostgresFact]
    public async Task From_and_to_are_inclusive_Dhaka_days_and_bad_filters_are_400()
    {
        var admin = await _kit.StaffAsync(Permissions.Roles.Admin, "date-admin");
        var tag = Tag();
        var lastOf10 = await RowAsync(tag, new DateTime(2026, 3, 10, 17, 59, 59));              // 2026-03-10 23:59:59 Dhaka
        var firstOf11 = await RowAsync(tag, new DateTime(2026, 3, 10, 18, 0, 0));              // 2026-03-11 00:00:00 Dhaka
        var lastOf11 = await RowAsync(tag, new DateTime(2026, 3, 11, 17, 59, 59, 999));        // 2026-03-11 23:59:59.999 Dhaka
        var firstOf12 = await RowAsync(tag, new DateTime(2026, 3, 11, 18, 0, 0));              // 2026-03-12 00:00:00 Dhaka

        (await IdsAsync(admin.Client, $"?entityName={tag}&from=2026-03-11&to=2026-03-11")).Should().Equal(lastOf11, firstOf11);
        (await IdsAsync(admin.Client, $"?entityName={tag}&to=2026-03-10")).Should().Equal(lastOf10);
        (await IdsAsync(admin.Client, $"?entityName={tag}&from=2026-03-12")).Should().Equal(firstOf12);
        (await IdsAsync(admin.Client, $"?entityName={tag}&from=2026-03-10&to=2026-03-12")).Should().HaveCount(4);

        foreach (var bad in new[] { "from=2026-13-01", "to=11/03/2026", "from=2026-03-12&to=2026-03-11", "action=" + new string('a', 101),
                     "entityName=" + new string('b', 101), "actorId=not-a-guid" })
            (await admin.Client.GetAsync($"/api/v1/admin/audit?{bad}")).StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
    }

    [PostgresFact]
    public async Task Page_size_is_capped_at_50_and_pages_do_not_overlap()
    {
        var admin = await _kit.StaffAsync(Permissions.Roles.Admin, "page-admin");
        var tag = Tag();
        var t0 = DateTime.UtcNow.AddHours(-2);
        await using (var db = _kit.Db())
        {
            for (var i = 0; i < 60; i++)
                db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), null, null, "test.paged", tag, i.ToString(), null, null, null, t0.AddSeconds(i)));
            await db.SaveChangesAsync();
        }

        var first = await DataAsync(await admin.Client.GetAsync($"/api/v1/admin/audit?entityName={tag}&pageSize=500"));
        first.GetProperty("pageSize").GetInt32().Should().Be(IAuditLogReadService.MaxPageSize);
        first.GetProperty("totalCount").GetInt32().Should().Be(60);
        var items = first.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(50);
        items.Select(i => i.GetProperty("createdAtUtc").GetDateTime()).Should().BeInDescendingOrder();
        items[0].GetProperty("entityId").GetString().Should().Be("59");

        var second = await DataAsync(await admin.Client.GetAsync($"/api/v1/admin/audit?entityName={tag}&pageSize=50&page=2"));
        var rest = second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        rest.Should().HaveCount(10).And.NotIntersectWith(items.Select(i => i.GetProperty("id").GetGuid()));

        var tiny = await DataAsync(await admin.Client.GetAsync($"/api/v1/admin/audit?entityName={tag}&pageSize=0&page=0"));
        tiny.GetProperty("pageSize").GetInt32().Should().Be(1);
        tiny.GetProperty("page").GetInt32().Should().Be(1);
        tiny.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---- detail ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Detail_redacts_secrets_and_never_decrypts_and_is_no_store()
    {
        var finance = await _kit.StaffAsync(Permissions.Roles.Finance, "detail-finance");
        var bin = "8765432109876";
        var cipher = FieldEncryption.Current.Protect(bin);
        var before = $$$"""{"Status":"Draft","PasswordHash":"AQAAAAIAAYagAAAAEhash","TwoFactorSecret":"{{{cipher}}}","Reference":"TT-55667788"}""";
        var after = $$$"""
            {"Status":"Sent","Email":"buyer@example.test","Bin":"{{{bin}}}","Note":"{{{cipher}}}","CompanySnapshot":{"Name":"Acme Ltd","Bin":"{{{cipher}}}"},
             "Lines":[{"Sku":"B-1","otp":"482913"}],"TotpCode":"123456","RefreshToken":"rt-abcdef","IdempotencyKey":"idem-77","PostalCode":"1710"}
            """;
        var id = await RowAsync(Tag(), DateTime.UtcNow, finance.Id, "test.detail", "e9", "corr-9", before, after);

        var response = await finance.Client.GetAsync($"/api/v1/admin/audit/{id}");
        NoStore(response);
        var raw = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { bin, cipher, "enc:", "AQAAAAIAAYagAAAAEhash", "TT-55667788", "482913", "123456", "rt-abcdef", "idem-77" })
            raw.Should().NotContain(secret);

        var data = await DataAsync(response);
        data.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo([.. ListFields, "oldValues", "newValues"]);
        data.GetProperty("action").GetString().Should().Be("test.detail");
        data.GetProperty("correlationId").GetString().Should().Be("corr-9");
        var o = data.GetProperty("oldValues");
        var n = data.GetProperty("newValues");
        o.GetProperty("Status").GetString().Should().Be("Draft");
        o.GetProperty("PasswordHash").GetString().Should().Be(AuditRedaction.Hidden);
        o.GetProperty("TwoFactorSecret").GetString().Should().Be(AuditRedaction.Hidden);
        o.GetProperty("Reference").GetString().Should().Be(AuditRedaction.Hidden);
        n.GetProperty("Email").GetString().Should().Be("buyer@example.test");
        n.GetProperty("PostalCode").GetString().Should().Be("1710");
        n.GetProperty("Bin").GetString().Should().Be(AuditRedaction.Hidden);
        n.GetProperty("Note").GetString().Should().Be(AuditRedaction.Hidden);
        n.GetProperty("CompanySnapshot").GetProperty("Name").GetString().Should().Be("Acme Ltd");
        n.GetProperty("CompanySnapshot").GetProperty("Bin").GetString().Should().Be(AuditRedaction.Hidden);
        n.GetProperty("Lines")[0].GetProperty("otp").GetString().Should().Be(AuditRedaction.Hidden);

        var empty = await DataAsync(await finance.Client.GetAsync($"/api/v1/admin/audit/{await RowAsync(Tag(), DateTime.UtcNow)}"));
        empty.GetProperty("oldValues").ValueKind.Should().Be(JsonValueKind.Null);
        empty.GetProperty("newValues").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task Real_rows_of_a_company_and_a_sent_quote_never_show_the_BIN_in_clear_or_encrypted()
    {
        var admin = await _kit.StaffAsync(Permissions.Roles.Admin, "bin-admin");
        var support = await _kit.StaffAsync(Permissions.Roles.Support, "bin-support");
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Audit Bin Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SentAsync(support.Client, quote, "sent");

        string bin;
        List<(Guid Id, bool PlainInDb)> rows;
        await using (var db = _kit.Db())
        {
            bin = await db.Companies.Where(c => c.Id == w.Company).Select(c => c.Bin).SingleAsync();
            var versions = await db.Database.SqlQuery<string>($"SELECT id::text AS \"Value\" FROM quote_versions WHERE quote_id = {quote}").ToListAsync();
            var entityIds = versions.Append(w.Company.ToString()).ToList();
            rows = (await db.AuditLogs.Where(a => entityIds.Contains(a.EntityId)).ToListAsync())
                .Select(a => (a.Id, ((a.OldValuesJson ?? "") + (a.NewValuesJson ?? "")).Contains(bin, StringComparison.Ordinal))).ToList();
        }

        rows.Should().NotBeEmpty();
        output.WriteLine($"audit rows of the company and the quote versions: {rows.Count}; holding the BIN in clear in the database: {rows.Count(r => r.PlainInDb)}");
        foreach (var (id, _) in rows)
        {
            var raw = await (await admin.Client.GetAsync($"/api/v1/admin/audit/{id}")).Content.ReadAsStringAsync();
            raw.Should().Contain("\"id\"").And.NotContain(bin).And.NotContain(FieldProtector.Prefix);
        }
    }

    [PostgresFact]
    public async Task Unknown_id_is_404_and_every_read_is_itself_audited()
    {
        var admin = await _kit.StaffAsync(Permissions.Roles.Admin, "self-admin");
        (await admin.Client.GetAsync($"/api/v1/admin/audit/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Client.GetAsync("/api/v1/admin/audit/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var tag = Tag();
        var row = await RowAsync(tag, DateTime.UtcNow);
        (await admin.Client.GetAsync($"/api/v1/admin/audit?entityName={tag}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"/api/v1/admin/audit/{row}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _kit.Db();
        var own = await db.AuditLogs.Where(a => a.ActorId == admin.Id && a.EntityName == "audit_logs").ToListAsync();
        own.Should().Contain(a => a.Action == IAuditLogReadService.ViewedAction && a.EntityId == row.ToString());
        own.Should().Contain(a => a.Action == IAuditLogReadService.SearchedAction && a.NewValuesJson!.Contains(tag));
        own.Should().OnlyContain(a => a.ActorIp == QuoteIssueFactory.ClientIp);
    }
}
