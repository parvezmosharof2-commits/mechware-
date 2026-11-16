using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Catalog;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Pricing;

/// <summary>
/// D2.9: the staff margin-floor API through the real host, database and roles (ADR-0008 §4; threat model §৮ PL11). Reading
/// needs <c>pricing.view_cost</c> (Finance, Admin), changing <c>pricing.manage_floors</c> (Admin only) plus a fresh
/// authenticator code. A floor change never approves anything: the approval evaluates the floors of its own moment.
/// </summary>
public sealed class MarginFloorEndpointsTests : IDisposable
{
    private const string Base = "/api/v1/admin/margin-floors";
    private const string Lists = "/api/v1/admin/price-lists";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "FloorStaff#2026!";
    private static readonly Guid Global = PriceListSql.GlobalMarginFloorId;
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    // ---- people (as in PriceListApprovalEndpointsTests) -------------------------------------------------------------------

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role)
    {
        _factory.CreateClient().Dispose(); // starts the host (its Development seeding) before the account is written
        var email = $"floor.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Floors " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Staff(client, id);
    }

    /// <summary>The current authenticator code, usable once more (the user's replay guard is cleared first).</summary>
    private async Task<string> CodeAsync(Staff staff)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == staff.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    // ---- requests -------------------------------------------------------------------------------------------------------

    private static object Floor(string scope, decimal markup, Guid? brandId = null, Guid? categoryId = null) =>
        new { scope, brandId, categoryId, minMarkupPercent = markup };

    private static Task<HttpResponseMessage> SendAsync(Staff who, HttpMethod method, string path, object? body, string? code)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PutAsync(Staff who, object body, string? code) => SendAsync(who, HttpMethod.Put, Base, body, code);

    private async Task<HttpResponseMessage> PutAsync(Staff who, object body) => await PutAsync(who, body, await CodeAsync(who));

    private static Task<HttpResponseMessage> DeleteAsync(Staff who, Guid id, string? code) => SendAsync(who, HttpMethod.Delete, $"{Base}/{id}", null, code);

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

    // ---- data -----------------------------------------------------------------------------------------------------------

    private async Task<decimal?> MarkupAsync(Guid floorId)
    {
        await using var db = Db();
        return await db.MarginFloors.AsNoTracking().Where(f => f.Id == floorId).Select(f => (decimal?)f.MinMarkupPercent).SingleOrDefaultAsync();
    }

    private async Task<List<AuditLog>> AuditAsync(string action, Guid? floorId = null)
    {
        await using var db = Db();
        var q = db.AuditLogs.AsNoTracking().Where(a => a.Action == action);
        if (floorId is { } id) q = q.Where(a => a.EntityId == id.ToString());
        return await q.OrderBy(a => a.CreatedAtUtc).ToListAsync();
    }

    private static decimal? MarkupIn(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("MinMarkupPercent").GetDecimal();
    }

    private async Task<Guid> BrandAsync()
    {
        await using var db = Db();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var brand = new Brand(Guid.NewGuid(), "Brand " + suffix, "brand-" + suffix, null, true);
        db.Brands.Add(brand);
        await db.SaveChangesAsync();
        return brand.Id;
    }

    private async Task<(Guid Root, Guid Child)> CategoriesAsync()
    {
        await using var db = Db();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var root = Category.CreateRoot(Guid.NewGuid(), "Automation " + suffix, "অটোমেশন", "automation-" + suffix);
        var child = Category.CreateChild(root, Guid.NewGuid(), "PLCs " + suffix, "পিএলসি", "plcs-" + suffix);
        db.Categories.AddRange(root, child);
        await db.SaveChangesAsync();
        return (root.Id, child.Id);
    }

    /// <summary>A variant (5 % VAT category) with this cost, optionally under a brand and/or category.</summary>
    private async Task<Guid> VariantAsync(long cost, Guid? brand = null, Guid? category = null)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 50);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {cost} WHERE id = {seeded.VariantId}");
        if (brand is { } b) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET brand_id = {b} WHERE id = {seeded.ProductId}");
        if (category is { } c) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET category_id = {c} WHERE id = {seeded.ProductId}");
        return seeded.VariantId;
    }

    // ---- PL11: permission, step-up, audit ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ChangingAFloor_NeedsItsOwnPermissionAndStepUp_AndIsAudited()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var brand = await BrandAsync();
        using var anonymous = _factory.CreateClient();

        // Support has neither right; Finance may read floors (view_cost) but not change them. The permission comes before any code.
        foreach (var who in new[] { support, finance })
        {
            (await PutAsync(who, Floor("Global", 1m))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await DeleteAsync(who, Global, await CodeAsync(who))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await anonymous.PutAsJsonAsync(Base, Floor("Global", 1m))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using (var db = Db())
            (await db.AuditLogs.CountAsync(a => (a.ActorId == support.UserId || a.ActorId == finance.UserId) && a.Action.StartsWith("user.step_up"))).Should().Be(0);

        // Admin without a code, with a wrong code: refused, nothing changed.
        (await ErrorCodeAsync(await PutAsync(admin, Floor("Global", 1m), null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await PutAsync(admin, Floor("Global", 1m), "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await MarkupAsync(Global)).Should().Be(10m, "the seeded global row is untouched");

        // A valid code changes the global markup; the same code again is a replay and is refused.
        var code = await CodeAsync(admin);
        var changed = await DataAsync(await PutAsync(admin, Floor("Global", 12.5m), code));
        (changed.GetProperty("id").GetGuid(), changed.GetProperty("minMarkupPercent").GetDecimal(), changed.GetProperty("updatedBy").GetGuid())
            .Should().Be((Global, 12.5m, admin.UserId));
        changed.GetProperty("updatedByName").GetString().Should().Be("Floors admin");
        (await ErrorCodeAsync(await PutAsync(admin, Floor("Global", 1m), code), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await MarkupAsync(Global)).Should().Be(12.5m, "the replayed code changed nothing");

        var row = (await AuditAsync("margin-floor.changed", Global)).Should().ContainSingle().Subject;
        (row.ActorId, row.EntityName).Should().Be((admin.UserId, "margin_floors"));
        (MarkupIn(row.OldValuesJson), MarkupIn(row.NewValuesJson)).Should().Be((10m, 12.5m), "the audit keeps the old and the new markup");
        using (var values = JsonDocument.Parse(row.NewValuesJson!))
            values.RootElement.GetProperty("Scope").GetString().Should().Be("Global");

        // A brand row: created (no old value), then deleted with its own step-up purpose.
        var created = await DataAsync(await PutAsync(admin, Floor("Brand", 20m, brand)));
        var brandFloor = created.GetProperty("id").GetGuid();
        var creation = (await AuditAsync("margin-floor.changed", brandFloor)).Should().ContainSingle().Subject;
        (creation.OldValuesJson, MarkupIn(creation.NewValuesJson)).Should().Be(((string?)null, 20m));
        creation.NewValuesJson.Should().Contain(brand.ToString());

        (await DeleteAsync(finance, brandFloor, await CodeAsync(finance))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(await DeleteAsync(admin, brandFloor, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await DeleteAsync(admin, brandFloor, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MarkupAsync(brandFloor)).Should().BeNull();
        var deletion = (await AuditAsync("margin-floor.deleted", brandFloor)).Should().ContainSingle().Subject;
        (deletion.ActorId, MarkupIn(deletion.OldValuesJson), deletion.NewValuesJson).Should().Be(((Guid?)admin.UserId, 20m, (string?)null));

        var stepUps = (await AuditAsync("user.step_up_passed")).Where(a => a.ActorId == admin.UserId).Select(a => a.NewValuesJson!).ToList();
        stepUps.Should().Contain(j => j.Contains("margin-floor.change"));
        stepUps.Should().Contain(j => j.Contains("margin-floor.delete") && j.Contains($"marginFloorId={brandFloor}"), "the step-up audit names the row it confirmed");
    }

    [PostgresFact]
    public async Task ReadingFloors_NeedsViewCost_AndShowsMarkupsOnly()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var variant = await VariantAsync(cost: 123_457);
        using var anonymous = _factory.CreateClient();

        foreach (var path in new[] { Base, $"{Base}/effective?variantId={variant}" })
        {
            (await support.Client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            (await anonymous.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        }

        var list = await DataAsync(await finance.Client.GetAsync(Base));
        var global = list.EnumerateArray().Should().ContainSingle().Subject;
        (global.GetProperty("id").GetGuid(), global.GetProperty("scope").GetString(), global.GetProperty("minMarkupPercent").GetDecimal())
            .Should().Be((Global, "Global", 10m));
        global.GetProperty("updatedBy").ValueKind.Should().Be(JsonValueKind.Null, "the migration seeded it");

        var raw = await (await finance.Client.GetAsync($"{Base}/effective?variantId={variant}")).Content.ReadAsStringAsync();
        raw.Should().NotContain("123457", "the effective floor names a row and a markup, never the variant's cost");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        (data.GetProperty("floor").GetProperty("id").GetGuid(), data.GetProperty("minMarkupPercent").GetDecimal(), data.GetProperty("failClosed").GetBoolean())
            .Should().Be((Global, 10m, false));
    }

    // ---- validation -------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Upsert_RefusesAWrongShapeRangeOrUnknownBrandOrCategory_AndNeverRounds()
    {
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var brand = await BrandAsync();
        var (category, _) = await CategoriesAsync();

        object[] refused =
        [
            Floor("Global", 12m, brandId: brand),
            Floor("Global", 12m, categoryId: category),
            Floor("Brand", 12m),
            Floor("Brand", 12m, brand, category),
            Floor("Category", 12m, brandId: brand),
            Floor("BrandAndCategory", 12m, brandId: brand),
            Floor("BrandAndCategory", 12m, categoryId: category),
            Floor("Everything", 12m),
            Floor("3", 12m, brand),
            new { scope = (string?)null, minMarkupPercent = 12m },
            new { scope = "Global" },
            Floor("Global", 1000.01m),
            Floor("Global", 10.001m),
            Floor("Global", -0.01m),
            Floor("Brand", 12m, Guid.NewGuid()),
            Floor("Category", 12m, categoryId: Guid.NewGuid()),
            Floor("BrandAndCategory", 12m, brand, Guid.NewGuid()),
        ];
        foreach (var body in refused)
            (await ErrorCodeAsync(await PutAsync(admin, body), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", JsonSerializer.Serialize(body));

        await using (var db = Db())
        {
            (await db.MarginFloors.CountAsync()).Should().Be(1, "nothing was written");
            (await db.AuditLogs.CountAsync(a => a.Action == "margin-floor.changed")).Should().Be(0);
        }

        (await MarkupAsync(Global)).Should().Be(10m);

        // The edges are allowed: 1,000 % and 0 %, and exactly two decimals.
        (await DataAsync(await PutAsync(admin, Floor("Brand", 1000m, brand)))).GetProperty("minMarkupPercent").GetDecimal().Should().Be(1000m);
        (await DataAsync(await PutAsync(admin, Floor("Category", 0m, categoryId: category)))).GetProperty("minMarkupPercent").GetDecimal().Should().Be(0m);
        var both = await DataAsync(await PutAsync(admin, Floor("brandandcategory", 10.01m, brand, category)));
        (both.GetProperty("scope").GetString(), both.GetProperty("minMarkupPercent").GetDecimal()).Should().Be(("BrandAndCategory", 10.01m));
        both.GetProperty("brandName").GetString().Should().StartWith("Brand ");
        both.GetProperty("categoryPath").EnumerateArray().Select(e => e.GetString()).Should().ContainSingle().Which.Should().StartWith("Automation ");
    }

    [PostgresFact]
    public async Task TheGlobalFloor_CannotBeDeleted_ButItsMarkupCanChange()
    {
        var admin = await StaffAsync(Permissions.Roles.Admin);

        (await ErrorCodeAsync(await DeleteAsync(admin, Global, await CodeAsync(admin)), HttpStatusCode.Conflict)).Should().Be("GLOBAL_FLOOR_REQUIRED");
        (await MarkupAsync(Global)).Should().Be(10m);
        (await AuditAsync("margin-floor.deleted")).Should().BeEmpty();
        (await DeleteAsync(admin, Guid.NewGuid(), await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await DataAsync(await PutAsync(admin, Floor("Global", 0m)))).GetProperty("id").GetGuid().Should().Be(Global, "an upsert of the global key changes the one row");
        (await DataAsync(await PutAsync(admin, Floor("Global", 25m)))).GetProperty("minMarkupPercent").GetDecimal().Should().Be(25m);
        await using var db = Db();
        (await db.MarginFloors.CountAsync(f => f.Scope == MarginFloorScope.Global)).Should().Be(1);
        (await AuditAsync("margin-floor.changed", Global)).Select(a => (MarkupIn(a.OldValuesJson), MarkupIn(a.NewValuesJson)))
            .Should().Equal((10m, 0m), (0m, 25m));
    }

    // ---- effective --------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Effective_ReturnsTheRowThePolicyApplies_BrandAndCategoryFirst()
    {
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var brand = await BrandAsync();
        var (root, child) = await CategoriesAsync();
        var variant = await VariantAsync(cost: 1_000, brand, child);

        async Task<(string? Scope, decimal Markup)> EffectiveAsync(string query)
        {
            var data = await DataAsync(await admin.Client.GetAsync($"{Base}/effective?{query}"));
            return (data.GetProperty("floor").GetProperty("scope").GetString(), data.GetProperty("minMarkupPercent").GetDecimal());
        }

        (await EffectiveAsync($"variantId={variant}")).Should().Be(("Global", 10m));
        await DataAsync(await PutAsync(admin, Floor("Category", 15m, categoryId: root)));
        (await EffectiveAsync($"variantId={variant}")).Should().Be(("Category", 15m), "the parent category's row applies to the child");
        await DataAsync(await PutAsync(admin, Floor("Brand", 12m, brand)));
        (await EffectiveAsync($"variantId={variant}")).Should().Be(("Brand", 12m), "brand comes before category, even when lower");
        await DataAsync(await PutAsync(admin, Floor("BrandAndCategory", 8m, brand, root)));
        await DataAsync(await PutAsync(admin, Floor("Category", 30m, categoryId: child)));
        (await EffectiveAsync($"variantId={variant}")).Should().Be(("BrandAndCategory", 8m), "brand + category (here the ancestor) is the most specific level");

        (await EffectiveAsync($"brandId={brand}")).Should().Be(("Brand", 12m));
        (await EffectiveAsync($"categoryId={child}")).Should().Be(("Category", 30m));
        (await EffectiveAsync($"brandId={brand}&categoryId={child}")).Should().Be(("BrandAndCategory", 8m));
        var detail = await DataAsync(await admin.Client.GetAsync($"{Base}/effective?variantId={variant}"));
        (detail.GetProperty("brandId").GetGuid(), detail.GetProperty("categoryId").GetGuid()).Should().Be((brand, child));
        detail.GetProperty("categoryAncestry").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(child, root);

        (await ErrorCodeAsync(await admin.Client.GetAsync($"{Base}/effective"), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorCodeAsync(await admin.Client.GetAsync($"{Base}/effective?variantId={variant}&brandId={brand}"), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await admin.Client.GetAsync($"{Base}/effective?variantId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Client.GetAsync($"{Base}/effective?brandId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- concurrency --------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ParallelUpsertsOfOneKey_LeaveOneRow_LoseNoOldValue_AndTheLastWriteIsVisible()
    {
        var admins = new List<Staff>();
        for (var i = 0; i < 8; i++) admins.Add(await StaffAsync(Permissions.Roles.Admin));
        var brand = await BrandAsync();
        var codes = new List<string>();
        foreach (var a in admins) codes.Add(await CodeAsync(a));

        var responses = await Task.WhenAll(admins.Select((a, i) => PutAsync(a, Floor("Brand", 11m + i, brand), codes[i])));

        foreach (var r in responses) r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        await using var db = Db();
        var row = (await db.MarginFloors.AsNoTracking().Where(f => f.BrandId == brand).ToListAsync()).Should().ContainSingle().Subject;

        // The changes queued: one created the row (no old value), every later one saw the previous one's value as its old value.
        var audit = (await AuditAsync("margin-floor.changed", row.Id)).Select(a => (Old: MarkupIn(a.OldValuesJson), New: MarkupIn(a.NewValuesJson)!.Value)).ToList();
        audit.Should().HaveCount(8);
        audit.Count(a => a.Old is null).Should().Be(1, "exactly one upsert created the row");
        decimal? current = null;
        for (var step = 0; step < 8; step++)
        {
            var next = audit.Where(a => a.Old == current).Should().ContainSingle($"step {step} follows the value {current}").Subject;
            current = next.New;
        }

        row.MinMarkupPercent.Should().Be(current!.Value, "the stored markup is the last write");
    }

    // ---- a floor change never approves anything ---------------------------------------------------------------------------

    private static object Entry(Guid variant, int minQty, long price, string? reason = null) => new { variantId = variant, minQty, unitPriceMinor = price, belowFloorReason = reason };

    private static async Task<(Guid Id, Guid Entry)> PendingAsync(Staff author, Guid variant, long price, string? reason)
    {
        var list = new { scope = "General", name = "D29 list " + Guid.NewGuid().ToString("N")[..6], validFromUtc = DateTime.UtcNow.AddDays(1), validToUtc = DateTime.UtcNow.AddDays(30) };
        var id = (await DataAsync(await author.Client.PostAsJsonAsync(Lists, list), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var filled = await DataAsync(await author.Client.PutAsJsonAsync($"{Lists}/{id}/entries", new { entries = new[] { Entry(variant, 2, price, reason) } }));
        await DataAsync(await author.Client.PostAsync($"{Lists}/{id}/submit", null));
        return (id, filled.GetProperty("entries")[0].GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> ListDetailAsync(Staff who, Guid id) => await DataAsync(await who.Client.GetAsync($"{Lists}/{id}"));

    private async Task<HttpResponseMessage> ApproveAsync(Staff who, Guid id, params Guid[] acknowledged)
    {
        var hash = (await ListDetailAsync(who, id)).GetProperty("contentHash").GetString();
        return await SendAsync(who, HttpMethod.Post, $"{Lists}/{id}/approve", new { expectedContentHash = hash, acknowledgedBelowFloorEntryIds = acknowledged }, await CodeAsync(who));
    }

    private async Task<PriceListEntry> StoredEntryAsync(Guid listId)
    {
        await using var db = Db();
        return (await PriceListTestData.LoadAsync(db, listId)).Entries.Single();
    }

    private static IEnumerable<Guid> BelowFloorIds(JsonElement detail) => detail.GetProperty("belowFloorEntryIds").EnumerateArray().Select(e => e.GetGuid());

    [PostgresFact]
    public async Task LoweringAFloorAfterSubmit_ChangesTheApprovalTimeEvaluation_AndApprovesNothingByItself()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var brand = await BrandAsync();
        // 11,025.00 at 5 % VAT is 10,500.00 net; cost 10,000 → global floor (10 %) 11,000: below the floor at submit.
        var variant = await VariantAsync(cost: 10_000, brand);
        var (id, entry) = await PendingAsync(support, variant, 11_025, "Agreed with the sales head for this account");
        BelowFloorIds(await ListDetailAsync(finance, id)).Should().Equal(entry);
        (await ErrorCodeAsync(await ApproveAsync(finance, id, entry), HttpStatusCode.Forbidden)).Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");

        // A brand row of 5 % (floor 10,500) now applies: the entry is no longer below the floor, but the list is still pending.
        await DataAsync(await PutAsync(admin, Floor("Brand", 5m, brand)));
        var detail = await ListDetailAsync(finance, id);
        (detail.GetProperty("status").GetString(), detail.GetProperty("approvedBy").ValueKind).Should().Be(("PendingApproval", JsonValueKind.Null), "a floor change approves nothing");
        BelowFloorIds(detail).Should().BeEmpty("the evaluation is the one of this moment");

        (await ErrorCodeAsync(await ApproveAsync(finance, id, entry), HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_NOT_ACKNOWLEDGED", "nothing is below the floor any more");
        var approved = await DataAsync(await ApproveAsync(finance, id));
        (approved.GetProperty("status").GetString(), approved.GetProperty("approvedBy").GetGuid()).Should().Be(("Approved", finance.UserId),
            "no acknowledgement and no below-floor right are needed once the entry is above the floor");
        var stored = await StoredEntryAsync(id);
        (stored.CostMinorAtApproval, stored.FloorMinorAtApproval, stored.BelowFloorAtApproval).Should().Be((10_000L, 10_500L, false));
    }

    [PostgresFact]
    public async Task RaisingAFloorAfterSubmit_MakesTheApprovalDemandTheAcknowledgement_WithTheNewIds()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var approver = await StaffAsync(Permissions.Roles.Admin);
        // 5,250.00 at 5 % VAT is 5,000.00 net; cost 1,000 → floor 1,100: above the floor at submit.
        var (withReason, withoutReason) = (await VariantAsync(cost: 1_000), await VariantAsync(cost: 1_000));
        var (id, entry) = await PendingAsync(support, withReason, 5_250, "Kept in case the floor is raised later");
        var (bare, _) = await PendingAsync(support, withoutReason, 5_250, null);
        BelowFloorIds(await ListDetailAsync(approver, id)).Should().BeEmpty();

        // The global floor goes to 500 % (floor 6,000): both entries are below it now.
        await DataAsync(await PutAsync(admin, Floor("Global", 500m)));
        BelowFloorIds(await ListDetailAsync(approver, id)).Should().Equal(entry);

        var refused = await ApproveAsync(approver, id);
        var text = await refused.Content.ReadAsStringAsync();
        (await ErrorCodeAsync(refused, HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_NOT_ACKNOWLEDGED");
        text.Should().Contain(entry.ToString(), "the approver is told the entry that is below the floor now");
        (await ErrorCodeAsync(await ApproveAsync(finance, id, entry), HttpStatusCode.Forbidden)).Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");
        (await ErrorCodeAsync(await ApproveAsync(approver, bare), HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_REASON_REQUIRED", "an entry without a reason cannot be approved below the floor");

        await DataAsync(await ApproveAsync(approver, id, entry));
        var stored = await StoredEntryAsync(id);
        (stored.CostMinorAtApproval, stored.FloorMinorAtApproval, stored.BelowFloorAtApproval).Should().Be((1_000L, 6_000L, true));
    }
}
