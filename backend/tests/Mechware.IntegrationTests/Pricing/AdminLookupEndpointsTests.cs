using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Pricing;

/// <summary>
/// E8.3b: the two staff lookups the console price-list editor needs: variants by SKU or text (drafts included, cost only for
/// <c>pricing.view_cost</c>) and the company picker (legal name, never BIN), plus the company name on price lists.
/// Support edits price lists (no cost), Finance holds view_cost but neither edit nor catalogue management, Admin holds all.
/// </summary>
public sealed class AdminLookupEndpointsTests : IDisposable
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "LookupStaff#2026!";
    private static long _binSeq = 8_300_000_000_000L;
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<HttpClient> StaffAsync(string role)
    {
        _factory.CreateClient().Dispose();
        var email = $"look.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Lookup Staff", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<HttpClient> BuyerAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<(string Raw, JsonElement Data)> GetAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url);
        var raw = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        return (raw, JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone());
    }

    private async Task<(SeededVariant Seed, string Sku)> VariantAsync(long? cost, bool draft = false, string? partNumber = null)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5, priceMinor: 123_400);
        if (cost is { } c) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {c} WHERE id = {seeded.VariantId}");
        if (draft) await db.Products.Where(p => p.Id == seeded.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProductStatus.Draft));
        if (partNumber is not null)
        {
            var product = await db.Products.Include(p => p.Variants).SingleAsync(p => p.Id == seeded.ProductId);
            product.Variants.Single().SetManufacturerPartNumber(partNumber);
            await db.SaveChangesAsync();
        }

        return (seeded, await db.ProductVariants.AsNoTracking().Where(v => v.Id == seeded.VariantId).Select(v => v.Sku).SingleAsync());
    }

    // ---- variants ----------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Variants_AnonymousIs401_BuyerAndFinanceAre403_SupportAndAdminAre200()
    {
        const string url = "/api/v1/admin/variants";
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var buyer = await BuyerAsync();
        (await buyer.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var finance = await StaffAsync(Permissions.Roles.Finance);
        (await finance.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "finance holds neither pricelists.edit nor catalog.manage");
        using var support = await StaffAsync(Permissions.Roles.Support);
        (await support.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        (await admin.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task Variants_ExactSkuHit_IncludesDraftProducts_AndAnotherSkuIsNotMatched()
    {
        var (live, liveSku) = await VariantAsync(cost: null);
        var (draft, draftSku) = await VariantAsync(cost: null, draft: true);
        using var support = await StaffAsync(Permissions.Roles.Support);

        var (_, hit) = await GetAsync(support, $"/api/v1/admin/variants?sku={liveSku.ToLowerInvariant()}");
        hit.GetProperty("items").GetArrayLength().Should().Be(1, "exact SKU, case-insensitive");
        var item = hit.GetProperty("items")[0];
        item.GetProperty("variantId").GetGuid().Should().Be(live.VariantId);
        item.GetProperty("sku").GetString().Should().Be(liveSku);
        item.GetProperty("priceMinor").GetInt64().Should().Be(123_400);
        item.GetProperty("active").GetBoolean().Should().BeTrue();
        item.GetProperty("productStatus").GetString().Should().Be("Active");

        var (_, draftHit) = await GetAsync(support, $"/api/v1/admin/variants?sku={draftSku}");
        draftHit.GetProperty("items")[0].GetProperty("variantId").GetGuid().Should().Be(draft.VariantId);
        draftHit.GetProperty("items")[0].GetProperty("productStatus").GetString().Should().Be("Draft");

        var (_, none) = await GetAsync(support, $"/api/v1/admin/variants?sku={liveSku}-nope");
        none.GetProperty("items").GetArrayLength().Should().Be(0, "an exact lookup never matches a longer SKU");
    }

    [PostgresFact]
    public async Task Variants_TextSearchFindsBySkuFragmentPartNumberAndTitle_AndWildcardsAreLiteral()
    {
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (seed, sku) = await VariantAsync(cost: null, partNumber: $"PN-{tag}-77");
        using var support = await StaffAsync(Permissions.Roles.Support);

        foreach (var q in new[] { sku[4..], $"pn {tag} 77", "Test Product" })
        {
            var (_, data) = await GetAsync(support, $"/api/v1/admin/variants?q={Uri.EscapeDataString(q)}&pageSize=50");
            data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("variantId").GetGuid()).Should().Contain(seed.VariantId, $"q = {q}");
        }

        var (_, wildcard) = await GetAsync(support, "/api/v1/admin/variants?q=" + Uri.EscapeDataString("%"));
        wildcard.GetProperty("items").GetArrayLength().Should().Be(0, "a percent sign is not a wildcard");
    }

    [PostgresFact]
    public async Task Variants_CostPriceOnlyForViewCost_AndPageSizeIsCapped()
    {
        var (_, sku) = await VariantAsync(cost: 80_000);
        await VariantAsync(cost: null);
        using var support = await StaffAsync(Permissions.Roles.Support);
        using var admin = await StaffAsync(Permissions.Roles.Admin);

        var (supportRaw, supportData) = await GetAsync(support, $"/api/v1/admin/variants?sku={sku}");
        supportRaw.ToLowerInvariant().Should().NotContain("cost");
        supportData.GetProperty("items")[0].TryGetProperty("costMinor", out _).Should().BeFalse();

        var (_, adminData) = await GetAsync(admin, $"/api/v1/admin/variants?sku={sku}");
        adminData.GetProperty("items")[0].GetProperty("costMinor").GetInt64().Should().Be(80_000);

        var (_, capped) = await GetAsync(support, "/api/v1/admin/variants?pageSize=5000");
        capped.GetProperty("pageSize").GetInt32().Should().Be(50);
        capped.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(50);
        var (_, second) = await GetAsync(support, "/api/v1/admin/variants?pageSize=1&page=2");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(1);
    }

    // ---- brands and categories ----------------------------------------------------------------------------------------

    private async Task<(Guid Id, string Name, string Slug)> BrandAsync(string name)
    {
        await using var db = Db();
        var brand = new Brand(Guid.NewGuid(), name, "br-" + Guid.NewGuid().ToString("N")[..10], null, false);
        db.Brands.Add(brand);
        await db.SaveChangesAsync();
        return (brand.Id, name, brand.Slug);
    }

    [PostgresFact]
    public async Task BrandAndCategoryLookups_Anonymous401_BuyerAndModerator403_SupportFinanceAdmin200()
    {
        using var anonymous = _factory.CreateClient();
        using var buyer = await BuyerAsync();
        using var moderator = await StaffAsync(Permissions.Roles.Moderator);
        using var support = await StaffAsync(Permissions.Roles.Support);
        using var finance = await StaffAsync(Permissions.Roles.Finance);
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        foreach (var url in new[] { "/api/v1/admin/brands", "/api/v1/admin/categories" })
        {
            (await anonymous.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
            (await buyer.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
            (await moderator.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{url}: moderator holds none of the three permissions");
            (await support.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, $"{url}: pricelists.edit");
            (await finance.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, $"{url}: pricing.view_cost");
            (await admin.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);
        }
    }

    [PostgresFact]
    public async Task BrandLookup_FiltersByNameAndSlugCaseInsensitively_PercentIsLiteral_SortedByName_PageSizeCapped()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var b = await BrandAsync($"Zeta {tag} Drives");
        var a = await BrandAsync($"Alpha {tag} Drives");
        var pct = await BrandAsync($"Pct 100% {tag}");
        using var support = await StaffAsync(Permissions.Roles.Support);

        var (raw, byTag) = await GetAsync(support, $"/api/v1/admin/brands?q={tag.ToUpperInvariant()}");
        byTag.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nameEn").GetString())
            .Should().Equal(a.Name, pct.Name, b.Name);

        var (_, bySlug) = await GetAsync(support, $"/api/v1/admin/brands?q={Uri.EscapeDataString(a.Slug.ToUpperInvariant())}");
        var item = bySlug.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("id").GetGuid().Should().Be(a.Id);
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "nameEn", "nameBn", "slug", "isActive"]);
        item.GetProperty("isActive").GetBoolean().Should().BeTrue();

        var (_, literal) = await GetAsync(support, $"/api/v1/admin/brands?q={Uri.EscapeDataString("100% " + tag)}");
        literal.GetProperty("items").EnumerateArray().Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(pct.Id);
        var (_, wildcard) = await GetAsync(support, "/api/v1/admin/brands?q=" + Uri.EscapeDataString("%"));
        wildcard.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nameEn").GetString())
            .Should().OnlyContain(n => n!.Contains('%'), "a percent sign is not a wildcard");
        var (_, underscore) = await GetAsync(support, "/api/v1/admin/brands?q=" + Uri.EscapeDataString("_"));
        underscore.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nameEn").GetString() + "|" + i.GetProperty("slug").GetString())
            .Should().OnlyContain(n => n.Contains('_'), "an underscore is not a one-character wildcard");

        var (_, capped) = await GetAsync(support, "/api/v1/admin/brands?pageSize=5000");
        capped.GetProperty("pageSize").GetInt32().Should().Be(50);
        capped.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(50);
        var (_, second) = await GetAsync(support, "/api/v1/admin/brands?pageSize=1&page=2");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        raw.ToLowerInvariant().Should().NotContain("cost");
    }

    [PostgresFact]
    public async Task CategoryLookup_CarriesTheEnglishPathRootToNode_AndFiltersByEnglishBanglaNameAndSlug()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var root = Category.CreateRoot(Guid.NewGuid(), $"Root {tag}", $"মূল {tag}", $"lk-root-{tag}");
        var mid = Category.CreateChild(root, Guid.NewGuid(), $"Middle {tag}", $"মধ্য {tag}", $"lk-mid-{tag}");
        var leaf = Category.CreateChild(mid, Guid.NewGuid(), $"Leaf {tag}", $"পাতা {tag}", $"lk-leaf-{tag}");
        await using (var db = Db())
        {
            db.Categories.AddRange(root, mid, leaf);
            await db.SaveChangesAsync();
        }

        using var support = await StaffAsync(Permissions.Roles.Support);
        var (raw, all) = await GetAsync(support, $"/api/v1/admin/categories?q={tag}&pageSize=50");
        var items = all.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("nameEn").GetString()).Should().Equal($"Leaf {tag}", $"Middle {tag}", $"Root {tag}");
        var leafItem = items[0];
        leafItem.GetProperty("path").EnumerateArray().Select(p => p.GetString()).Should().Equal($"Root {tag}", $"Middle {tag}", $"Leaf {tag}");
        leafItem.GetProperty("parentId").GetGuid().Should().Be(mid.Id);
        leafItem.GetProperty("nameBn").GetString().Should().Be($"পাতা {tag}");
        leafItem.GetProperty("isActive").GetBoolean().Should().BeTrue();
        leafItem.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "nameEn", "nameBn", "slug", "parentId", "path", "isActive"]);
        items[1].GetProperty("path").EnumerateArray().Select(p => p.GetString()).Should().Equal($"Root {tag}", $"Middle {tag}");
        items[2].GetProperty("path").GetArrayLength().Should().Be(1);
        items[2].GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
        raw.ToLowerInvariant().Should().NotContain("cost").And.NotContain("commission");

        var (_, byBn) = await GetAsync(support, $"/api/v1/admin/categories?q={Uri.EscapeDataString("মধ্য " + tag)}");
        byBn.GetProperty("items").EnumerateArray().Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(mid.Id);
        var (_, bySlug) = await GetAsync(support, $"/api/v1/admin/categories?q=LK-LEAF-{tag}");
        bySlug.GetProperty("items").EnumerateArray().Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(leaf.Id);
        var (_, byEn) = await GetAsync(support, $"/api/v1/admin/categories?q={Uri.EscapeDataString("middle " + tag.ToUpperInvariant())}");
        byEn.GetProperty("items").GetArrayLength().Should().Be(1);
        var (_, wildcard) = await GetAsync(support, "/api/v1/admin/categories?q=" + Uri.EscapeDataString("%"));
        wildcard.GetProperty("items").GetArrayLength().Should().Be(0, "a percent sign is not a wildcard");

        var (_, capped) = await GetAsync(support, "/api/v1/admin/categories?pageSize=999");
        capped.GetProperty("pageSize").GetInt32().Should().Be(50);
        var (_, second) = await GetAsync(support, "/api/v1/admin/categories?pageSize=1&page=2");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---- companies ---------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, string Name, string Bin)> CompanyAsync(string name, bool active)
    {
        await using var db = Db();
        var bin = Interlocked.Increment(ref _binSeq).ToString("D13");
        var company = Company.Register(Guid.NewGuid(), name, "TL-E83-" + Guid.NewGuid().ToString("N")[..6], bin, null, null);
        if (active) company.Activate();
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (company.Id, name, bin);
    }

    [PostgresFact]
    public async Task CompanyLookup_FiltersByNameAndStatus_NeverShowsOrSearchesTheBin_AndIsStaffOnly()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var acme = await CompanyAsync($"Acme {tag} Looms Ltd", active: true);
        var pending = await CompanyAsync($"Acme {tag} Pending Ltd", active: false);
        var other = await CompanyAsync($"Zenith Works {tag} Ltd", active: true);

        using var buyer = await BuyerAsync();
        (await buyer.GetAsync("/api/v1/admin/companies/lookup")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await buyer.GetAsync("/api/v1/admin/companies?q=acme")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/admin/companies/lookup")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var support = await StaffAsync(Permissions.Roles.Support);
        var (raw, both) = await GetAsync(support, $"/api/v1/admin/companies/lookup?q=acme%20{tag}");
        var names = both.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("legalName").GetString()).ToList();
        names.Should().BeEquivalentTo([acme.Name, pending.Name]);
        raw.Should().NotContain(acme.Bin).And.NotContain("bin", because: "the picker carries no BIN, trade licence or TIN");
        both.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "legalName", "status"]);

        var (_, onlyActive) = await GetAsync(support, $"/api/v1/admin/companies/lookup?q=acme&status=active");
        onlyActive.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Contain(acme.Id).And.NotContain(pending.Id);

        var (_, byBin) = await GetAsync(support, $"/api/v1/admin/companies/lookup?q={acme.Bin}");
        byBin.GetProperty("items").GetArrayLength().Should().Be(0, "the BIN is not searchable");
        var (_, byBinList) = await GetAsync(support, $"/api/v1/admin/companies?q={other.Bin}");
        byBinList.GetProperty("items").GetArrayLength().Should().Be(0);

        var (_, caseless) = await GetAsync(support, $"/api/v1/admin/companies?q=ZENITH%20WORKS%20{tag}");
        caseless.GetProperty("items").GetArrayLength().Should().Be(1);
        caseless.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(other.Id);

        using var badStatus = await support.GetAsync("/api/v1/admin/companies/lookup?status=nonsense");
        badStatus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task PriceListSummaryAndDetail_CarryTheCompanyName_AndGeneralListsCarryNone()
    {
        var company = await CompanyAsync("Named Contract Buyer " + Guid.NewGuid().ToString("N")[..6], active: true);
        using var support = await StaffAsync(Permissions.Roles.Support);
        object Body(string scope, Guid? companyId, string? contract) => new
        {
            scope,
            companyId,
            contractRef = contract,
            name = "E83 " + Guid.NewGuid().ToString("N")[..6],
            validFromUtc = DateTime.UtcNow.AddDays(1),
            validToUtc = DateTime.UtcNow.AddDays(30)
        };

        using var created = await support.PostAsJsonAsync("/api/v1/admin/price-lists", Body("Company", company.Id, "CN-83"));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var detail = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        detail.GetProperty("companyName").GetString().Should().Be(company.Name);
        var id = detail.GetProperty("id").GetGuid();

        using var general = await support.PostAsJsonAsync("/api/v1/admin/price-lists", Body("General", null, null));
        general.StatusCode.Should().Be(HttpStatusCode.Created);
        var generalId = JsonDocument.Parse(await general.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var (raw, list) = await GetAsync(support, "/api/v1/admin/price-lists?pageSize=50");
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Single(i => i.GetProperty("id").GetGuid() == id).GetProperty("companyName").GetString().Should().Be(company.Name);
        items.Single(i => i.GetProperty("id").GetGuid() == generalId).GetProperty("companyName").ValueKind.Should().Be(JsonValueKind.Null);
        raw.Should().NotContain(company.Bin);

        var (_, read) = await GetAsync(support, $"/api/v1/admin/price-lists/{id}");
        read.GetProperty("companyName").GetString().Should().Be(company.Name);
    }
}
