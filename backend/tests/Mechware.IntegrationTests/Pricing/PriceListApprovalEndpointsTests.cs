using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Api.Services;
using Mechware.Application.Companies;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.Infrastructure.Pricing;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Pricing;

/// <summary>
/// D2.8: approve, reject and withdraw through the real API (ADR-0008 §3, §4, §10; threat model §৮ PL1–PL3, PL10, PL14, PL19).
/// Real roles: Support edits and submits, Finance approves (cost visible, no below-floor right), Admin also approves below the
/// floor. Every approval and withdrawal needs a fresh authenticator code; <see cref="CodeAsync"/> makes the current one usable
/// again for the test (it clears the replay guard), except where a test proves the replay guard itself.
/// </summary>
public sealed class PriceListApprovalEndpointsTests : IDisposable
{
    private const string Base = "/api/v1/admin/price-lists";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "PriceStaff#2026!";
    private const string StepUpHeader = "X-Step-Up-Code";
    private static long _binSeq = 8_200_000_000_000L;
    private static readonly string[] CostMembers = ["costMinor", "floorMinor", "belowFloor", "belowCost", "costUnknown"];
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    // ---- people -------------------------------------------------------------------------------------------------------

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role, WebApplicationFactory<Program>? host = null)
    {
        host ??= _factory;
        host.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"appr.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Approver " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Staff(client, id);
    }

    /// <summary>The current authenticator code, usable once more: the replay guard of this user is cleared first.</summary>
    private async Task<string> CodeAsync(Staff staff)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == staff.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>A host whose signed-in staff never hold <c>pricing.view_cost</c> (no role has approve without it today).</summary>
    private WebApplicationFactory<Program> HostWithoutViewCost() => _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        s.AddScoped<ICurrentUserService>(sp => new WithoutViewCost(new HttpContextCurrentUserService(sp.GetRequiredService<IHttpContextAccessor>())))));

    private sealed class WithoutViewCost(ICurrentUserService inner) : ICurrentUserService
    {
        public Guid? UserId => inner.UserId;
        public string? Email => inner.Email;
        public Guid? SellerId => inner.SellerId;
        public string? IpAddress => inner.IpAddress;
        public string? UserAgent => inner.UserAgent;
        public string? CorrelationId => inner.CorrelationId;
        public bool HasPermission(string permission) => permission != Permissions.PricingViewCost && inner.HasPermission(permission);
    }

    // ---- data -----------------------------------------------------------------------------------------------------------

    private async Task<Guid> VariantAsync(long? cost = null)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 50);
        if (cost is { } c) await SetCostAsync(seeded.VariantId, c);
        return seeded.VariantId;
    }

    private async Task SetCostAsync(Guid variant, long cost)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {cost} WHERE id = {variant}");
    }

    private static object NewList(string scope = "General", Guid? companyId = null, DateTime? validFrom = null) => new
    {
        scope,
        companyId,
        contractRef = companyId is null ? null : "CN-D28-" + Guid.NewGuid().ToString("N")[..6],
        name = "D28 list " + Guid.NewGuid().ToString("N")[..6],
        validFromUtc = validFrom ?? DateTime.UtcNow.AddDays(1),
        validToUtc = DateTime.UtcNow.AddDays(30),
    };

    private static object Entry(Guid variant, int minQty, long price, string? reason = null) => new { variantId = variant, minQty, unitPriceMinor = price, belowFloorReason = reason };

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static IEnumerable<string> PropertyNames(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                yield return p.Name;
                foreach (var n in PropertyNames(p.Value)) yield return n;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                foreach (var n in PropertyNames(item)) yield return n;
    }

    /// <summary>Created, filled and submitted by <paramref name="author"/>; returns the list id and the entry ids by variant.</summary>
    private static async Task<(Guid Id, Dictionary<Guid, Guid> Entries)> PendingAsync(Staff author, object[] entries, object? list = null)
    {
        var id = (await DataAsync(await author.Client.PostAsJsonAsync(Base, list ?? NewList()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var filled = await DataAsync(await author.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries }));
        await DataAsync(await author.Client.PostAsync($"{Base}/{id}/submit", null));
        var ids = filled.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("variantId").GetGuid(), e => e.GetProperty("id").GetGuid());
        return (id, ids);
    }

    private static async Task<JsonElement> DetailAsync(Staff who, Guid id) => await DataAsync(await who.Client.GetAsync($"{Base}/{id}"));

    private static async Task<string> HashAsync(Staff who, Guid id) => (await DetailAsync(who, id)).GetProperty("contentHash").GetString()!;

    private static Task<HttpResponseMessage> PostAsync(Staff who, string path, object? body, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (code is not null) request.Headers.Add(StepUpHeader, code);
        return who.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ApproveWithAsync(Staff who, Guid id, string? hash, Guid[]? acknowledged, string? code) =>
        PostAsync(who, $"{Base}/{id}/approve", new { expectedContentHash = hash, acknowledgedBelowFloorEntryIds = acknowledged ?? [] }, code);

    private async Task<HttpResponseMessage> ApproveAsync(Staff who, Guid id, params Guid[] acknowledged) =>
        await ApproveWithAsync(who, id, await HashAsync(who, id), acknowledged, await CodeAsync(who));

    private async Task<PriceList> LoadAsync(Guid id)
    {
        await using var db = Db();
        return await PriceListTestData.LoadAsync(db, id);
    }

    private async Task<List<AuditLog>> AuditAsync(string action, Guid listId)
    {
        await using var db = Db();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.EntityId == listId.ToString()).ToListAsync();
    }

    // ---- PL2: step-up and permission --------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Approve_WithoutStepUpCode_IsRefused()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var (id, _) = await PendingAsync(support, [Entry(await VariantAsync(cost: 1_000), 2, 5_000)]);
        var hash = await HashAsync(finance, id);

        (await ErrorCodeAsync(await ApproveWithAsync(finance, id, hash, [], null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await ApproveWithAsync(finance, id, hash, [], "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await LoadAsync(id)).Status.Should().Be(PriceListStatus.PendingApproval);
        (await AuditAsync("pricelist.approved", id)).Should().BeEmpty();

        var approved = await DataAsync(await ApproveWithAsync(finance, id, hash, [], await CodeAsync(finance)));
        (approved.GetProperty("status").GetString(), approved.GetProperty("approvedBy").GetGuid()).Should().Be(("Approved", finance.UserId));
        await using var db = Db();
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == finance.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("pricelist.approve") && json.Contains($"priceListId={id}"), "the step-up audit names the list it confirmed");
    }

    [PostgresFact]
    public async Task StepUpCode_ConfirmsOneApprovalOnly_AReplayIsRefused()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var variant = await VariantAsync(cost: 1_000);
        var (first, _) = await PendingAsync(support, [Entry(variant, 2, 5_000)]);
        var (second, _) = await PendingAsync(support, [Entry(variant, 3, 4_900)]);

        var code = await CodeAsync(finance);
        await DataAsync(await ApproveWithAsync(finance, first, await HashAsync(finance, first), [], code));
        (await ErrorCodeAsync(await ApproveWithAsync(finance, second, await HashAsync(finance, second), [], code), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await LoadAsync(second)).Status.Should().Be(PriceListStatus.PendingApproval);
    }

    [PostgresFact]
    public async Task Approve_WithoutPermission_Is403()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var (id, _) = await PendingAsync(support, [Entry(await VariantAsync(cost: 1_000), 2, 5_000)]);
        var hash = await HashAsync(support, id);
        using var anonymous = _factory.CreateClient();

        // Support may edit and submit, never decide; the permission is checked before any code is looked at.
        foreach (var action in new[] { "approve", "reject", "withdraw" })
        {
            var body = new { expectedContentHash = hash, acknowledgedBelowFloorEntryIds = Array.Empty<Guid>(), reason = "Looks right to me" };
            (await PostAsync(support, $"{Base}/{id}/{action}", body, await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden, action);
            (await anonymous.PostAsJsonAsync($"{Base}/{id}/{action}", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, action);
        }

        var stored = await LoadAsync(id);
        (stored.Status, stored.ApprovedBy, stored.RejectedBy).Should().Be((PriceListStatus.PendingApproval, null, null));
        await using var db = Db();
        (await db.AuditLogs.CountAsync(a => a.ActorId == support.UserId && a.Action.StartsWith("user.step_up"))).Should().Be(0, "no code was checked");
    }

    // ---- PL1: four eyes -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Approve_ByTheSubmitterOrAnEditor_Is409FourEyes_ButFinanceApprovesWhatSupportWrote()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var variant = await VariantAsync(cost: 1_000);

        var (own, _) = await PendingAsync(admin, [Entry(variant, 2, 5_000)]);
        (await ErrorCodeAsync(await ApproveAsync(admin, own), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_FOUR_EYES", "the admin created and submitted it");
        // Naming someone else as the approver in the body changes nothing: the approver is the signed-in user.
        var smuggled = new
        {
            expectedContentHash = await HashAsync(admin, own),
            acknowledgedBelowFloorEntryIds = Array.Empty<Guid>(),
            approverId = finance.UserId,
            approvedBy = finance.UserId,
            userId = finance.UserId,
        };
        (await ErrorCodeAsync(await PostAsync(admin, $"{Base}/{own}/approve", smuggled, await CodeAsync(admin)), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_FOUR_EYES");
        (await LoadAsync(own)).Status.Should().Be(PriceListStatus.PendingApproval);

        var id = (await DataAsync(await support.Client.PostAsJsonAsync(Base, NewList()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await DataAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries = new[] { Entry(variant, 2, 5_000) } }));
        await DataAsync(await admin.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries = new[] { Entry(variant, 5, 4_800) } }));
        await DataAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null));
        (await ErrorCodeAsync(await ApproveAsync(admin, id), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_FOUR_EYES", "the admin edited the draft");

        var approved = await DataAsync(await ApproveAsync(finance, id));
        (approved.GetProperty("status").GetString(), approved.GetProperty("approvedBy").GetGuid(), approved.GetProperty("createdBy").GetGuid())
            .Should().Be(("Approved", finance.UserId, support.UserId));
        approved.GetProperty("actorNames").GetProperty(finance.UserId.ToString()).GetString().Should().Be("Approver finance");
        var stored = await LoadAsync(id);
        (stored.ApprovedBy, stored.SubmittedBy).Should().Be((finance.UserId, support.UserId));
    }

    // ---- PL3: what was shown is what is approved ------------------------------------------------------------------------

    [PostgresFact]
    public async Task Approve_WithAStaleContentHash_IsRefused()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var variant = await VariantAsync(cost: 1_000);
        var (id, _) = await PendingAsync(support, [Entry(variant, 2, 5_000)]);
        var shown = await HashAsync(finance, id);

        (await ErrorCodeAsync(await ApproveWithAsync(finance, id, null, [], await CodeAsync(finance)), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorCodeAsync(await ApproveWithAsync(finance, id, new string('a', 64), [], await CodeAsync(finance)), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_CHANGED");

        // The list goes back, its author changes a price and submits again: the hash the approver saw before is stale now.
        await DataAsync(await PostAsync(finance, $"{Base}/{id}/reject", new { reason = "Check the price for two units" }, null));
        await DataAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries = new[] { Entry(variant, 2, 4_000) } }));
        await DataAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null));
        (await ErrorCodeAsync(await ApproveWithAsync(finance, id, shown, [], await CodeAsync(finance)), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_CHANGED");
        (await LoadAsync(id)).Status.Should().Be(PriceListStatus.PendingApproval);

        var current = await HashAsync(finance, id);
        current.Should().NotBe(shown);
        await DataAsync(await ApproveWithAsync(finance, id, current, [], await CodeAsync(finance)));
        (await LoadAsync(id)).Entries.Single().UnitPriceMinor.Should().Be(4_000, "the approved content is the one whose hash was sent");
    }

    [PostgresFact]
    public async Task TwoParallelApprovals_ApproveExactlyOnce()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        Staff[] approvers = [await StaffAsync(Permissions.Roles.Finance), await StaffAsync(Permissions.Roles.Finance), await StaffAsync(Permissions.Roles.Finance), await StaffAsync(Permissions.Roles.Admin)];
        var variant = await VariantAsync(cost: 1_000);

        for (var round = 0; round < 4; round++)
        {
            var (id, _) = await PendingAsync(support, [Entry(variant, 2 + round, 5_000 - round)]);
            var hash = await HashAsync(support, id);
            var codes = new List<string>();
            foreach (var a in approvers) codes.Add(await CodeAsync(a));

            var responses = await Task.WhenAll(approvers.Select((a, i) => ApproveWithAsync(a, id, hash, [], codes[i])));

            var winners = responses.Select((r, i) => (r, i)).Where(x => x.r.StatusCode == HttpStatusCode.OK).ToList();
            winners.Should().ContainSingle($"round {round}: exactly one approval wins");
            foreach (var r in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
                (await ErrorCodeAsync(r, HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_PENDING", "the losers waited on the row lock and found the list decided");
            (await LoadAsync(id)).ApprovedBy.Should().Be(approvers[winners.Single().i].UserId);
            (await AuditAsync("pricelist.approved", id)).Should().ContainSingle();
        }
    }

    // ---- PL10: margin floor ---------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Approve_BelowFloorWithoutExtraPermission_IsRefused()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var variant = await VariantAsync(cost: 10_000); // floor 11,000
        var (id, entries) = await PendingAsync(support, [Entry(variant, 2, 10_500, "Strategic account, agreed with the sales head")]);
        var below = entries[variant];
        (await DetailAsync(finance, id)).GetProperty("belowFloorEntryIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(below);

        (await ErrorCodeAsync(await ApproveAsync(finance, id, below), HttpStatusCode.Forbidden)).Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");
        (await LoadAsync(id)).Status.Should().Be(PriceListStatus.PendingApproval);

        await DataAsync(await ApproveAsync(admin, id, below));
        var entry = (await LoadAsync(id)).Entries.Single();
        (entry.CostMinorAtApproval, entry.FloorMinorAtApproval, entry.BelowFloorAtApproval).Should().Be((10_000L, 11_000L, true));
        JsonDocument.Parse((await AuditAsync("pricelist.approved", id)).Single().NewValuesJson!).RootElement.GetProperty("BelowFloorAcknowledged").GetInt32().Should().Be(1);
    }

    [PostgresFact]
    public async Task Approve_BelowFloorWithoutAcknowledgement_IsRefused()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var (low, fine) = (await VariantAsync(cost: 10_000), await VariantAsync(cost: 1_000));
        var (id, entries) = await PendingAsync(support, [Entry(low, 2, 10_500, "Clearance of the old model"), Entry(fine, 2, 5_000)]);
        var (below, above) = (entries[low], entries[fine]);

        foreach (var wrong in new Guid[][] { [], [above], [below, above], [below, Guid.NewGuid()] })
        {
            var refused = await ApproveAsync(admin, id, wrong);
            var text = await refused.Content.ReadAsStringAsync();
            (await ErrorCodeAsync(refused, HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_NOT_ACKNOWLEDGED", string.Join(",", wrong));
            text.Should().Contain(below.ToString(), "the approver is told which entries to acknowledge").And.NotContain(above.ToString());
        }

        (await LoadAsync(id)).Status.Should().Be(PriceListStatus.PendingApproval);
        await DataAsync(await ApproveAsync(admin, id, below));
    }

    [PostgresFact]
    public async Task FloorIsRecheckedAtApproval_WithTheCostOfThatMoment()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var admin = await StaffAsync(Permissions.Roles.Admin);

        // A general list is compared on its net price: 5,250.00 at the test category's 5 % VAT is 5,000.00 net.
        // Above the floor at submit (cost 1,000, floor 1,100); then the cost rises: below the floor without a reason, then below cost.
        var rising = await VariantAsync(cost: 1_000);
        var (first, _) = await PendingAsync(support, [Entry(rising, 2, 5_250)]);
        await SetCostAsync(rising, 4_600); // floor 5,060
        (await ErrorCodeAsync(await ApproveAsync(admin, first), HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_REASON_REQUIRED");
        await SetCostAsync(rising, 5_100);
        (await ErrorCodeAsync(await ApproveAsync(admin, first), HttpStatusCode.Conflict)).Should().Be("PRICE_BELOW_COST");

        // Below the floor at submit (cost 4,800); then the cost falls: nothing is below the floor any more, and that is what is stored.
        var falling = await VariantAsync(cost: 4_800);
        var (second, entries) = await PendingAsync(support, [Entry(falling, 2, 5_250, "Introductory price for this buyer")]); // floor 5,280
        (await DetailAsync(admin, second)).GetProperty("belowFloorEntryIds").GetArrayLength().Should().Be(1);
        await SetCostAsync(falling, 1_000);
        var shown = await DetailAsync(admin, second);
        shown.GetProperty("belowFloorEntryIds").GetArrayLength().Should().Be(0, "the detail evaluates with the cost of this moment, like the approval");
        (shown.GetProperty("entries")[0].GetProperty("costMinor").GetInt64(), shown.GetProperty("entries")[0].GetProperty("floorMinor").GetInt64()).Should().Be((1_000L, 1_100L));

        (await ErrorCodeAsync(await ApproveAsync(admin, second, entries[falling]), HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_NOT_ACKNOWLEDGED");
        await DataAsync(await ApproveAsync(admin, second));
        var entry = (await LoadAsync(second)).Entries.Single();
        (entry.CostMinorAtApproval, entry.FloorMinorAtApproval, entry.BelowFloorAtApproval).Should().Be((1_000L, 1_100L, false));
    }

    [PostgresFact]
    public async Task AnApproverWithoutViewCost_ApprovesWithIdsOnly_AndNeverSeesAnAmount()
    {
        var host = HostWithoutViewCost();
        var support = await StaffAsync(Permissions.Roles.Support, host);
        var finance = await StaffAsync(Permissions.Roles.Finance, host);
        var admin = await StaffAsync(Permissions.Roles.Admin, host);
        await using (var db = Db())
        {
            var c = Company.Register(Guid.NewGuid(), "Approval Buyer Ltd", "TL-D28-" + Guid.NewGuid().ToString("N")[..6], Interlocked.Increment(ref _binSeq).ToString("D13"), null, null);
            c.Activate();
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            var plain = await VariantAsync(cost: 1_000);
            var (easy, _) = await PendingAsync(support, [Entry(plain, 1, 5_000)], NewList("Company", c.Id));
            await DataAsync(await ApproveAsync(finance, easy), HttpStatusCode.OK); // nothing below the floor: no cost right needed

            // cost 123,457 -> floor 135,803; price 130,000 is below the floor, above cost.
            var low = await VariantAsync(cost: 123_457);
            var (id, entries) = await PendingAsync(support, [Entry(low, 1, 130_000, "Matching the competitor's tender"), Entry(plain, 1, 5_000)], NewList("Company", c.Id));
            var below = entries[low];

            void NoAmounts(string raw)
            {
                raw.Should().NotContain("123457").And.NotContain("135803");
                using var doc = JsonDocument.Parse(raw);
                PropertyNames(doc.RootElement).Should().NotIntersectWith(CostMembers);
            }

            var detail = await admin.Client.GetAsync($"{Base}/{id}");
            var raw = await detail.Content.ReadAsStringAsync();
            NoAmounts(raw);
            raw.Should().Contain(below.ToString());
            using (var doc = JsonDocument.Parse(raw))
                doc.RootElement.GetProperty("data").GetProperty("belowFloorEntryIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(below);

            var refused = await ApproveAsync(admin, id);
            var refusedText = await refused.Content.ReadAsStringAsync();
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, refusedText);
            refusedText.Should().Contain($"1 entry is below the margin floor: {below}");
            NoAmounts(refusedText);

            var approved = await ApproveAsync(admin, id, below);
            approved.StatusCode.Should().Be(HttpStatusCode.OK);
            NoAmounts(await approved.Content.ReadAsStringAsync());
        }
    }

    // ---- reject ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Reject_NeedsNoCode_ReturnsToDraft_TheAuthorEditsAgain_AndAnotherPersonApprovesTheResubmission()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var variant = await VariantAsync(cost: 1_000);
        var (id, _) = await PendingAsync(support, [Entry(variant, 2, 5_000)]);

        (await ErrorCodeAsync(await PostAsync(finance, $"{Base}/{id}/reject", new { reason = "no" }, null), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorCodeAsync(await PostAsync(finance, $"{Base}/{id}/reject", new { }, null), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        var rejected = await DataAsync(await PostAsync(finance, $"{Base}/{id}/reject", new { reason = "The two-unit price is too low" }, null));
        (rejected.GetProperty("status").GetString(), rejected.GetProperty("rejectedBy").GetGuid(), rejected.GetProperty("rejectionReason").GetString())
            .Should().Be(("Draft", finance.UserId, "The two-unit price is too low"));
        rejected.GetProperty("contentHash").ValueKind.Should().Be(JsonValueKind.Null);
        rejected.GetProperty("submittedBy").ValueKind.Should().Be(JsonValueKind.Null);
        rejected.TryGetProperty("rejectedAtUtc", out var at).Should().BeTrue();
        at.ValueKind.Should().Be(JsonValueKind.String);
        (await ErrorCodeAsync(await PostAsync(finance, $"{Base}/{id}/reject", new { reason = "Again, by mistake" }, null), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_PENDING");

        await DataAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries = new[] { Entry(variant, 2, 5_500) } }));
        await DataAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null));
        var approved = await DataAsync(await ApproveAsync(admin, id));
        (approved.GetProperty("status").GetString(), approved.GetProperty("approvedBy").GetGuid()).Should().Be(("Approved", admin.UserId));
        (await PostAsync(finance, $"{Base}/{id}/reject", new { reason = "Too late to reject" }, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- withdraw -------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Withdraw_NeedsPermissionStepUpAndReason()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var variant = await VariantAsync(cost: 1_000);
        var (id, _) = await PendingAsync(support, [Entry(variant, 2, 5_000)]);
        string Path(Guid list) => $"{Base}/{list}/withdraw";
        var reason = new { reason = "Approved with the wrong buyer's prices" };

        (await ErrorCodeAsync(await PostAsync(finance, Path(id), reason, await CodeAsync(finance)), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_APPROVED");
        await DataAsync(await ApproveAsync(admin, id));

        (await PostAsync(support, Path(id), reason, await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "no permission");
        (await ErrorCodeAsync(await PostAsync(finance, Path(id), reason, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await PostAsync(finance, Path(id), new { }, await CodeAsync(finance)), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorCodeAsync(await PostAsync(finance, Path(id), new { reason = "oops" }, await CodeAsync(finance)), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await LoadAsync(id)).Status.Should().Be(PriceListStatus.Approved);

        var withdrawn = await DataAsync(await PostAsync(finance, Path(id), reason, await CodeAsync(finance)));
        (withdrawn.GetProperty("status").GetString(), withdrawn.GetProperty("withdrawnBy").GetGuid(), withdrawn.GetProperty("withdrawReason").GetString())
            .Should().Be(("Withdrawn", finance.UserId, "Approved with the wrong buyer's prices"));
        withdrawn.GetProperty("displayState").GetString().Should().Be("Withdrawn");
        (await ErrorCodeAsync(await PostAsync(finance, Path(id), reason, await CodeAsync(finance)), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_APPROVED");
    }

    // ---- PL19: audit --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task EveryPriceListTransition_IsAudited_WithActorAndContentHash()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var variant = await VariantAsync(cost: 1_000);
        var (id, _) = await PendingAsync(support, [Entry(variant, 2, 5_000)]);
        var firstHash = await HashAsync(support, id);
        await DataAsync(await PostAsync(finance, $"{Base}/{id}/reject", new { reason = "Please re-check the break" }, null));
        await DataAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries = new[] { Entry(variant, 2, 4_500) } }));
        await DataAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null));
        var secondHash = await HashAsync(support, id);
        var approved = await DataAsync(await ApproveAsync(admin, id));
        await DataAsync(await PostAsync(finance, $"{Base}/{id}/withdraw", new { reason = "Contract cancelled by the buyer" }, await CodeAsync(finance)));
        var revisedId = (await DataAsync(await support.Client.PostAsync($"{Base}/{id}/revise", null), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        async Task<JsonElement> Row(string action, Guid list, Guid actor, int index = 0)
        {
            var rows = (await AuditAsync(action, list)).OrderBy(a => a.CreatedAtUtc).ToList();
            rows.Should().HaveCountGreaterThan(index, action);
            rows[index].ActorId.Should().Be(actor, action);
            rows[index].EntityName.Should().Be("price_lists");
            using var doc = JsonDocument.Parse(rows[index].NewValuesJson!);
            return doc.RootElement.Clone();
        }

        (await Row("pricelist.submitted", id, support.UserId, 0)).GetProperty("ContentHash").GetString().Should().Be(firstHash);
        var rejected = await Row("pricelist.rejected", id, finance.UserId);
        (rejected.GetProperty("ContentHash").GetString(), rejected.GetProperty("Reason").GetString()).Should().Be((firstHash, "Please re-check the break"));
        (await Row("pricelist.submitted", id, support.UserId, 1)).GetProperty("ContentHash").GetString().Should().Be(secondHash);
        var approval = await Row("pricelist.approved", id, admin.UserId);
        (approval.GetProperty("ContentHash").GetString(), approval.GetProperty("Version").GetInt32(), approval.GetProperty("BelowFloorAcknowledged").GetInt32())
            .Should().Be((secondHash, 1, 0));
        approval.GetProperty("EffectiveFromUtc").GetDateTime().Should().Be(approved.GetProperty("effectiveFromUtc").GetDateTime());
        var withdrawal = await Row("pricelist.withdrawn", id, finance.UserId);
        (withdrawal.GetProperty("ContentHash").GetString(), withdrawal.GetProperty("Reason").GetString()).Should().Be((secondHash, "Contract cancelled by the buyer"));
        (await Row("pricelist.revised", revisedId, support.UserId)).GetProperty("RevisedFromContentHash").GetString().Should().Be(secondHash);

        // The interceptor's entity rows come on top of the explicit action rows.
        await using var db = Db();
        (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && !a.Action.StartsWith("pricelist."))).Should().BeGreaterThan(0);
    }

    // ---- effect on orders (PL13, PL14, PL16) ----------------------------------------------------------------------------

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<PlacedOrder> OrderAsync(Guid buyer, Guid company, Guid variant, DateTime at)
    {
        await using var db = Db();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), new FixedClock(at),
            ShippingTestKit.DevelopmentQuoter(db), new PriceListResolver(db));
        return await orders.PlaceAsync(new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variant, 1)], null, company));
    }

    private async Task<Mechware.Domain.Orders.OrderItem> ItemAsync(Guid orderId)
    {
        await using var db = Db();
        return await db.OrderItems.AsNoTracking().SingleAsync(i => db.SubOrders.Any(s => s.Id == i.SubOrderId && s.OrderId == orderId));
    }

    [PostgresFact]
    public async Task AnApprovedContract_PricesOrdersFromItsStart_AWithdrawalStopsItForLaterOrders_PlacedOrdersKeepTheirPrice()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        Guid buyer, company;
        await using (var db = Db())
        {
            var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
            var member = User.Create($"buyer.{Guid.NewGuid():N}@example.test", null, null, "Buyer");
            owner.VerifyEmail(DateTime.UtcNow);
            member.VerifyEmail(DateTime.UtcNow);
            db.Users.AddRange(owner, member);
            db.Addresses.Add(TestUsers.DefaultAddress(member.Id));
            await db.SaveChangesAsync();
            var companies = new CompanyService(db);
            var bin = Interlocked.Increment(ref _binSeq).ToString("D13");
            var registered = await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Contract Orders Ltd", "TL-D28-" + bin[^4..], bin, null, null));
            await companies.AddAcceptedMemberAsync(owner.Id, registered.Id, new AddMemberRequest(member.Id, CompanyRoles.Buyer, null, null));
            await companies.AdminActivateAsync(registered.Id);
            (buyer, company) = (member.Id, registered.Id);
        }

        var variant = await VariantAsync(cost: 1_000);
        var (id, _) = await PendingAsync(support, [Entry(variant, 1, 100_000)], NewList("Company", company, DateTime.UtcNow.AddHours(-1)));
        var approved = await DataAsync(await ApproveAsync(finance, id));
        var start = approved.GetProperty("effectiveFromUtc").GetDateTime().ToUniversalTime();
        start.Should().BeOnOrAfter(approved.GetProperty("approvedAtUtc").GetDateTime().ToUniversalTime().AddSeconds(60), "a backdated window starts 60 s after the approval");

        var before = await ItemAsync((await OrderAsync(buyer, company, variant, start.AddSeconds(-1))).OrderId);
        before.PriceListId.Should().BeNull("one second before the start the public price applies");
        var contract = await OrderAsync(buyer, company, variant, start);
        var onContract = await ItemAsync(contract.OrderId);
        (onContract.UnitPriceMinor, onContract.PriceListId, onContract.PriceSource).Should().Be((100_000, id, PriceSource.Contract));

        await DataAsync(await PostAsync(finance, $"{Base}/{id}/withdraw", new { reason = "Contract ended early by the buyer" }, await CodeAsync(finance)));

        var after = await ItemAsync((await OrderAsync(buyer, company, variant, start.AddMinutes(1))).OrderId);
        (after.PriceListId, after.PriceSource).Should().Be(((Guid?)null, PriceSource.List), "an order priced after the withdrawal pays the public price again");
        after.UnitPriceMinor.Should().Be(before.UnitPriceMinor);
        var kept = await ItemAsync(contract.OrderId);
        (kept.UnitPriceMinor, kept.PriceListId, kept.PriceListVersion).Should().Be((100_000, id, 1), "a placed order keeps its contract price");
    }
}
