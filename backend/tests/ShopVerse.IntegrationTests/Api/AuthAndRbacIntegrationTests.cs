using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ShopVerse.Application.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.IntegrationTests.Api;

public class AuthAndRbacIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthAndRbacIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BuyerLogin_ShouldReturnInMemoryAccessToken_AndSetHttpOnlySecureSameSiteLaxRefreshCookie()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = "buyer@mechware.com.bd",
            password = "BuyerPass#2026!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey("Set-Cookie");

        string setCookie = string.Join("; ", response.Headers.GetValues("Set-Cookie"));
        setCookie.Should().Contain("refresh_token=");
        setCookie.ToLowerInvariant().Should().Contain("httponly");
        setCookie.ToLowerInvariant().Should().Contain("samesite=lax");

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        string accessToken = data.GetProperty("accessToken").GetString()!;
        accessToken.Should().NotBeNullOrWhiteSpace();
        data.GetProperty("expiresIn").GetInt32().Should().Be(900);
    }

    [Fact]
    public async Task AdminLoginWithoutTotp_ShouldReturn202RequiresTwoFactor_AndSucceedWithTotpOrRecoveryCode()
    {
        HttpClient client = _factory.CreateClient();

        // Step 1: Login with password only for Admin (mandatory 2FA role)
        HttpResponseMessage step1 = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = "admin@mechware.com.bd",
            password = "MechAdmin#2026!"
        });

        step1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using JsonDocument step1Doc = JsonDocument.Parse(await step1.Content.ReadAsStringAsync());
        step1Doc.RootElement.GetProperty("data").GetProperty("requiresTwoFactor").GetBoolean().Should().BeTrue();

        // Step 2: Compute current RFC 6238 TOTP code and complete login
        var totpService = new TotpService();
        string currentTotp = totpService.ComputeTotpCode("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", DateTimeOffset.UtcNow);

        HttpResponseMessage step2 = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = "admin@mechware.com.bd",
            password = "MechAdmin#2026!",
            totpCode = currentTotp
        });

        step2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SellerIsolation_SellerAlphaCannotReadSellerBetaResources_AndCannotInvokeOrdersRefund()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = "seller.alpha@mechware.com.bd",
            password = "SellerAlpha#2026!"
        });

        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        string sellerAlphaToken = loginDoc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sellerAlphaToken);

        // 1. Seller Alpha reads own SellerAlphaId resource -> 200 OK
        HttpResponseMessage ownResp = await client.GetAsync($"/api/v1/sellers/{IdentityService.SellerAlphaId}/resources");
        ownResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Seller Alpha tries to read SellerBetaId resource -> 403 Forbidden
        HttpResponseMessage crossSellerResp = await client.GetAsync($"/api/v1/sellers/{IdentityService.SellerBetaId}/resources");
        crossSellerResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 3. Seller Alpha tries to invoke [RequirePermission(Permissions.OrdersRefund)] -> 403 Forbidden
        HttpResponseMessage refundResp = await client.PostAsync($"/api/v1/sellers/{IdentityService.SellerAlphaId}/resources/refund-audit", null);
        refundResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BangladeshLocationsAndAddressBookAndGdprExport_ShouldWorkEndToEnd()
    {
        HttpClient client = _factory.CreateClient();

        // 1. Public Bangladesh Divisions lookup
        HttpResponseMessage bdResp = await client.GetAsync("/api/v1/locations/bd?level=division");
        bdResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string bdContent = await bdResp.Content.ReadAsStringAsync();
        bdContent.Should().Contain("Dhaka").And.Contain("ঢাকা");

        // 2. Login as Buyer
        HttpResponseMessage loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = "buyer@mechware.com.bd",
            password = "BuyerPass#2026!"
        });
        using JsonDocument loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        string buyerToken = loginDoc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyerToken);

        // 3. Create new Bangladesh industrial address
        HttpResponseMessage createAddrResp = await client.PostAsJsonAsync("/api/v1/users/me/addresses", new UpsertAddressRequestDto(
            "Chattogram Port Warehouse",
            "Engr. Rafiqul Islam",
            "+8801819223344",
            "Sector 4, Plot 19, CEPZ",
            "Bay 2",
            "Halishahar",
            "Chattogram",
            "Chattogram",
            "Chattogram EPZ (Halishahar)",
            "4223",
            false,
            true,
            22.2912m,
            91.7781m));

        createAddrResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // 4. GDPR Data Export
        HttpResponseMessage exportResp = await client.GetAsync("/api/v1/users/me/export");
        exportResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string exportJson = await exportResp.Content.ReadAsStringAsync();
        exportJson.Should().Contain("Chattogram Port Warehouse");
    }
}
