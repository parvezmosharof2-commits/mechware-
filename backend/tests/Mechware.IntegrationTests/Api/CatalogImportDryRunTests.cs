using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Catalog;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Api;

/// <summary>C10.1 exit test: a 1,000-row file with a known set of bad rows; exactly those are reported and nothing is written.</summary>
public sealed class CatalogImportDryRunTests : IDisposable
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

    private static MultipartFormDataContent Upload(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "import.csv");
        return form;
    }

    [PostgresFact]
    public async Task Only_staff_with_catalog_manage_may_call_it()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.PostAsync("/api/v1/admin/catalog/import/dry-run", Upload([]))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var buyer = await SignedInAsync("buyer@mechware.com.bd", "BuyerPass#2026!", null);
        (await buyer.PostAsync("/api/v1/admin/catalog/import/dry-run", Upload([]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [PostgresFact]
    public async Task A_thousand_row_file_reports_exactly_the_bad_rows_and_writes_nothing()
    {
        string run = Guid.NewGuid().ToString("N")[..6];
        string brand = $"it-brand-{run}", category = $"it-cat-{run}", attr = $"it{run}_current";
        await using (var seed = Db())
        {
            seed.Brands.Add(new Brand(Guid.NewGuid(), $"IT Brand {run}", brand, null, false));
            seed.Categories.Add(Category.CreateRoot(Guid.NewGuid(), "IT category", "আইটি বিভাগ", category));
            seed.Attributes.Add(new AttributeDefinition(Guid.NewGuid(), "Rated current", "রেটেড কারেন্ট", attr, AttributeDataType.Number, "A", true));
            await seed.SaveChangesAsync();
        }

        var csv = new StringBuilder("product_slug,product_title_en,product_title_bn,brand,category,sku,manufacturer_part_number,price_bdt,min_order_qty,pack_size,supply_status,datasheet_url,image_url,attr:" + attr + "\r\n");
        var expected = new Dictionary<int, string>();   // csv row number (header = 1) -> error code
        for (int i = 0; i < 1000; i++)
        {
            string slug = $"it-{run}-{i}", pn = $"IT{run}-{i:D4}", price = "1250.50", b = brand, title = "ইন্ডাস্ট্রিয়াল রিলে", current = "10 A", moq = "", pack = "", url = "";
            string? code = null;
            switch (i)
            {
                case 10: price = "0"; code = "PRICE_NOT_POSITIVE"; break;
                case 20: b = "nobody-" + run; code = "UNKNOWN_BRAND"; break;
                case 30: pn = $"IT{run}-0029"; code = "DUPLICATE_PART_NUMBER"; break;
                case 40: title = "=HYPERLINK(\"http://evil\")"; code = "FORMULA_INJECTION"; break;
                case 50: current = "230 V"; code = "INVALID_UNIT_VALUE"; break;
                case 60: moq = "6"; pack = "4"; code = "MOQ_NOT_PACK_MULTIPLE"; break;
                case 70: url = "http://example.com/a.pdf"; code = "INVALID_URL"; break;
                case 80: slug = "Bad Slug " + i; code = "INVALID_SLUG"; break;
                case 90: pn = $"it{run} 0089"; code = "NORMALISED_COLLISION"; break;
            }

            if (code is not null) expected[i + 2] = code;
            string quoted = "\"" + title.Replace("\"", "\"\"") + "\"";
            csv.Append($"{slug},{quoted},\"ইন্ডাস্ট্রিয়াল রিলে\",{b},{category},SKU-{run}-{i},{pn},{price},{moq},{pack},active,{url},,{current}\r\n");
        }

        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(false).GetBytes(csv.ToString())];

        await using var before = Db();
        long products = await before.Products.LongCountAsync(), variants = await before.ProductVariants.LongCountAsync(),
            brands = await before.Brands.LongCountAsync(), categories = await before.Categories.LongCountAsync();

        using var admin = await SignedInAsync("admin@mechware.com.bd", "MechAdmin#2026!", "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP");
        var response = await admin.PostAsync("/api/v1/admin/catalog/import/dry-run", Upload(bytes));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");

        data.GetProperty("rows").GetInt32().Should().Be(1000);
        data.GetProperty("invalidRows").GetInt32().Should().Be(expected.Count);
        data.GetProperty("validRows").GetInt32().Should().Be(1000 - expected.Count);
        data.GetProperty("errorCount").GetInt32().Should().Be(expected.Count);
        data.GetProperty("errorsTruncated").GetBoolean().Should().BeFalse();
        data.GetProperty("existingProducts").GetInt32().Should().Be(0);
        data.GetProperty("newVariants").GetInt32().Should().Be(1000 - expected.Count);
        var reported = data.GetProperty("errors").EnumerateArray().ToDictionary(e => e.GetProperty("row").GetInt32(), e => e.GetProperty("code").GetString()!);
        reported.Should().BeEquivalentTo(expected);

        await using var after = Db();
        (await after.Products.LongCountAsync()).Should().Be(products);
        (await after.ProductVariants.LongCountAsync()).Should().Be(variants);
        (await after.Brands.LongCountAsync()).Should().Be(brands);
        (await after.Categories.LongCountAsync()).Should().Be(categories);
    }
}
