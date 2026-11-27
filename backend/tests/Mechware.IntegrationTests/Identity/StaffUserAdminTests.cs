using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Identity;

public sealed class StaffUsersFactory() : DbBackedFactory();

/// <summary>
/// G5.7 staff users and roles over HTTP (and the last-admin guard on the service): users.manage only, step-up on every write but a
/// rejection, four eyes on role changes, nobody changes their own roles or disables themselves, the last administrator stays,
/// disabling revokes the refresh tokens at once, and no hash or secret leaves the API.
/// </summary>
public sealed class StaffUserAdminTests(StaffUsersFactory factory) : IClassFixture<StaffUsersFactory>
{
    private const string Password = "StaffUsers#2026!";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Person(Guid Id, string Email, HttpClient Client);

    private async Task<(Guid Id, string Email)> NewAsync(params string[] roles)
    {
        factory.CreateClient().Dispose();   // the host (and its Development seeding) starts before the account is written
        var email = $"g57.{Guid.NewGuid():N}"[..20] + "@example.test";
        await using var db = Db();
        var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Staff " + string.Join("+", roles), "en");
        user.VerifyEmail(DateTime.UtcNow);
        foreach (var role in roles) user.AssignRole(Guid.NewGuid(), role);
        user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
        user.EnableTwoFactor(DateTime.UtcNow);
        db.Users.Add(user);
        db.Addresses.Add(TestUsers.DefaultAddress(user.Id));
        await db.SaveChangesAsync();
        return (user.Id, email);
    }

