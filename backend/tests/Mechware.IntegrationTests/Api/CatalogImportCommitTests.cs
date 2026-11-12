using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Mechware.Domain.Catalog;
using Mechware.Domain.Identity;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Api;

/// <summary>C10.2 exit test: the same file twice leaves one copy of everything; a bad row writes nothing; overlapping commits do not duplicate.</summary>
public sealed class CatalogImportCommitTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TotpService Totp = new();
    private readonly DefaultApiFactory _factory = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    private Guid _sellerId;

    public void Dispose() => _factory.Dispose();

    private string BrandSlug => $"it-brand-{_run}";
    private string CatSlug => $"it-cat-{_run}";
    private string Current => $"it{_run}_current";
    private string Breaking => $"it{_run}_breaking";
    private string Shielded => $"it{_run}_shielded";
    private string Series => $"it{_run}_series";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task SeedAsync()
    {
        await using var db = Db();
        var user = User.Create($"imp-{_run}@example.test", null, null, "Import Seller");
        var seller = new Seller(Guid.NewGuid(), user.Id, $"Import Seller {_run}", $"import-seller-{_run}", null, null, null, 4.5m, 10, 99m, DateTime.UtcNow);
        seller.Approve(DateTime.UtcNow);
        _sellerId = seller.Id;
        db.Users.Add(user);
        db.Sellers.Add(seller);
        db.Brands.Add(new Brand(Guid.NewGuid(), $"IT Brand {_run}", BrandSlug, null, false));
        db.Categories.Add(Category.CreateRoot(Guid.NewGuid(), "IT category", "আইটি বিভাগ", CatSlug));
        db.Attributes.Add(new AttributeDefinition(Guid.NewGuid(), "Rated current", "রেটেড কারেন্ট", Current, AttributeDataType.Number, "A", true));
        db.Attributes.Add(new AttributeDefinition(Guid.NewGuid(), "Breaking capacity", "ব্রেকিং", Breaking, AttributeDataType.Number, "A", true));
        db.Attributes.Add(new AttributeDefinition(Guid.NewGuid(), "Shielded", "শিল্ডেড", Shielded, AttributeDataType.Boolean, null, true));
        db.Attributes.Add(new AttributeDefinition(Guid.NewGuid(), "Series", "সিরিজ", Series, AttributeDataType.String, null, true));
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> AdminAsync()
    {
        HttpClient client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = "admin@mechware.com.bd",
            password = "MechAdmin#2026!",
            totpCode = Totp.ComputeTotpCode("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", DateTimeOffset.UtcNow)
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    private string Header() =>
        $"product_slug,product_title_en,product_title_bn,brand,category,sku,manufacturer_part_number,price_bdt,min_order_qty,pack_size,supply_status,datasheet_url,attr:{Current},attr:{Breaking},attr:{Shielded},attr:{Series}\r\n";

    /// <summary>Product n has variants a (always) and b (when n is even).</summary>
    private string Rows(int from, int to, string price = "1250.50", string? series = null)
    {
        var sb = new StringBuilder();
        for (int n = from; n < to; n++)
        {
            foreach (var v in n % 2 == 0 ? new[] { "a", "b" } : new[] { "a" })
            {
                sb.Append($"it-{_run}-{n},Relay {n},রিলে {n},{BrandSlug},{CatSlug},SKU-{_run}-{n}{v},IT{_run}-{n:D4}{v},{price},{(v == "a" ? "4" : "")},{(v == "a" ? "2" : "")},active,,10 A,6 kA,yes,{series ?? "Series " + n % 3}\r\n");
            }
        }

        return sb.ToString();
    }

    private static MultipartFormDataContent Upload(string csv, Guid? sellerId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new UTF8Encoding(false).GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "import.csv");
        if (sellerId is { } id) form.Add(new StringContent(id.ToString()), "sellerId");
        return form;
    }

    private async Task<(HttpStatusCode Status, JsonElement Root)> CommitAsync(HttpClient admin, string csv)
    {
        var response = await admin.PostAsync("/api/v1/admin/catalog/import/commit", Upload(csv, _sellerId));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private async Task<(int Products, int Variants, int Outbox, int Links, int Values)> CountsAsync()
    {
        await using var db = Db();
        var ids = await db.Products.Where(p => p.Slug.StartsWith($"it-{_run}-")).Select(p => p.Id).ToListAsync();
        return (ids.Count, await db.ProductVariants.CountAsync(v => v.Sku.StartsWith($"SKU-{_run.ToUpperInvariant()}")),
            await db.OutboxMessages.CountAsync(o => o.EventType == nameof(ProductUpdatedEvent) && ids.Contains(o.AggregateId)),
            await db.ProductAttributeValues.CountAsync(l => ids.Contains(l.ProductId)),
            await db.Attributes.Where(a => a.Code.StartsWith($"it{_run}")).SelectMany(a => a.Values).CountAsync());
    }

    [PostgresFact]
    public async Task Only_staff_with_catalog_manage_may_commit()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.PostAsync("/api/v1/admin/catalog/import/commit", Upload("", null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var buyer = _factory.CreateClient();
        var login = await buyer.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        buyer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        (await buyer.PostAsync("/api/v1/admin/catalog/import/commit", Upload("", null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [PostgresFact]
    public async Task A_valid_file_is_written_and_the_same_file_again_changes_nothing()
    {
        await SeedAsync();
        using var admin = await AdminAsync();
        string csv = Header() + Rows(0, 10);   // 10 products, 15 variants
        string sha = Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(csv)));

        var first = await CommitAsync(admin, csv);
        first.Status.Should().Be(HttpStatusCode.OK, first.Root.ToString());
        var d = first.Root.GetProperty("data");
        d.GetProperty("sha256").GetString().Should().Be(sha);
        d.GetProperty("createdProducts").GetInt32().Should().Be(10);
        d.GetProperty("createdVariants").GetInt32().Should().Be(15);
        d.GetProperty("updatedProducts").GetInt32().Should().Be(0);

        await using (var db = Db())
        {
            var products = await db.Products.Include(p => p.Variants).Include(p => p.AttributeValues).Where(p => p.Slug.StartsWith($"it-{_run}-")).ToListAsync();
            products.Should().HaveCount(10).And.OnlyContain(p => p.Status == ProductStatus.Draft && p.SellerId == _sellerId);
            var p4 = products.Single(p => p.Slug == $"it-{_run}-4");
            p4.TitleBn.Should().Be("রিলে 4");
            p4.Variants.Should().HaveCount(2);
            var a = p4.Variants.Single(v => v.Sku == $"SKU-{_run.ToUpperInvariant()}-4A");
            a.PriceMinor.Should().Be(125_050);
            (a.MinOrderQty, a.PackSize).Should().Be((4, 2));
            a.PartNumberKey.Should().Be(PartNumbers.Normalise($"IT{_run}-0004a"));
            p4.Variants.Single(v => v.Sku == $"SKU-{_run.ToUpperInvariant()}-4B").MinOrderQty.Should().Be(1);
            p4.AttributeValues.Should().HaveCount(4);
            var values = await db.AttributeValues.Where(v => p4.AttributeValues.Select(l => l.AttributeValueId).Contains(v.Id)).ToListAsync();
            values.Single(v => v.ValueEn == "6 kA").NumericValue.Should().Be(6000m);   // stored in the attribute's own unit (A)
            values.Should().Contain(v => v.ValueEn == "true");
        }

        var counts = await CountsAsync();
        counts.Outbox.Should().BeGreaterThanOrEqualTo(10);
        await using (var ob = Db())
        {
            var slugs = $"it-{_run}-";
            var ids = ob.Products.Where(p => p.Slug.StartsWith(slugs)).Select(p => p.Id);
            (await ob.OutboxMessages.Where(o => o.EventType == nameof(ProductUpdatedEvent) && ids.Contains(o.AggregateId)).Select(o => o.AggregateId).Distinct().CountAsync())
                .Should().Be(10, "every created product queues a ProductUpdatedEvent, which the search indexer consumes");
        }
        await using (var db = Db())
        {
            var summary = await db.AuditLogs.SingleAsync(l => l.Action == "catalog.import.committed" && l.EntityId == sha);
            summary.NewValuesJson.Should().MatchRegex("\"CreatedProducts\": ?10");
            summary.ActorId.Should().NotBeNull();
        }

        var second = await CommitAsync(admin, csv);
        second.Status.Should().Be(HttpStatusCode.OK, second.Root.ToString());
        var e = second.Root.GetProperty("data");
        e.GetProperty("createdProducts").GetInt32().Should().Be(0);
        e.GetProperty("createdVariants").GetInt32().Should().Be(0);
        e.GetProperty("updatedProducts").GetInt32().Should().Be(0);
        e.GetProperty("updatedVariants").GetInt32().Should().Be(0);
        e.GetProperty("unchangedProducts").GetInt32().Should().Be(10);
        e.GetProperty("unchangedVariants").GetInt32().Should().Be(15);
        (await CountsAsync()).Should().Be(counts, "no new rows, no new events, no new attribute values");

        // A third commit that changes one price and one attribute, and leaves the other fields blank: only those change.
        string changed = Header() + Rows(0, 10).Replace($"it-{_run}-3,Relay 3,রিলে 3,{BrandSlug},{CatSlug},SKU-{_run}-3a,IT{_run}-0003a,1250.50,4,2,active,,10 A,6 kA,yes,Series 0",
            $"it-{_run}-3,Relay 3,রিলে 3,{BrandSlug},{CatSlug},SKU-{_run}-3a,IT{_run}-0003a,1300,,,,,10 A,6 kA,yes,Series 9");
        var third = await CommitAsync(admin, changed);
        third.Status.Should().Be(HttpStatusCode.OK, third.Root.ToString());
        var f = third.Root.GetProperty("data");
        f.GetProperty("updatedProducts").GetInt32().Should().Be(1);
        f.GetProperty("updatedVariants").GetInt32().Should().Be(1);
        f.GetProperty("unchangedProducts").GetInt32().Should().Be(9);
        await using (var db = Db())
        {
            var v = await db.ProductVariants.SingleAsync(x => x.Sku == $"SKU-{_run.ToUpperInvariant()}-3A");
            v.PriceMinor.Should().Be(130_000);
            (v.MinOrderQty, v.PackSize).Should().Be((4, 2), "blank cells leave the stored order units alone");
            var p = await db.Products.Include(x => x.AttributeValues).SingleAsync(x => x.Slug == $"it-{_run}-3");
            p.AttributeValues.Should().HaveCount(4);
            (await db.AttributeValues.Where(x => p.AttributeValues.Select(l => l.AttributeValueId).Contains(x.Id)).Select(x => x.ValueEn).ToListAsync())
                .Should().Contain("Series 9").And.NotContain("Series 0");
        }
    }

    [PostgresFact]
    public async Task One_bad_row_returns_422_and_nothing_is_written()
    {
        await SeedAsync();
        using var admin = await AdminAsync();
        string csv = Header() + Rows(0, 6) + $"it-{_run}-bad,Bad,খারাপ,{BrandSlug},{CatSlug},SKU-{_run}-bad,IT{_run}-bad,0,,,active,,,,,\r\n";

        int auditBefore;
        await using (var pre = Db()) auditBefore = await pre.AuditLogs.CountAsync(l => l.Action == "catalog.import.committed");
        var result = await CommitAsync(admin, csv);
        result.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        var report = result.Root.GetProperty("data");
        report.GetProperty("errors").EnumerateArray().Select(x => x.GetProperty("code").GetString()).Should().Contain("PRICE_NOT_POSITIVE");
        (await CountsAsync()).Should().Be((0, 0, 0, 0, 0));
        await using var db = Db();
        (await db.AuditLogs.CountAsync(l => l.Action == "catalog.import.committed")).Should().Be(auditBefore);
    }

    [PostgresFact]
    public async Task A_domain_rule_broken_late_rolls_the_whole_file_back()
    {
        await SeedAsync();
        using var admin = await AdminAsync();
        (await CommitAsync(admin, Header() + Rows(0, 2, "100"))).Status.Should().Be(HttpStatusCode.OK);
        var before = await CountsAsync();
        // Row for product 0 sets min order quantity 3 with the stored pack size 2: valid in the file, refused by the domain.
        string csv = Header() + Rows(2, 5)
            + $"it-{_run}-0,Relay 0,রিলে 0,{BrandSlug},{CatSlug},SKU-{_run}-0a,IT{_run}-0000a,100,3,,,,,,,\r\n";
        var result = await CommitAsync(admin, csv);
        result.Status.Should().Be(HttpStatusCode.UnprocessableEntity, result.Root.ToString());
        result.Root.GetProperty("data").GetProperty("errors")[0].GetProperty("code").GetString().Should().Be("DOMAIN_RULE");
        (await CountsAsync()).Should().Be(before, "the products of the first batch were rolled back");
    }

    [PostgresFact]
    public async Task Two_commits_of_overlapping_files_at_once_create_no_duplicates()
    {
        await SeedAsync();
        using var admin1 = await AdminAsync();
        string fileA = Header() + Rows(0, 30), fileB = Header() + Rows(15, 45);   // 15..29 in both

        var results = await Task.WhenAll(CommitAsync(admin1, fileA), CommitAsync(admin1, fileB));
        results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, string.Join(" | ", results.Select(r => r.Root.ToString())));

        var counts = await CountsAsync();
        counts.Products.Should().Be(45);
        counts.Variants.Should().Be(Enumerable.Range(0, 45).Sum(n => n % 2 == 0 ? 2 : 1));
        results.Sum(r => r.Root.GetProperty("data").GetProperty("createdProducts").GetInt32()).Should().Be(45, "each product is created by exactly one of them");

        await using var db = Db();
        (await db.ProductVariants.Where(v => v.Sku.StartsWith($"SKU-{_run.ToUpperInvariant()}")).GroupBy(v => v.PartNumberKey).CountAsync(g => g.Count() > 1)).Should().Be(0);
    }

    [PostgresTheory]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task Large_files_commit_in_reasonable_time(int variants)
    {
        await SeedAsync();
        using var admin = await AdminAsync();
        string csv = Header() + Rows(0, variants * 2 / 3 + 1);
        csv = string.Join("\r\n", csv.Split("\r\n").Take(variants + 1)) + "\r\n";

        var clock = Stopwatch.StartNew();
        var first = await CommitAsync(admin, csv);
        long createMs = clock.ElapsedMilliseconds;
        first.Status.Should().Be(HttpStatusCode.OK, first.Root.ToString());
        clock.Restart();
        var again = await CommitAsync(admin, csv);
        long repeatMs = clock.ElapsedMilliseconds;
        again.Root.GetProperty("data").GetProperty("createdVariants").GetInt32().Should().Be(0);
        output.WriteLine($"TIMING {variants} variant rows: first commit {createMs} ms, identical re-commit {repeatMs} ms");
        createMs.Should().BeLessThan(120_000);
    }
}
