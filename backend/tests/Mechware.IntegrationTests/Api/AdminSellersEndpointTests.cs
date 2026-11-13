using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Api;

/// <summary>E3b: the staff seller picker. Closed to anonymous callers and buyers; approved by default; id, store name and status only.</summary>
public sealed class AdminSellersEndpointTests : IDisposable
{
    private static readonly TotpService Totp = new();
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<HttpClient> SignedInAsync(string email, string password, string? secret)
    {
        HttpClient client = _factory.CreateClient();
        object body = secret is null
            ? new { identifier = email, password }
            : new { identifier = email, password, totpCode = Totp.ComputeTotpCode(secret, DateTimeOffset.UtcNow) };
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login", body);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    private Task<HttpClient> AdminAsync() => SignedInAsync("admin@mechware.com.bd", "MechAdmin#2026!", "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP");

    private async Task<(string Tag, Guid Zeta, Guid Alpha, Guid Pending)> SeedAsync()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Db();
        Seller Make(string name, bool approve)
        {
            var user = Mechware.Domain.Identity.User.Create($"s-{Guid.NewGuid():N}@example.test", null, null, "Owner Person");
            db.Users.Add(user);
            var s = new Seller(Guid.NewGuid(), user.Id, name, "sl-" + Guid.NewGuid().ToString("N")[..12], null, null, null, 4m, 1, 99m, DateTime.UtcNow);
            if (approve) s.Approve(DateTime.UtcNow);
            db.Sellers.Add(s);
            return s;
        }

        var zeta = Make($"Zeta Works {tag}", true);
        var alpha = Make($"Alpha Works {tag}", true);
        var pending = Make($"Pending Works {tag}", false);
        await db.SaveChangesAsync();
        return (tag, zeta.Id, alpha.Id, pending.Id);
    }

    [PostgresFact]
    public async Task Only_staff_with_catalog_manage_may_list_sellers()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/admin/sellers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var buyer = await SignedInAsync("buyer@mechware.com.bd", "BuyerPass#2026!", null);
        (await buyer.GetAsync("/api/v1/admin/sellers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var admin = await AdminAsync();
        (await admin.GetAsync("/api/v1/admin/sellers")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task The_list_is_approved_by_default_filters_by_name_and_status_pages_and_carries_no_personal_data()
    {
        var (tag, zeta, alpha, pending) = await SeedAsync();
        using var admin = await AdminAsync();

        async Task<(HttpStatusCode Status, string Raw, JsonElement Data)> GetAsync(string query)
        {
            using var r = await admin.GetAsync("/api/v1/admin/sellers" + query);
            string raw = await r.Content.ReadAsStringAsync();
            return (r.StatusCode, raw, r.IsSuccessStatusCode ? JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone() : default);
        }

        var byName = await GetAsync($"?q={tag}");
        var items = byName.Data.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).Should().Equal(alpha, zeta);   // ordered by store name, pending left out
        items.Should().OnlyContain(i => i.GetProperty("status").GetString() == "approved");
        items[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "storeName", "status"]);
        byName.Raw.Should().NotContain("example.test").And.NotContain("Owner Person").And.NotContain("userId");

        var pend = await GetAsync($"?status=pending&q={tag}");
        pend.Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(pending);

        var like = await GetAsync($"?q=%25{tag}");   // a literal '%' is not a wildcard
        like.Data.GetProperty("totalCount").GetInt32().Should().Be(0);

        var paged = await GetAsync($"?q={tag}&page=2&pageSize=1");
        paged.Data.GetProperty("totalCount").GetInt32().Should().Be(2);
        paged.Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(zeta);

        (await GetAsync("?pageSize=100000")).Data.GetProperty("pageSize").GetInt32().Should().Be(50);
        (await GetAsync("?status=bogus")).Status.Should().Be(HttpStatusCode.BadRequest);
    }
}
