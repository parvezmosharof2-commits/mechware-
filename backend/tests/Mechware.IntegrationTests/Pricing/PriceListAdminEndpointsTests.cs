using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Pricing;

/// <summary>
/// D2.7: the staff API for price lists, part 1 (ADR-0008 §3, §11; threat model §৮ PL4, PL8, PL9, PL12). Real API host, real
/// database, real roles: Support edits (no cost), Finance only reads (cost), Admin does both.
/// </summary>
public sealed class PriceListAdminEndpointsTests : IDisposable
{
    private const string Base = "/api/v1/admin/price-lists";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "PriceStaff#2026!";
    private static long _binSeq = 8_100_000_000_000L;
    private static readonly string[] CostMembers = ["costMinor", "floorMinor", "belowFloor", "belowCost", "costUnknown"];
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    // ---- sign-in -----------------------------------------------------------------------------------------------------

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role)
    {
        _factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"price.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Price Staff", "en");
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

    private async Task<HttpClient> BuyerAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    // ---- data --------------------------------------------------------------------------------------------------------

    /// <summary>A published variant with the given VAT-exclusive cost (null = no cost on file).</summary>
    private async Task<Guid> VariantAsync(long? cost = null)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
        if (cost is { } c) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {c} WHERE id = {seeded.VariantId}");
        return seeded.VariantId;
    }

    private async Task<Guid> CompanyAsync(bool active)
    {
        await using var db = Db();
        var company = Company.Register(Guid.NewGuid(), "Price Buyer Ltd", "TL-D27-" + Guid.NewGuid().ToString("N")[..6],
            Interlocked.Increment(ref _binSeq).ToString("D13"), null, null);
        if (active) company.Activate();
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static object NewList(string scope = "General", Guid? companyId = null, string? contractRef = null) => new
    {
        scope,
        companyId,
        contractRef,
        name = "D27 list " + Guid.NewGuid().ToString("N")[..6],
        validFromUtc = DateTime.UtcNow.AddDays(1),
        validToUtc = DateTime.UtcNow.AddDays(30),
    };

    private static PriceListTestData.Entry Row(Guid v, int q, long p) => new(v, q, p);

    private static object Entry(Guid variant, int minQty, long price, string? reason = null) => new { variantId = variant, minQty, unitPriceMinor = price, belowFloorReason = reason };

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> CreateListAsync(HttpClient client, object? body = null) =>
        (await DataAsync(await client.PostAsJsonAsync(Base, body ?? NewList()), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> PutEntries(HttpClient c, Guid list, params object[] entries) =>
        c.PutAsJsonAsync($"{Base}/{list}/entries", new { entries });

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

    private async Task<PriceList> LoadAsync(Guid id)
    {
        await using var db = Db();
        return await PriceListTestData.LoadAsync(db, id);
    }

    private async Task<int> AuditCountAsync(string action, Guid listId)
    {
        await using var db = Db();
        return await db.AuditLogs.CountAsync(a => a.Action == action && a.EntityId == listId.ToString());
    }

    // ---- who may call ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Permissions_EveryEndpoint_AnonymousIs401_BuyerIs403_FinanceReadsOnly_SupportAndAdminEditButNeverApprove()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        using var buyer = await BuyerAsync();
        using var anonymous = _factory.CreateClient();
        var variant = await VariantAsync(cost: 1_000);
        var id = await CreateListAsync(support.Client);
        var stranger = Guid.NewGuid();

        // (method, path, body, needs edit)
        (HttpMethod, string, object?, bool)[] endpoints =
        [
            (HttpMethod.Get, Base, null, false),
            (HttpMethod.Get, $"{Base}/{id}", null, false),
            (HttpMethod.Post, Base, NewList(), true),
            (HttpMethod.Put, $"{Base}/{id}", new { name = "Renamed", validFromUtc = DateTime.UtcNow.AddDays(1), validToUtc = DateTime.UtcNow.AddDays(9) }, true),
            (HttpMethod.Put, $"{Base}/{id}/entries", new { entries = new[] { Entry(variant, 2, 5_000) } }, true),
            (HttpMethod.Delete, $"{Base}/{id}/entries/{stranger}", null, true),
            (HttpMethod.Post, $"{Base}/{stranger}/submit", null, true),
            (HttpMethod.Post, $"{Base}/{stranger}/revise", null, true),
            (HttpMethod.Delete, $"{Base}/{stranger}", null, true),
        ];
        HttpRequestMessage Build((HttpMethod M, string P, object? B, bool E) e) => new(e.M, e.P) { Content = e.B is null ? null : JsonContent.Create(e.B) };

        foreach (var e in endpoints)
        {
            (await anonymous.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{e.Item1} {e.Item2}");
            (await buyer.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"buyer {e.Item1} {e.Item2}");
            var asFinance = await finance.Client.SendAsync(Build(e));
            if (e.Item4) asFinance.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"finance {e.Item1} {e.Item2}");
            else asFinance.StatusCode.Should().Be(HttpStatusCode.OK, $"finance {e.Item1} {e.Item2}");
            foreach (var who in new[] { support, admin })
                (await who.Client.SendAsync(Build(e))).StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized, $"{e.Item1} {e.Item2}");
        }

        // Approving is not part of this unit and not in Support's hands (D2.8 builds it with step-up).
        Permissions.GetDefaultPermissionsForRole(Permissions.Roles.Support).Should().NotContain(Permissions.PriceListsApprove)
            .And.NotContain(Permissions.PricingViewCost);
        (await support.Client.PostAsync($"{Base}/{id}/approve", null)).IsSuccessStatusCode.Should().BeFalse();
    }

    // ---- exit tests --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task CreateOrEdit_CannotSetStatusOrApprover()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var variant = await VariantAsync(cost: 1_000);
        var (foreignSeries, foreignUser) = (Guid.NewGuid(), Guid.NewGuid());
        var extras = new
        {
            id = Guid.NewGuid(),
            seriesId = foreignSeries,
            version = 9,
            status = "Approved",
            approvedBy = foreignUser,
            approvedAtUtc = DateTime.UtcNow,
            createdBy = foreignUser,
            submittedBy = foreignUser,
            contentHash = new string('a', 64),
            effectiveFromUtc = DateTime.UtcNow,
        };
        var body = new Dictionary<string, object?>
        {
            ["scope"] = "General",
            ["name"] = "Body with extras",
            ["validFromUtc"] = DateTime.UtcNow.AddDays(1),
            ["validToUtc"] = DateTime.UtcNow.AddDays(9),
        };
        foreach (var p in extras.GetType().GetProperties()) body[p.Name] = p.GetValue(extras);

        var created = await DataAsync(await support.Client.PostAsJsonAsync(Base, body), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().Should().Be("Draft");
        created.GetProperty("version").GetInt32().Should().Be(1);
        created.GetProperty("createdBy").GetGuid().Should().Be(support.UserId, "the creator comes from the signed-in user");
        created.GetProperty("seriesId").GetGuid().Should().NotBe(foreignSeries);
        id.Should().NotBe((Guid)extras.id);
        created.GetProperty("approvedBy").ValueKind.Should().Be(JsonValueKind.Null);

        var header = new Dictionary<string, object?>(extras.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(extras)))
        {
            ["name"] = "Edited with extras",
            ["validFromUtc"] = DateTime.UtcNow.AddDays(1),
            ["validToUtc"] = DateTime.UtcNow.AddDays(8),
        };
        await DataAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}", header));
        var entry = Entry(variant, 2, 9_000).GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(Entry(variant, 2, 9_000)));
        entry["status"] = "Approved";
        entry["id"] = Guid.NewGuid();
        await DataAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}/entries", new { entries = new[] { entry }, status = "Approved", approvedBy = foreignUser }));

        var stored = await LoadAsync(id);
        stored.Status.Should().Be(PriceListStatus.Draft);
        (stored.Version, stored.CreatedBy, stored.ApprovedBy, stored.SubmittedBy, stored.ContentHash).Should().Be((1, support.UserId, null, null, null));
        stored.SeriesId.Should().NotBe(foreignSeries);
        stored.Name.Should().Be("Edited with extras");
        stored.Entries.Should().ContainSingle().Which.Id.Should().NotBe((Guid)entry["id"]!);
    }

    [PostgresFact]
    public async Task StaffWithoutViewCost_SeesNoCostFields()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        // cost 123,457 -> floor (10 %) 135,803; price 130,000 is below the floor but above cost.
        var variant = await VariantAsync(cost: 123_457);
        var listId = await CreateListAsync(support.Client);
        await DataAsync(await PutEntries(support.Client, listId, Entry(variant, 2, 130_000, "Agreed with the buyer's purchasing head")));

        void NoCost(JsonElement json) => PropertyNames(json).Should().NotIntersectWith(CostMembers);
        NoCost(await DataAsync(await support.Client.GetAsync(Base)));
        NoCost(await DataAsync(await support.Client.GetAsync($"{Base}/{listId}")));
        var submitted = await DataAsync(await support.Client.PostAsync($"{Base}/{listId}/submit", null));
        NoCost(submitted);
        submitted.GetProperty("status").GetString().Should().Be("PendingApproval");

        // The error for a below-floor entry without a reason names the entry, never an amount.
        var second = await CreateListAsync(support.Client);
        var entryId = (await DataAsync(await PutEntries(support.Client, second, Entry(variant, 2, 130_000)))).GetProperty("entries")[0].GetProperty("id").GetGuid();
        var refused = await support.Client.PostAsync($"{Base}/{second}/submit", null);
        var errorText = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, errorText);
        errorText.Should().Contain(entryId.ToString()).And.NotContain("123457").And.NotContain("135803").And.NotContain("130000")
            .And.NotMatchRegex("(?i)\"(cost|floor)Minor\"");

        // The admin holds view_cost: the same list shows cost, floor and flag, and the error carries amounts.
        var detail = await DataAsync(await admin.Client.GetAsync($"{Base}/{listId}"));
        var line = detail.GetProperty("entries")[0];
        (line.GetProperty("costMinor").GetInt64(), line.GetProperty("floorMinor").GetInt64(), line.GetProperty("belowFloor").GetBoolean())
            .Should().Be((123_457L, 135_803L, true));
        (await admin.Client.PostAsync($"{Base}/{second}/submit", null)).Content.ReadAsStringAsync().Result.Should().Contain("135803");
    }

    // ---- create, read, list -----------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task CompanyList_NeedsAnExistingActiveCompany_AndAContractRef()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var pending = await CompanyAsync(active: false);
        var active = await CompanyAsync(active: true);

        (await support.Client.PostAsJsonAsync(Base, NewList("Company", Guid.NewGuid(), "CN-1"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no such company");
        (await support.Client.PostAsJsonAsync(Base, NewList("Company", pending, "CN-1"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "company not active");
        (await support.Client.PostAsJsonAsync(Base, NewList("Company", null, "CN-1"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await support.Client.PostAsJsonAsync(Base, NewList("Company", active, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a contract needs its reference");
        (await support.Client.PostAsJsonAsync(Base, NewList("General", active))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a public list has no company");
        (await support.Client.PostAsJsonAsync(Base, NewList("Group"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "group lists come later");
        (await support.Client.PostAsJsonAsync(Base, NewList("3"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "numbers are not scope names");

        var ok = await DataAsync(await support.Client.PostAsJsonAsync(Base, NewList("Company", active, "CN-2026-7")), HttpStatusCode.Created);
        (ok.GetProperty("scope").GetString(), ok.GetProperty("companyId").GetGuid(), ok.GetProperty("contractRef").GetString()).Should().Be(("Company", active, "CN-2026-7"));
    }

    [PostgresFact]
    public async Task List_IsPagedAndCapped_FiltersAndShowsNewestFirst_WithTheComputedDisplayState()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var marker = "zz" + Guid.NewGuid().ToString("N")[..8];
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await CreateListAsync(support.Client, new { scope = "General", name = $"{marker} list {i}", validFromUtc = DateTime.UtcNow.AddDays(1), validToUtc = DateTime.UtcNow.AddDays(9) }));
            await Task.Delay(20);
        }

        var page = await DataAsync(await support.Client.GetAsync($"{Base}?q={marker}&pageSize=2"));
        page.GetProperty("total").GetInt32().Should().Be(3);
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(new[] { ids[2], ids[1] }, "newest first");
        var item = page.GetProperty("items")[0];
        (item.GetProperty("status").GetString(), item.GetProperty("displayState").GetString(), item.GetProperty("entryCount").GetInt32()).Should().Be(("Draft", "Draft", 0));
        (await DataAsync(await support.Client.GetAsync($"{Base}?pageSize=5000"))).GetProperty("pageSize").GetInt32().Should().Be(50);
        (await DataAsync(await support.Client.GetAsync($"{Base}?q={marker}&status=Approved"))).GetProperty("total").GetInt32().Should().Be(0);
        (await DataAsync(await support.Client.GetAsync($"{Base}?q={marker}&scope=Company"))).GetProperty("total").GetInt32().Should().Be(0);
        (await support.Client.GetAsync($"{Base}?status=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Approved lists: active, scheduled, expired, superseded come from the clock and the series.
        await using var db = Db();
        var (v1, v2) = (await VariantAsync(), await VariantAsync());
        var now = PriceListTestData.WholeSecond(DateTime.UtcNow);
        var active = await PriceListTestData.ApprovedAsync(db, PriceListScope.General, null, now.AddHours(-4), now.AddDays(2), now.AddHours(-3), Row(v1, 2, 900));
        var revised = await PriceListTestData.ReviseAsync(db, active.Id, now.AddHours(-1), [Row(v1, 2, 800)]);
        var scheduled = await PriceListTestData.ApprovedAsync(db, PriceListScope.General, null, now.AddDays(3), now.AddDays(9), now.AddMinutes(-5), Row(v2, 2, 900));
        var expired = await PriceListTestData.ApprovedAsync(db, PriceListScope.General, null, now.AddDays(-3), now.AddDays(-1), now.AddDays(-3), Row(v2, 3, 900));
        async Task<string> State(Guid id) => (await DataAsync(await support.Client.GetAsync($"{Base}/{id}"))).GetProperty("displayState").GetString()!;
        (await State(active.Id), await State(revised.Id), await State(scheduled.Id), await State(expired.Id)).Should().Be(("Superseded", "Active", "Scheduled", "Expired"));
    }

    // ---- editing -----------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Entries_AreUpsertedAndRemoved_AndEveryEditorBecomesAContributor()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var (v1, v2) = (await VariantAsync(), await VariantAsync());
        var id = await CreateListAsync(support.Client);

        var d = await DataAsync(await PutEntries(support.Client, id, Entry(v1, 2, 9_000), Entry(v1, 10, 8_000), Entry(v2, 2, 7_000)));
        d.GetProperty("entries").GetArrayLength().Should().Be(3);
        var again = await DataAsync(await PutEntries(admin.Client, id, Entry(v1, 2, 8_500, "Matching the competitor's offer")));
        again.GetProperty("entries").GetArrayLength().Should().Be(3, "same variant and quantity is an update");
        again.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("variantId").GetGuid() == v1 && e.GetProperty("minQty").GetInt32() == 2)
            .GetProperty("unitPriceMinor").GetInt64().Should().Be(8_500);
        again.GetProperty("contributors").EnumerateArray().Select(c => c.GetGuid()).Should().BeEquivalentTo([support.UserId, admin.UserId]);

        var gone = again.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("variantId").GetGuid() == v2).GetProperty("id").GetGuid();
        (await DataAsync(await admin.Client.DeleteAsync($"{Base}/{id}/entries/{gone}"))).GetProperty("entries").GetArrayLength().Should().Be(2);
        (await admin.Client.DeleteAsync($"{Base}/{id}/entries/{gone}")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no such entry any more");

        foreach (var bad in new[] { Entry(v1, 2, 0), Entry(v1, 2, -5), Entry(v1, 0, 100), Entry(v1, 1, 100), Entry(v1, 2, long.MaxValue), Entry(v1, 2, 100, "no") })
            (await PutEntries(support.Client, id, bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad));
        (await PutEntries(support.Client, id, Entry(v1, 2, 100), Entry(v1, 2, 200))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "duplicate in one request");
    }

    [PostgresFact]
    public async Task EntryBatch_IsCapped_AndAnUnknownOrInactiveVariant_IsRefusedWithoutSavingAnything()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var good = await VariantAsync();
        var inactive = await VariantAsync();
        await using (var db = Db())
        {
            (await db.ProductVariants.SingleAsync(v => v.Id == inactive)).Deactivate();
            await db.SaveChangesAsync();
        }
        var id = await CreateListAsync(support.Client);

        var tooMany = Enumerable.Range(2, 501).Select(q => Entry(good, q, 100)).ToArray();
        (await ErrorCodeAsync(await PutEntries(support.Client, id, tooMany), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await PutEntries(support.Client, id)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "an empty batch");

        var unknown = Guid.NewGuid();
        var refused = await PutEntries(support.Client, id, Entry(good, 2, 5_000), Entry(inactive, 2, 5_000), Entry(unknown, 2, 5_000));
        var text = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain(inactive.ToString()).And.Contain(unknown.ToString());
        (await LoadAsync(id)).Entries.Should().BeEmpty("the good entry in the same batch was not saved either");
    }

    [PostgresFact]
    public async Task AListThatIsPendingOrApproved_CannotBeEdited_AndOnlyADraftCanBeDeleted()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var variant = await VariantAsync(cost: 1_000);
        var pending = await CreateListAsync(support.Client);
        await PutEntries(support.Client, pending, Entry(variant, 2, 5_000));
        await DataAsync(await support.Client.PostAsync($"{Base}/{pending}/submit", null));
        await using var db = Db();
        var now = PriceListTestData.WholeSecond(DateTime.UtcNow);
        var approved = await PriceListTestData.ApprovedAsync(db, PriceListScope.General, null, now.AddDays(1), now.AddDays(9), now, Row(await VariantAsync(), 2, 900));
        var entryId = approved.Entries.Single().Id;

        foreach (var id in new[] { pending, approved.Id })
        {
            var window = new { name = "Changed", validFromUtc = now.AddDays(1), validToUtc = now.AddDays(9) };
            (await ErrorCodeAsync(await support.Client.PutAsJsonAsync($"{Base}/{id}", window), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_EDITABLE");
            (await ErrorCodeAsync(await PutEntries(support.Client, id, Entry(variant, 2, 1)), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_EDITABLE");
            (await ErrorCodeAsync(await support.Client.DeleteAsync($"{Base}/{id}/entries/{entryId}"), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_EDITABLE");
            (await ErrorCodeAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_EDITABLE");
            (await ErrorCodeAsync(await support.Client.DeleteAsync($"{Base}/{id}"), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_DELETABLE");
            (await LoadAsync(id)).Should().NotBeNull("it was not deleted");
        }

        (await LoadAsync(pending)).Entries.Single().UnitPriceMinor.Should().Be(5_000, "the refused edit changed nothing");
    }

    [PostgresFact]
    public async Task DeletingADraft_RemovesItWithItsEntriesAndContributors_AndIsAudited()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var id = await CreateListAsync(support.Client);
        await PutEntries(support.Client, id, Entry(await VariantAsync(), 2, 5_000));

        (await support.Client.DeleteAsync($"{Base}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await support.Client.GetAsync($"{Base}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await support.Client.DeleteAsync($"{Base}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = Db();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM price_list_entries WHERE price_list_id = {id}").SingleAsync()).Should().Be(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM price_list_contributors WHERE price_list_id = {id}").SingleAsync()).Should().Be(0);
        (await AuditCountAsync("pricelist.deleted", id)).Should().Be(1);
        (await db.AuditLogs.SingleAsync(a => a.Action == "pricelist.deleted" && a.EntityId == id.ToString())).ActorId.Should().Be(support.UserId);
    }

    [PostgresFact]
    public async Task ConcurrentEditsOfOneDraft_NeverLoseAnUpdate()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var id = await CreateListAsync(support.Client);
        var variants = new List<Guid>();
        for (var i = 0; i < 6; i++) variants.Add(await VariantAsync());

        var responses = await Task.WhenAll(variants.Select(v => PutEntries(support.Client, id, Entry(v, 2, 5_000))));
        responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
        var saved = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        saved.Should().BeGreaterThan(0);
        (await LoadAsync(id)).Entries.Should().HaveCount(saved, "every edit that was answered 200 is stored, every other one was refused with 409 (edits queue on the row lock, so normally all are 200)");
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            (await r.Content.ReadAsStringAsync()).Should().Contain("CONCURRENT_MODIFICATION");
    }

    [PostgresFact]
    public async Task AStaleEdit_ThatBypassesTheRowLock_IsRefusedByTheConcurrencyToken_NotSilentlyLost()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var id = await CreateListAsync(support.Client);
        await using var first = Db();
        await using var second = Db();
        var a = await first.PriceLists.Include(p => p.Entries).Include(p => p.Contributors).SingleAsync(p => p.Id == id);
        var b = await second.PriceLists.Include(p => p.Entries).Include(p => p.Contributors).SingleAsync(p => p.Id == id);

        a.ChangeHeader("First editor", null, a.ValidFromUtc, a.ValidToUtc, Guid.NewGuid(), DateTime.UtcNow);
        await first.SaveChangesAsync();
        b.ChangeHeader("Second editor", null, b.ValidFromUtc, b.ValidToUtc, Guid.NewGuid(), DateTime.UtcNow);
        var stale = async () => await second.SaveChangesAsync();

        await stale.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await LoadAsync(id)).Name.Should().Be("First editor");
    }

    // ---- submit and revise ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Submit_RefusesBelowFloorWithoutAReason_AndBelowCostAlways_ThenStoresTheHash()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        var costly = await VariantAsync(cost: 10_000);   // floor 11,000
        var noCost = await VariantAsync();
        var id = await CreateListAsync(support.Client);

        (await ErrorCodeAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", "no entries");
        var added = await DataAsync(await PutEntries(support.Client, id, Entry(costly, 2, 10_500), Entry(noCost, 2, 4_000)));
        Guid IdOf(Guid variant) => added.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("variantId").GetGuid() == variant).GetProperty("id").GetGuid();
        var ids = new[] { IdOf(costly), IdOf(noCost) };

        var refused = await support.Client.PostAsync($"{Base}/{id}/submit", null);
        var text = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain(ids[0].ToString()).And.Contain(ids[1].ToString(), "a variant without cost counts as below floor");
        (await LoadAsync(id)).Status.Should().Be(PriceListStatus.Draft);

        await DataAsync(await PutEntries(support.Client, id, Entry(costly, 2, 9_000, "Clearance of old stock"), Entry(noCost, 2, 4_000, "Cost to be confirmed")));
        var belowCost = await support.Client.PostAsync($"{Base}/{id}/submit", null);
        (await belowCost.Content.ReadAsStringAsync()).Should().Contain(ids[0].ToString(), "below cost is refused even with a reason");
        belowCost.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await DataAsync(await PutEntries(support.Client, id, Entry(costly, 2, 10_500, "Strategic account, approved by sales head")));
        var done = await DataAsync(await support.Client.PostAsync($"{Base}/{id}/submit", null));
        var stored = await LoadAsync(id);
        (stored.Status, stored.SubmittedBy).Should().Be((PriceListStatus.PendingApproval, support.UserId));
        done.GetProperty("contentHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$").And.Be(stored.ContentHash).And.Be(stored.ComputeContentHash());
        done.GetProperty("status").GetString().Should().Be("PendingApproval");
        (await AuditCountAsync("pricelist.submitted", id)).Should().Be(1);

        // Staff who may see cost get the per-entry evaluation with the submit result; Support did not.
        var shown = await DataAsync(await admin.Client.GetAsync($"{Base}/{id}"));
        shown.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("variantId").GetGuid() == costly).GetProperty("belowFloor").GetBoolean().Should().BeTrue();
        PropertyNames(done).Should().NotIntersectWith(CostMembers);
    }

    [PostgresFact]
    public async Task Revise_MakesVersionPlusOneAsADraftOfTheReviserAlone_AndOnlyOnceFromTheLatestDecidedVersion()
    {
        var support = await StaffAsync(Permissions.Roles.Support);
        var openDraft = await CreateListAsync(support.Client);
        (await ErrorCodeAsync(await support.Client.PostAsync($"{Base}/{openDraft}/revise", null), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_REVISABLE");

        await using var db = Db();
        var now = PriceListTestData.WholeSecond(DateTime.UtcNow);
        var v1 = await PriceListTestData.ApprovedAsync(db, PriceListScope.General, null, now.AddDays(1), now.AddDays(9), now, Row(await VariantAsync(), 2, 900), Row(await VariantAsync(), 3, 800));

        var v2 = await DataAsync(await support.Client.PostAsync($"{Base}/{v1.Id}/revise", null), HttpStatusCode.Created);
        (v2.GetProperty("version").GetInt32(), v2.GetProperty("status").GetString(), v2.GetProperty("seriesId").GetGuid(), v2.GetProperty("createdBy").GetGuid())
            .Should().Be((2, "Draft", v1.SeriesId, support.UserId));
        v2.GetProperty("contributors").EnumerateArray().Select(c => c.GetGuid()).Should().Equal(support.UserId);
        v2.GetProperty("entries").GetArrayLength().Should().Be(2);
        v2.GetProperty("contentHash").ValueKind.Should().Be(JsonValueKind.Null);
        (await AuditCountAsync("pricelist.revised", v2.GetProperty("id").GetGuid())).Should().Be(1);

        (await ErrorCodeAsync(await support.Client.PostAsync($"{Base}/{v1.Id}/revise", null), HttpStatusCode.Conflict)).Should().Be("PRICE_LIST_NOT_LATEST");
    }
}