    /// <summary>Signs in over https so the Secure refresh cookie is kept by the client.</summary>
    private async Task<Person> SignInAsync((Guid Id, string Email) who)
    {
        var client = factory.CreateClient(new() { HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = who.Email, password = Password, totpCode = await CodeAsync(who.Id) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(who.Id, who.Email, client);
    }

    private async Task<Person> StaffAsync(params string[] roles) => await SignInAsync(await NewAsync(roles));

    /// <summary>The current authenticator code, usable once more (the replay guard is cleared first).</summary>
    private async Task<string> CodeAsync(Guid userId)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient c, HttpMethod method, string url, object? body = null, string? code = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return await c.SendAsync(request);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private Task<HttpResponseMessage> RequestRolesAsync(Person who, Guid target, string[] roles, string? code) =>
        SendAsync(who.Client, HttpMethod.Put, $"/api/v1/admin/users/{target}/roles", new { roles, reason = "Moves to the finance desk" }, code);

    private static string[] Roles(JsonElement user) => [.. user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)];

    // ---- permission matrix ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Permission_matrix_anonymous_401_every_role_but_admin_403_admin_reads()
    {
        var target = await NewAsync(Permissions.Roles.Support);
        var change = Guid.NewGuid();
        (HttpMethod Method, string Url)[] endpoints =
        [
            (HttpMethod.Get, "/api/v1/admin/users"), (HttpMethod.Get, $"/api/v1/admin/users/{target.Id}"), (HttpMethod.Get, "/api/v1/admin/roles"),
            (HttpMethod.Get, "/api/v1/admin/role-changes"), (HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/disable"),
            (HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/enable"), (HttpMethod.Put, $"/api/v1/admin/users/{target.Id}/roles"),
            (HttpMethod.Post, $"/api/v1/admin/role-changes/{change}/approve"), (HttpMethod.Post, $"/api/v1/admin/role-changes/{change}/reject")
        ];
        object body = new { reason = "matrix check", roles = new[] { "finance" } };

        using var anonymous = factory.CreateClient();
        foreach (var (method, url) in endpoints)
            (await SendAsync(anonymous, method, url, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {url}");

        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Moderator })
        {
            var who = await StaffAsync(role);
            foreach (var (method, url) in endpoints)
                (await SendAsync(who.Client, method, url, body, "123456")).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role}: {method} {url}");
        }

        var admin = await StaffAsync(Permissions.Roles.Admin);
        foreach (var (method, url) in endpoints.Where(e => e.Method == HttpMethod.Get))
            (await SendAsync(admin.Client, method, url)).StatusCode.Should().Be(HttpStatusCode.OK, url);

        var roles = (await DataAsync(await admin.Client.GetAsync("/api/v1/admin/roles"))).EnumerateArray().ToList();
        roles.Select(r => r.GetProperty("name").GetString()).Should().Equal(StaffRoles.All);
        roles.Single(r => r.GetProperty("name").GetString() == Permissions.Roles.Admin).GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()).Should().BeEquivalentTo(Permissions.GetDefaultPermissionsForRole(Permissions.Roles.Admin));
    }

    // ---- disable / enable -----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Disabling_needs_a_code_never_yourself_and_ends_access_and_refresh_tokens_at_once_even_after_enable()
    {
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var target = await StaffAsync(Permissions.Roles.Support);
        var disable = $"/api/v1/admin/users/{target.Id}/disable";
        object reason = new { reason = "Left the company" };

        (await ErrorAsync(await SendAsync(admin.Client, HttpMethod.Post, disable, reason), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorAsync(await SendAsync(admin.Client, HttpMethod.Post, $"/api/v1/admin/users/{admin.Id}/disable", reason, await CodeAsync(admin.Id)),
            HttpStatusCode.Conflict)).Should().Be("USER_DISABLE_SELF");
        (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var disabled = await DataAsync(await SendAsync(admin.Client, HttpMethod.Post, disable, reason, await CodeAsync(admin.Id)));
        disabled.GetProperty("status").GetString().Should().Be("suspended");

        (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the access token dies on the next request");
        (await target.Client.PostAsync("/api/v1/auth/refresh", null)).IsSuccessStatusCode.Should().BeFalse("a disabled account refreshes nothing");
        await using (var db = Db())
        {
            (await db.RefreshTokens.CountAsync(t => t.UserId == target.Id)).Should().BeGreaterThan(0);
            (await db.RefreshTokens.AnyAsync(t => t.UserId == target.Id && !t.IsRevoked)).Should().BeFalse("every refresh token is revoked");
            (await db.AuditLogs.CountAsync(a => a.Action == "user.disabled" && a.EntityId == target.Id.ToString() && a.ActorId == admin.Id)).Should().Be(1);
        }

        var enabled = await DataAsync(await SendAsync(admin.Client, HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/enable", new { }, await CodeAsync(admin.Id)));
        enabled.GetProperty("status").GetString().Should().Be("active");
        (await target.Client.PostAsync("/api/v1/auth/refresh", null)).IsSuccessStatusCode.Should().BeFalse("an old session never comes back");
        (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SignInAsync((target.Id, target.Email))).Client.Should().NotBeNull("a fresh sign-in works again");
    }

    // ---- last admin (service: an admin calling over HTTP is always another admin, so a race is what the guard stops) -------

    private sealed class Actor(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.9";
        public string? UserAgent => null;
        public string? CorrelationId => "g57-last-admin";
        public bool HasPermission(string permission) => true;
    }

    [PostgresFact]
    public async Task The_last_active_admin_is_never_disabled_or_demoted()
    {
        var target = await NewAsync(Permissions.Roles.Admin);
        var other = await NewAsync(Permissions.Roles.Admin);
        var (asker, approver) = (Guid.NewGuid(), Guid.NewGuid());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MechwareDbContext>();
        StaffUserService As(Guid id) => new(db, new Actor(id), TimeProvider.System);

        // While another admin exists the demotion may be asked for; then the other admin goes, and the approval is refused.
        var change = await As(asker).RequestRoleChangeAsync(target.Id, new StaffRoleChangeRequest([Permissions.Roles.Finance], "Moves to finance"));

        List<Guid> suspended;
        await using (var raw = Db())
        {
            suspended = await raw.Users.Where(u => u.Id != target.Id && u.Status == UserStatus.Active && u.UserRoles.Any(r => r.RoleName == "admin"))
                .Select(u => u.Id).ToListAsync();
            await raw.Users.Where(u => suspended.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Suspended));
        }
        suspended.Should().Contain(other.Id);
        try
        {
            var disable = () => As(asker).DisableAsync(target.Id, new StaffUserReasonRequest("Left the company"));
            (await disable.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("LAST_ADMIN");
            db.ChangeTracker.Clear();
            var approve = () => As(approver).ApproveRoleChangeAsync(change.Id);
            (await approve.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("LAST_ADMIN");
            db.ChangeTracker.Clear();

            await using var check = Db();
            var after = await check.Users.Include(u => u.UserRoles).SingleAsync(u => u.Id == target.Id);
            after.Status.Should().Be(UserStatus.Active);
            after.UserRoles.Select(r => r.RoleName).Should().Equal(Permissions.Roles.Admin);
            (await check.RefreshTokens.AnyAsync(t => t.UserId == target.Id && t.IsRevoked)).Should().BeFalse();
        }
        finally
        {
            await using var raw = Db();
            await raw.Users.Where(u => suspended.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Active));
            await raw.StaffRoleChanges.Where(c => c.Id == change.Id).ExecuteDeleteAsync();
        }
    }

    // ---- four eyes and self-elevation -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_role_change_needs_a_code_and_another_admin_and_changes_nothing_until_approved()
    {
        var adminA = await StaffAsync(Permissions.Roles.Admin);
        var adminB = await StaffAsync(Permissions.Roles.Admin);
        var target = await NewAsync(Permissions.Roles.Support, Permissions.Roles.Buyer);

        (await ErrorAsync(await RequestRolesAsync(adminA, target.Id, [Permissions.Roles.Finance], null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        var asked = await DataAsync(await RequestRolesAsync(adminA, target.Id, [Permissions.Roles.Finance], await CodeAsync(adminA.Id)), HttpStatusCode.Accepted);
        var id = asked.GetProperty("id").GetGuid();
        asked.GetProperty("status").GetString().Should().Be("requested");

        var detail = await DataAsync(await adminB.Client.GetAsync($"/api/v1/admin/users/{target.Id}"));
        Roles(detail.GetProperty("user")).Should().Equal(Permissions.Roles.Buyer, Permissions.Roles.Support);
        detail.GetProperty("pendingRoleChange").GetProperty("id").GetGuid().Should().Be(id);
        (await ErrorAsync(await RequestRolesAsync(adminB, target.Id, [Permissions.Roles.Admin], await CodeAsync(adminB.Id)), HttpStatusCode.Conflict))
            .Should().Be("ROLE_CHANGE_PENDING");

        var approve = $"/api/v1/admin/role-changes/{id}/approve";
        (await ErrorAsync(await SendAsync(adminA.Client, HttpMethod.Post, approve, null, await CodeAsync(adminA.Id)), HttpStatusCode.Conflict))
            .Should().Be("ROLE_CHANGE_FOUR_EYES");
        (await ErrorAsync(await SendAsync(adminB.Client, HttpMethod.Post, approve), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        var approved = await DataAsync(await SendAsync(adminB.Client, HttpMethod.Post, approve, null, await CodeAsync(adminB.Id)));
        approved.GetProperty("status").GetString().Should().Be("approved");
        approved.GetProperty("decidedBy").GetGuid().Should().Be(adminB.Id);

        Roles((await DataAsync(await adminA.Client.GetAsync($"/api/v1/admin/users/{target.Id}"))).GetProperty("user"))
            .Should().Equal(Permissions.Roles.Buyer, Permissions.Roles.Finance);
        await using var db = Db();
        (await db.AuditLogs.CountAsync(a => a.Action == "user.roles_changed" && a.EntityId == target.Id.ToString() && a.ActorId == adminB.Id)).Should().Be(1);
        // The database refuses a self-approved change too.
        var insert = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO staff_role_changes (id, user_id, roles_before, roles_after, reason, status, requested_by, decided_by, created_at) VALUES ({Guid.NewGuid()}, {target.Id}, {Array.Empty<string>()}, {new[] { "admin" }}, {"direct insert"}, {"approved"}, {adminA.Id}, {adminA.Id}, {DateTime.UtcNow})");
        await insert.Should().ThrowAsync<Npgsql.PostgresException>().Where(e => e.ConstraintName == "ck_staff_role_changes_four_eyes");
    }

    [PostgresFact]
    public async Task Nobody_raises_their_own_roles_and_only_staff_roles_are_managed()
    {
        var adminA = await StaffAsync(Permissions.Roles.Admin);
        var adminB = await StaffAsync(Permissions.Roles.Support, Permissions.Roles.Admin);

        (await ErrorAsync(await RequestRolesAsync(adminB, adminB.Id, [Permissions.Roles.Admin, Permissions.Roles.Finance, Permissions.Roles.Support],
            await CodeAsync(adminB.Id)), HttpStatusCode.Forbidden)).Should().Be("ROLE_CHANGE_SELF");
        (await ErrorAsync(await RequestRolesAsync(adminA, adminB.Id, [Permissions.Roles.Admin, Permissions.Roles.Seller], await CodeAsync(adminA.Id)),
            HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");

        // A asks to raise B; B cannot approve (or block) a change of B's own roles.
        var asked = await DataAsync(await RequestRolesAsync(adminA, adminB.Id, [Permissions.Roles.Admin, Permissions.Roles.Finance, Permissions.Roles.Support],
            await CodeAsync(adminA.Id)), HttpStatusCode.Accepted);
        var id = asked.GetProperty("id").GetGuid();
        (await ErrorAsync(await SendAsync(adminB.Client, HttpMethod.Post, $"/api/v1/admin/role-changes/{id}/approve", null, await CodeAsync(adminB.Id)),
            HttpStatusCode.Forbidden)).Should().Be("ROLE_CHANGE_SELF");
        (await ErrorAsync(await SendAsync(adminB.Client, HttpMethod.Post, $"/api/v1/admin/role-changes/{id}/reject", new { reason = "not me please" }),
            HttpStatusCode.Forbidden)).Should().Be("ROLE_CHANGE_SELF");

        await using (var db = Db())
            (await db.UserRoles.Where(r => r.UserId == adminB.Id).Select(r => r.RoleName).OrderBy(r => r).ToListAsync())
                .Should().Equal(Permissions.Roles.Admin, Permissions.Roles.Support);

        // The requester withdraws: nothing changes.
        var withdrawn = await DataAsync(await SendAsync(adminA.Client, HttpMethod.Post, $"/api/v1/admin/role-changes/{id}/reject", new { reason = "asked by mistake" }));
        withdrawn.GetProperty("status").GetString().Should().Be("rejected");
    }

    // ---- no secrets -----------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> UserFields = ["id", "email", "fullName", "roles", "status", "twoFactorEnrolled", "lastSignInAtUtc", "createdAtUtc"];

    [PostgresFact]
    public async Task List_and_detail_carry_only_the_reviewed_fields_and_no_hash_or_secret()
    {
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var target = await SignInAsync(await NewAsync(Permissions.Roles.Finance));
        string hash, secret;
        await using (var db = Db())
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == target.Id);
            (hash, secret) = (u.PasswordHash!, u.TwoFactorSecret!);
        }

        var listResponse = await admin.Client.GetAsync($"/api/v1/admin/users?q={Uri.EscapeDataString(target.Email[..10])}&role=finance&status=active");
        listResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        var listText = await listResponse.Content.ReadAsStringAsync();
        var list = await DataAsync(listResponse);
        var item = list.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().Should().Be(target.Id);
        item.GetProperty("twoFactorEnrolled").GetBoolean().Should().BeTrue();
        item.GetProperty("lastSignInAtUtc").ValueKind.Should().Be(JsonValueKind.String);

        var detailResponse = await admin.Client.GetAsync($"/api/v1/admin/users/{target.Id}");
        var detailText = await detailResponse.Content.ReadAsStringAsync();
        var detail = await DataAsync(detailResponse);
        foreach (var user in new[] { item, detail.GetProperty("user") })
            user.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(UserFields);
        detail.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("user", "pendingRoleChange");

        foreach (var text in new[] { listText, detailText })
        {
            text.Should().NotContain(hash).And.NotContain(secret);
            foreach (var word in new[] { "hash", "secret", "recovery", "token", "password", "totp" })
                text.Should().NotContainEquivalentOf(word);
        }

        (await ErrorAsync(await admin.Client.GetAsync("/api/v1/admin/users?status=deleted"), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
    }
}
