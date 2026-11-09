using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Api;

/// <summary>
/// M4: an access token is a signed claim from up to 15 minutes ago. Whether the account is still active, the session still
/// open and the roles still held is read from the database on every request.
/// </summary>
public class LiveSessionTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Password = "LiveSession#2026!";
    private string ResourceUrl(Guid sellerId) => $"/api/v1/sellers/{sellerId}/resources";

    private MechwareDbContext Db() =>
        new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private async Task<(Guid Id, string Email)> NewUserAsync(string role = Permissions.Roles.Buyer, Guid? sellerId = null)
    {
        string email = $"live.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        await using var db = Db();
        var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Live Session", "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.NewGuid(), role);
        if (sellerId is { } s) user.AssignSellerId(s, DateTime.UtcNow);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, email);
    }

    private async Task<(HttpClient Client, string AccessToken)> SignInAsync(string email)
    {
        HttpClient client = factory.CreateClient(new() { HandleCookies = true, BaseAddress = new Uri("https://localhost") });   // the refresh cookie is Secure
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        string token = doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private static Task<HttpResponseMessage> Me(HttpClient client) => client.GetAsync("/api/v1/users/me");

    [PostgresFact]
    public async Task SigningOut_EndsTheAccessToken_AtOnce()
    {
        var user = await NewUserAsync();
        var (client, _) = await SignInAsync(user.Email);
        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/api/v1/auth/logout", null)).IsSuccessStatusCode.Should().BeTrue();

        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a signed-out session's access token must not live on for 15 minutes");
    }

    [PostgresFact]
    public async Task EndingOneSession_LeavesTheOthers()
    {
        var user = await NewUserAsync();
        var (laptop, _) = await SignInAsync(user.Email);
        var (phone, _) = await SignInAsync(user.Email);

        await laptop.PostAsync("/api/v1/auth/logout", null);

        (await Me(laptop)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Me(phone)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresTheory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Banned)]
    public async Task ADisabledAccount_LosesAccessOnTheNextRequest(UserStatus status)
    {
        var user = await NewUserAsync();
        var (client, _) = await SignInAsync(user.Email);

        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET status = {status.ToString()} WHERE id = {user.Id}");

        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task AnErasedAccount_LosesAccessOnTheNextRequest()
    {
        var user = await NewUserAsync();
        var (client, _) = await SignInAsync(user.Email);

        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET deleted_at = now() WHERE id = {user.Id}");

        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task ARemovedRole_StopsWorkingOnTheNextRequest_NotWhenTheTokenExpires()
    {
        var seller = await NewUserAsync(Permissions.Roles.Seller, IdentityService.SellerAlphaId);
        var (client, _) = await SignInAsync(seller.Email);
        (await client.GetAsync(ResourceUrl(IdentityService.SellerAlphaId))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM user_roles WHERE user_id = {seller.Id}");

        (await client.GetAsync(ResourceUrl(IdentityService.SellerAlphaId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [PostgresFact]
    public async Task ARoleThatDemandsAnAuthenticator_EndsAccessUntilItIsEnrolled()
    {
        var user = await NewUserAsync();
        var (client, _) = await SignInAsync(user.Email);

        await using (var db = Db())
        {
            var u = await db.Users.Include(x => x.UserRoles).SingleAsync(x => x.Id == user.Id);
            u.AssignRole(Guid.NewGuid(), Permissions.Roles.Support);
            await db.SaveChangesAsync();
        }

        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task AValidlySignedTokenForAnUnknownAccount_IsRefused()
    {
        // Signed with the server's own key, for an account that does not exist (or no longer does).
        var tokens = factory.Services.GetRequiredService<Mechware.Application.Identity.ITokenService>();
        var ghost = User.Create("ghost@mechware.com.bd", null, null, "Ghost", "en");
        string token = tokens.GenerateAccessToken(ghost, [Permissions.Roles.Admin], Permissions.GetDefaultPermissionsForRole(Permissions.Roles.Admin), DateTime.UtcNow, Guid.NewGuid());

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task ATokenForARealAccountButAnUnknownSession_IsRefused()
    {
        var user = await NewUserAsync();
        await using var db = Db();
        var entity = await db.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == user.Id);
        var tokens = factory.Services.GetRequiredService<Mechware.Application.Identity.ITokenService>();
        string token = tokens.GenerateAccessToken(entity, [Permissions.Roles.Buyer], [], DateTime.UtcNow, Guid.NewGuid());

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await Me(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task RevokingASessionByAnyOfItsTokens_EndsItsAccessToken_EvenAfterRefreshes()
    {
        var user = await NewUserAsync();
        var (laptop, _) = await SignInAsync(user.Email);
        var (phone, _) = await SignInAsync(user.Email);

        // The laptop refreshes, so its family now holds a used token and an active one.
        HttpResponseMessage refreshed = await laptop.PostAsync("/api/v1/auth/refresh", null);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument doc = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()))
            laptop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        (await Me(laptop)).StatusCode.Should().Be(HttpStatusCode.OK);

        Guid usedTokenId;
        await using (var db = Db())
            usedTokenId = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.IsUsed).Select(t => t.Id).SingleAsync();

        // From the phone, end the laptop's session by naming its older, already-used token.
        (await phone.PostAsJsonAsync("/api/v1/auth/sessions/revoke", new { sessionId = usedTokenId, revokeAll = false })).IsSuccessStatusCode.Should().BeTrue();

        (await Me(laptop)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Me(phone)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task SigningOutWithoutTheCookie_EndsOnlyThisSession()
    {
        // A bearer-only client (or a blocked cookie): the access token's session id says which session to end.
        var user = await NewUserAsync();
        var (one, tokenOne) = await SignInAsync(user.Email);
        var (two, _) = await SignInAsync(user.Email);

        HttpClient bearerOnly = factory.CreateClient(new() { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        bearerOnly.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenOne);
        (await bearerOnly.PostAsync("/api/v1/auth/logout", null)).IsSuccessStatusCode.Should().BeTrue();

        (await Me(one)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Me(two)).StatusCode.Should().Be(HttpStatusCode.OK, "signing out of one device must not sign out the others");
    }

    [PostgresFact]
    public async Task AChangedSellerLink_TakesEffectOnTheNextRequest()
    {
        var seller = await NewUserAsync(Permissions.Roles.Seller, IdentityService.SellerAlphaId);
        var (client, _) = await SignInAsync(seller.Email);
        (await client.GetAsync(ResourceUrl(IdentityService.SellerAlphaId))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET seller_id = {IdentityService.SellerBetaId} WHERE id = {seller.Id}");

        (await client.GetAsync(ResourceUrl(IdentityService.SellerAlphaId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(ResourceUrl(IdentityService.SellerBetaId))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task ASessionWhoseCurrentTokenIsRevoked_IsOver_EvenIfOlderUsedTokensWereNot()
    {
        var user = await NewUserAsync();
        var (laptop, _) = await SignInAsync(user.Email);
        HttpResponseMessage refreshed = await laptop.PostAsync("/api/v1/auth/refresh", null);
        using (JsonDocument doc = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()))
            laptop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);

        // Only the family's current (unused) token is revoked; the used one is left as it was.
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refresh_tokens SET is_revoked = TRUE WHERE user_id = {user.Id} AND is_used = FALSE");

        (await Me(laptop)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a used token cannot keep a session open");
    }

    [PostgresFact]
    public async Task EndingThisSessionWithoutKnowingWhichItIs_EndsNone_NotAll()
    {
        var user = await NewUserAsync();
        var (one, _) = await SignInAsync(user.Email);

        using (var scope = factory.Services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<Mechware.Application.Identity.IIdentityService>();
            int revoked = await identity.RevokeSessionsAsync(user.Id, new Mechware.Application.Identity.RevokeSessionsRequestDto(RevokeAll: false));
            revoked.Should().Be(0);
        }

        (await Me(one)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
