using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Api;

/// <summary>
/// Sprint 5–6: a part number finds its product however it is typed, through the real API. Only published products, obsolete
/// ones included; "did you mean" (O read as 0, I or L as 1) only when nothing matches as typed.
/// </summary>
public sealed class PartNumberLookupTests : IDisposable
{
    private readonly DbBackedFactory _factory = new(settings: new Dictionary<string, string> { ["RateLimiting:StorefrontPermitPerMinute"] = "100000" });

    public void Dispose() => _factory.Dispose();

    private ShopVerseDbContext Db() => new(new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    /// <summary>A published product whose default SKU carries <paramref name="partNumber"/>; returns its id and slug.</summary>
    private async Task<(Guid Id, string Slug)> ProductWithAsync(string partNumber)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 3);
        var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == seeded.ProductId);
        p.Variants.Single().SetManufacturerPartNumber(partNumber);
        await db.SaveChangesAsync();
        return (p.Id, p.Slug);
    }

    private async Task<HttpResponseMessage> LookUpAsync(string q)
    {
        using var client = _factory.CreateClient();
        return await client.GetAsync("/api/v1/storefront/pn?q=" + Uri.EscapeDataString(q));
    }

    private async Task<(bool Exact, string[] Slugs)> FoundAsync(string q)
    {
        var response = await LookUpAsync(q);
        response.StatusCode.Should().Be(HttpStatusCode.OK, q);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        return (data.GetProperty("exact").GetBoolean(),
            [.. data.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("slug").GetString()!)]);
    }

    [PostgresFact]
    public async Task APartNumber_IsFound_HoweverItIsTyped()
    {
        var (_, slug) = await ProductWithAsync("6ES7214-1AG40-0XB0");

        foreach (var typed in new[] { "6ES7214-1AG40-0XB0", "6es7214-1ag40-0xb0", "6ES7 214 1AG40 0XB0", "6ES7.214.1AG40.0XB0", "6ES72141AG400XB0", "  6es7/214_1ag40-0xb0 ", "৬ES7২১৪-1AG40-0XB0" })
        {
            var (exact, slugs) = await FoundAsync(typed);
            exact.Should().BeTrue(typed);
            slugs.Should().Equal([slug], typed);
        }
    }

    [PostgresFact]
    public async Task LookAlikeLetters_GiveADidYouMean_OnlyWhenNothingMatchesAsTyped()
    {
        var (_, cpu) = await ProductWithAsync("6ES7214-1AG40-0XB0");

        var (exact, slugs) = await FoundAsync("6ES7214-IAG4O-OXBO");   // I for 1, O for 0
        exact.Should().BeFalse("found only by reading the look-alike letters");
        slugs.Should().Equal([cpu]);

        // When a product's number really has the letter, the exact match wins and nothing fuzzy is added.
        var (_, withLetter) = await ProductWithAsync("LC1-D09");
        var (_, withDigit) = await ProductWithAsync("1C1-D09");
        var asTyped = await FoundAsync("LC1D09");
        asTyped.Exact.Should().BeTrue();
        asTyped.Slugs.Should().Equal([withLetter]);
        (await FoundAsync("1C1D09")).Slugs.Should().Equal([withDigit]);
    }

    [PostgresFact]
    public async Task OnlyPublishedProducts_AreFound_ObsoleteOnesIncluded()
    {
        var (obsolete, obsoleteSlug) = await ProductWithAsync("OBS-0001");
        var (archived, _) = await ProductWithAsync("ARC-0001");
        var (deleted, _) = await ProductWithAsync("DEL-0001");
        var (inactiveSku, _) = await ProductWithAsync("INA-0001");
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET supply_status = 'obsolete' WHERE id = {obsolete}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET status = 'archived' WHERE id = {archived}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET deleted_at = now() WHERE id = {deleted}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET status = 'inactive' WHERE product_id = {inactiveSku}");
        }

        (await FoundAsync("OBS-0001")).Slugs.Should().Equal([obsoleteSlug], "someone replacing a part searches its old number");
        foreach (var hidden in new[] { "ARC-0001", "DEL-0001", "INA-0001" })
            (await LookUpAsync(hidden)).StatusCode.Should().Be(HttpStatusCode.NotFound, hidden);
    }

    [PostgresFact]
    public async Task ThePartNumber_SoldByTwoSellers_ListsBoth()
    {
        var (_, first) = await ProductWithAsync("3RT2015-1BB41");
        var (_, second) = await ProductWithAsync("3RT2015-1BB41");

        (await FoundAsync("3RT20151BB41")).Slugs.Should().BeEquivalentTo([first, second]);
    }

    [PostgresFact]
    public async Task AProduct_WhoseSkusShareTheNumber_IsListedOnce()
    {
        var (id, slug) = await ProductWithAsync("ATV320-SHARED");
        await using (var db = Db())
        {
            var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == id);
            var second = p.AddVariant(Guid.NewGuid(), "SKU-" + Guid.NewGuid().ToString("N")[..8], null,
                new Dictionary<string, string> { ["Model"] = "B" }, ShopVerse.Domain.Common.ValueObjects.Money.FromMinorUnits(160_000, ShopVerse.Domain.Common.Enums.Currency.BDT),
                null, null, 0.5m, false);
            second.SetManufacturerPartNumber("ATV320-SHARED");
            await db.SaveChangesAsync();
        }

        (await FoundAsync("ATV320SHARED")).Slugs.Should().Equal([slug], "two SKUs of one product are one entry");
    }

    [PostgresFact]
    public async Task ManySkusOfOneProduct_DoNotCrowdOutOthers_AndTheDefaultSkuIsShown()
    {
        var (crowded, crowdedSlug) = await ProductWithAsync("CROWD-0001");
        string defaultSku;
        await using (var db = Db())
        {
            var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == crowded);
            defaultSku = p.Variants.Single().Sku;
            for (int i = 0; i < 45; i++)
            {
                // "A-…" sorts before the default "SKU-…": the default must still be the one shown.
                p.AddVariant(Guid.NewGuid(), $"A-{i:D2}-{Guid.NewGuid():N}"[..20], null, new Dictionary<string, string> { ["Model"] = $"V{i}" },
                    ShopVerse.Domain.Common.ValueObjects.Money.FromMinorUnits(160_000, ShopVerse.Domain.Common.Enums.Currency.BDT), null, null, 0.5m, false)
                    .SetManufacturerPartNumber("CROWD-0001");
            }

            await db.SaveChangesAsync();
            // Listed first by title, so its SKUs come first too.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET title_en = 'AAA crowded' WHERE id = {crowded}");
        }

        var (_, other1) = await ProductWithAsync("CROWD-0001");
        var (_, other2) = await ProductWithAsync("CROWD-0001");

        var response = await LookUpAsync("CROWD0001");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var matches = doc.RootElement.GetProperty("data").GetProperty("matches").EnumerateArray().ToList();
        matches.Select(m => m.GetProperty("slug").GetString()).Should().BeEquivalentTo([crowdedSlug, other1, other2]);
        matches.Single(m => m.GetProperty("slug").GetString() == crowdedSlug).GetProperty("sku").GetString().Should().Be(defaultSku);
    }

    [PostgresFact]
    public async Task AtMostTenProducts_AreListed()
    {
        for (int i = 0; i < 12; i++) await ProductWithAsync("MANY-0001");
        (await FoundAsync("MANY0001")).Slugs.Should().HaveCount(10);
    }

    [PostgresFact]
    public async Task NoMatch_Is404_AndTooLittleToSearch_Is400_NeverA500()
    {
        (await LookUpAsync("NO-SUCH-PART-9999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var q in new[] { "", " ", "-", "/./", "A" })
            (await LookUpAsync(q)).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"'{q}'");
        using var client = _factory.CreateClient();
        (await client.GetAsync("/api/v1/storefront/pn")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LookUpAsync(new string('x', 5000) + "1")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a long query is cut, not an error");
        (await LookUpAsync("'; DROP TABLE products; --")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- staff set the part number -----

    private const string AdminEmail = "admin@mechware.com.bd";
    private static readonly TotpService Totp = new();

    private async Task<HttpClient> AdminAsync()
    {
        HttpClient client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = AdminEmail,
            password = "MechAdmin#2026!",
            totpCode = Totp.ComputeTotpCode("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", DateTimeOffset.UtcNow)
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    [PostgresFact]
    public async Task Staff_SetThePartNumber_AndItIsFound_AnonymousAndBuyersCannot()
    {
        Guid product, variant;
        await using (var db = Db())
        {
            var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
            (product, variant) = (seeded.ProductId, seeded.VariantId);
        }

        string url = $"/api/v1/admin/products/{product}/variants/{variant}/part-number";
        using (var anonymous = _factory.CreateClient())
            (await anonymous.PutAsJsonAsync(url, new { manufacturerPartNumber = "X-1" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var admin = await AdminAsync();
        var set = await admin.PutAsJsonAsync(url, new { manufacturerPartNumber = " ATV320U07N4C " });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        using (var doc = JsonDocument.Parse(await set.Content.ReadAsStringAsync()))
            doc.RootElement.GetProperty("data").GetProperty("variants")[0].GetProperty("manufacturerPartNumber").GetString().Should().Be("ATV320U07N4C");
        (await FoundAsync("atv320 u07n4c")).Slugs.Should().ContainSingle();

        (await admin.PutAsJsonAsync(url, new { manufacturerPartNumber = new string('A', 65) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync(url, new { manufacturerPartNumber = "-" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/admin/products/{product}/variants/{Guid.NewGuid()}/part-number", new { manufacturerPartNumber = "Y-2" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.PutAsJsonAsync(url, new { manufacturerPartNumber = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LookUpAsync("ATV320U07N4C")).StatusCode.Should().Be(HttpStatusCode.NotFound, "cleared");
    }
}
