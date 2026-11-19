using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>The host the RFQ HTTP tests run on: a roomy per-user limit, so only the test that wants to hit it does.</summary>
public sealed class RfqApiFactory() : DbBackedFactory(null, new Dictionary<string, string>
{
    ["RateLimiting:QuoteRequestsPermitPerMinute"] = "10000",
    ["Quotes:MaxRfqsPerCompanyPerHour"] = "1000",
    ["Quotes:MaxOpenRfqsPerCompany"] = "1000",
});

/// <summary>Real accounts, real companies and real sign-ins for the buyer RFQ tests; each test class has its own host and so its own login budget (25 a minute).</summary>
public sealed class RfqApiKit(DbBackedFactory factory)
{
    public const string Password = "RfqBuyer#2026!";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private static long _binSeq = 3_970_000_000;

    public sealed record Person(Guid Id, HttpClient Client);

    public MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    public async Task<Person> PersonAsync(string role = Permissions.Roles.Buyer)
    {
        factory.CreateClient().Dispose();   // starts the host (its Development seeding runs once) before the account is written
        var email = $"rfq.{Guid.NewGuid():N}"[..22] + "@example.test";
        var staff = role != Permissions.Roles.Buyer;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Rfq " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            if (staff)
            {
                user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
                user.EnableTwoFactor(DateTime.UtcNow);
            }

            db.Users.Add(user);
            db.Addresses.Add(TestUsers.DefaultAddress(user.Id));
            await db.SaveChangesAsync();
            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", staff
                ? new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) }
                : (object)new { identifier = email, password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
            return new Person(user.Id, client);
        }
    }

    public async Task<Guid> CompanyAsync(Guid admin, string name, bool activate = true, params (Guid User, CompanyRoles Roles)[] members)
    {
        await using var db = Db();
        var companies = new CompanyService(db);
        var bin = (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
        var id = (await companies.RegisterAsync(admin, new RegisterCompanyRequest(name, "TL-RFQ-" + bin[^5..], bin, null, null))).Id;
        foreach (var (user, roles) in members) await companies.AddAcceptedMemberAsync(admin, id, new AddMemberRequest(user, roles, null, null));
        if (activate) await companies.AdminActivateAsync(id);
        return id;
    }

    /// <summary>One sellable catalogue variant, with the given manufacturer part number.</summary>
    public async Task<(Guid ProductId, Guid VariantId, string Sku)> VariantAsync(string? partNumber)
    {
        await using var db = Db();
        var s = await CatalogTestData.SeedVariantAsync(db, 100);
        var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == s.ProductId);
        var v = p.Variants.Single();
        if (partNumber is not null) p.SetManufacturerPartNumber(v.Id, partNumber);
        await db.SaveChangesAsync();
        return (p.Id, v.Id, v.Sku);
    }

    public static string Key() => "rfq-" + Guid.NewGuid().ToString("N");

    public static string Url(Guid company, string tail = "") => $"/api/v1/companies/{company}/rfqs{tail}";

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid company, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(company))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    public static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    public static async Task<(string Code, string Message)> ErrorAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        var error = JsonDocument.Parse(text).RootElement.GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    /// <summary>A refusal that must look the same for everyone: private, never stored, and one code and message.</summary>
    public static async Task<string> NotFoundShapeAsync(HttpResponseMessage r, string because)
    {
        var (code, message) = await ErrorAsync(r, HttpStatusCode.NotFound);
        r.Headers.CacheControl?.NoStore.Should().BeTrue("also a refusal is not kept: " + because);
        r.Headers.Vary.Should().Contain("Authorization");
        return code + "|" + message;
    }

    public static void ShouldBePrivate(HttpResponseMessage r)
    {
        r.Headers.CacheControl!.NoStore.Should().BeTrue();
        r.Headers.CacheControl.Private.Should().BeTrue();
        r.Headers.Vary.Should().Contain("Authorization");
    }
}
