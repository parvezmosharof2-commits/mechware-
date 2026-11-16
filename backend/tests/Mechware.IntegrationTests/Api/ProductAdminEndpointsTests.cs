using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Catalog;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Infrastructure.Caching;
using Mechware.Infrastructure.Catalog;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;

namespace Mechware.IntegrationTests.Api;

/// <summary>C1.7: staff product list, edit, variant setters and lifecycle against real PostgreSQL.</summary>
public sealed class ProductAdminEndpointsTests : IDisposable
{
    private const string AdminEmail = "admin@mechware.com.bd";
    private const string AdminPassword = "MechAdmin#2026!";
    private const string AdminSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private static readonly TotpService Totp = new();
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private static async Task<HttpClient> SignedInAsync(DbBackedFactory factory, string email, string password, string? secret)
    {
        HttpClient client = factory.CreateClient();
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

    private Task<HttpClient> AdminAsync() => SignedInAsync(_factory, AdminEmail, AdminPassword, AdminSecret);

    private static string Url(Guid product, string tail = "") => $"/api/v1/admin/products/{product}" + (tail.Length == 0 ? "" : "/" + tail);

    private static async Task<JsonElement> DataOf(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> Refused(HttpResponseMessage response, HttpStatusCode status, string contains)
    {
        string raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(contains);
        return raw;
    }

    /// <summary>A published product (price 1,500.00) with one stocked SKU; its seller and category are fresh.</summary>
    private async Task<(Guid Id, Guid VariantId, string Slug)> PublishedAsync()
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
        return (seeded.ProductId, seeded.VariantId, await db.Products.Where(p => p.Id == seeded.ProductId).Select(p => p.Slug).SingleAsync());
    }

    /// <summary>A draft with one active SKU; <paramref name="withMedia"/> false leaves it without an image.</summary>
    private async Task<(Guid Id, Guid VariantId, string Slug)> DraftAsync(bool withMedia = true, bool withVariant = true)
    {
        await using var db = Db();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
        var template = await db.Products.AsNoTracking().SingleAsync(p => p.Id == seeded.ProductId);
        var slug = $"draft-{Guid.NewGuid():N}"[..20];
        var draft = new Product(Guid.NewGuid(), template.SellerId, template.CategoryId, null, "Draft " + slug, "খসড়া", slug, "s", "s", "<p>d</p>");
        if (withVariant)
            draft.AddVariant(Guid.NewGuid(), "DR-" + slug, null, new Dictionary<string, string>(), new Money(250_000, Currency.BDT), null, null, 0.8m, true);
        if (withMedia) draft.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "front view", 1200, 1200);
        db.Products.Add(draft);
        await db.SaveChangesAsync();
        return (draft.Id, draft.Variants.FirstOrDefault()?.Id ?? Guid.Empty, slug);
    }

    private static long Version(JsonElement data) => data.GetProperty("rowVersion").GetInt64();

    // ----- who may call -----

    [PostgresFact]
    public async Task Anonymous_Gets401_AndBuyerAndSeller_Get403_OnEveryEndpoint()
    {
        var id = Guid.NewGuid();
        (HttpMethod Method, string Path, object? Body)[] endpoints =
        [
            (HttpMethod.Get, "/api/v1/admin/products", null),
            (HttpMethod.Get, Url(id), null),
            (HttpMethod.Put, Url(id), new { titleEn = "x", titleBn = "y", categoryId = Guid.NewGuid(), expectedVersion = 1 }),
            (HttpMethod.Put, Url(id, $"variants/{Guid.NewGuid()}"), new { weightKg = 1, expectedVersion = 1 }),
            (HttpMethod.Post, Url(id, "publish"), new { expectedVersion = 1 }),
            (HttpMethod.Post, Url(id, "archive"), new { expectedVersion = 1 }),
            (HttpMethod.Post, Url(id, "restore"), new { expectedVersion = 1 })
        ];
        HttpRequestMessage Build((HttpMethod Method, string Path, object? Body) e) =>
            new(e.Method, e.Path) { Content = e.Body is null ? null : JsonContent.Create(e.Body) };

        using var anonymous = _factory.CreateClient();
        foreach (var e in endpoints) (await anonymous.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, e.Path);

        using var buyer = await SignedInAsync(_factory, "buyer@mechware.com.bd", "BuyerPass#2026!", null);
        foreach (var e in endpoints) (await buyer.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Forbidden, e.Path);

        using var seller = await SignedInAsync(_factory, "seller.alpha@mechware.com.bd", "SellerAlpha#2026!", null);
        foreach (var e in endpoints) (await seller.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Forbidden, e.Path);
    }

    // ----- list -----

    [PostgresFact]
    public async Task List_FiltersPagesAndCapsTheSize_IncludesDrafts_AndEscapesWildcards()
    {
        var published = await PublishedAsync();
        var draft = await DraftAsync();
        var (deleted, _, _) = await DraftAsync();
        await using (var db = Db())
        {
            var p = await db.Products.SingleAsync(x => x.Id == deleted);
            p.SoftDelete(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        using var admin = await AdminAsync();
        var all = await DataOf(await admin.GetAsync("/api/v1/admin/products?pageSize=500"));
        all.GetProperty("pageSize").GetInt32().Should().Be(50, "the page size is capped");
        all.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(50);

        // A draft is listed (with its aggregate), a soft-deleted product is not.
        var byDraft = await DataOf(await admin.GetAsync($"/api/v1/admin/products?q={draft.Slug}"));
        byDraft.GetProperty("totalCount").GetInt32().Should().Be(1);
        var row = byDraft.GetProperty("items")[0];
        row.GetProperty("id").GetGuid().Should().Be(draft.Id);
        row.GetProperty("status").GetString().Should().Be("draft");
        row.GetProperty("supplyStatus").GetString().Should().Be("active");
        row.GetProperty("variantCount").GetInt32().Should().Be(1);
        (row.GetProperty("minPriceMinor").GetInt64(), row.GetProperty("maxPriceMinor").GetInt64()).Should().Be((25_000_000, 25_000_000));
        row.GetProperty("sellerStoreName").GetString().Should().StartWith("Test Seller");
        row.GetProperty("categoryPath").GetString().Should().StartWith("test-");
        row.GetProperty("rowVersion").GetInt64().Should().BeGreaterThan(0);
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?q={Uri.EscapeDataString("Draft draft-")}"))).GetProperty("totalCount").GetInt32()
            .Should().BeGreaterThanOrEqualTo(1);

        // Status filter, SKU search (case-insensitive), part-number search.
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?status=active&q={published.Slug}"))).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?status=draft&q={published.Slug}"))).GetProperty("totalCount").GetInt32().Should().Be(0);
        string sku = (await Db().ProductVariants.AsNoTracking().SingleAsync(v => v.Id == published.VariantId)).Sku;
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?q={sku.ToLowerInvariant()}"))).GetProperty("items")[0].GetProperty("id").GetGuid()
            .Should().Be(published.Id);
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?q={deleted}"))).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?q={(await Db().Products.AsNoTracking().Where(p => p.Id == deleted).Select(p => p.Slug).SingleAsync())}")))
            .GetProperty("totalCount").GetInt32().Should().Be(0, "a deleted product is not listed");

        // LIKE wildcards typed by the caller are literal: neither '%' nor '_' matches a slug.
        (await DataOf(await admin.GetAsync("/api/v1/admin/products?q=%25"))).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await DataOf(await admin.GetAsync("/api/v1/admin/products?q=_"))).GetProperty("totalCount").GetInt32().Should().Be(0);

        // Brand / category / seller / supply filters and paging.
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?categoryId={published.Id}"))).GetProperty("totalCount").GetInt32().Should().Be(0);
        var detail = await DataOf(await admin.GetAsync(Url(published.Id)));
        var byCategory = await DataOf(await admin.GetAsync($"/api/v1/admin/products?categoryId={detail.GetProperty("categoryId").GetGuid()}&pageSize=1"));
        byCategory.GetProperty("totalCount").GetInt32().Should().Be(1);
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?sellerId={detail.GetProperty("sellerId").GetGuid()}&supplyStatus=obsolete"))).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await DataOf(await admin.GetAsync($"/api/v1/admin/products?q={published.Slug}&page=2&pageSize=1"))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await admin.GetAsync("/api/v1/admin/products?status=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task List_UsesAFixedNumberOfQueries_WhateverThePageHolds()
    {
        for (int i = 0; i < 3; i++) await DraftAsync();
        var recorder = new SqlRecorder();
        await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).AddInterceptors(recorder).Options);
        var service = new ProductAdminService(db, null!, TimeProvider.System);

        await service.ListAsync(null, null, null, null, null, null, 1, 1);   // warm
        recorder.Commands.Clear();
        var one = await service.ListAsync(null, null, null, null, null, null, 1, 1);
        int small = recorder.Commands.Count;
        recorder.Commands.Clear();
        var fifty = await service.ListAsync("draft", null, null, null, null, null, 1, 50);
        int big = recorder.Commands.Count;

        one.Items.Should().HaveCount(1);
        fifty.Items.Count.Should().BeGreaterThan(2);
        big.Should().Be(small).And.BeLessThanOrEqualTo(2, string.Join("\n---\n", recorder.Commands));
        Console.WriteLine($"PRODUCT-LIST-QUERIES small={small} big={big} rows={fifty.Items.Count}");
    }

    // ----- edit -----

    [PostgresFact]
    public async Task Update_RoundTrips_LeavesTheSlugAlone_AuditsAndRequestsReindex()
    {
        var p = await PublishedAsync();
        using var admin = await AdminAsync();
        var before = await DataOf(await admin.GetAsync(Url(p.Id)));
        before.GetProperty("allowedTransitions").EnumerateArray().Select(x => x.GetString()).Should().Equal("archive");
        var variant = before.GetProperty("variants")[0];
        (variant.GetProperty("weightKg").GetDecimal(), variant.GetProperty("priceMinor").GetInt64()).Should().Be((0.5m, 150_000));

        int events;
        await using (var db = Db()) events = await db.OutboxMessages.CountAsync(o => o.EventType == nameof(ProductUpdatedEvent) && o.AggregateId == p.Id);

        var newCategory = await NewCategoryAsync();
        var after = await DataOf(await admin.PutAsJsonAsync(Url(p.Id), new
        {
            titleEn = "  Renamed servo drive ",
            titleBn = "নতুন নাম",
            shortDescriptionEn = "short",
            shortDescriptionBn = "ছোট",
            descriptionHtml = "<p>Safe <strong>text</strong></p><ul><li>one</li></ul>",
            categoryId = newCategory,
            brandId = (Guid?)null,
            lengthCm = 12.5,
            widthCm = 8,
            heightCm = 3,
            expectedVersion = Version(before)
        }));

        after.GetProperty("titleEn").GetString().Should().Be("Renamed servo drive");
        after.GetProperty("slug").GetString().Should().Be(p.Slug, "changing the title never changes the slug");
        after.GetProperty("categoryId").GetGuid().Should().Be(newCategory);
        after.GetProperty("lengthCm").GetDecimal().Should().Be(12.5m);
        Version(after).Should().NotBe(Version(before));
        (await DataOf(await admin.GetAsync(Url(p.Id)))).GetProperty("descriptionHtml").GetString().Should().Contain("<strong>text</strong>");

        await using var verify = Db();
        (await verify.OutboxMessages.CountAsync(o => o.EventType == nameof(ProductUpdatedEvent) && o.AggregateId == p.Id)).Should().BeGreaterThan(events, "a reindex was requested");
        (await verify.AuditLogs.CountAsync(a => a.Action == "catalog.product.edited" && a.EntityId == p.Id.ToString())).Should().Be(1);
        (await verify.AuditLogs.SingleAsync(a => a.Action == "catalog.product.edited" && a.EntityId == p.Id.ToString())).ActorId.Should().NotBeNull();
    }

    [PostgresFact]
    public async Task Update_WithAStaleVersion_Gets409_AndSavesNothing_AndAMissingVersionGets400()
    {
        var p = await PublishedAsync();
        using var admin = await AdminAsync();
        var data = await DataOf(await admin.GetAsync(Url(p.Id)));
        object Body(long? version, string title) => new
        {
            titleEn = title,
            titleBn = "বাংলা",
            descriptionHtml = "<p>x</p>",
            categoryId = data.GetProperty("categoryId").GetGuid(),
            expectedVersion = version
        };

        await DataOf(await admin.PutAsJsonAsync(Url(p.Id), Body(Version(data), "First edit")));
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body(Version(data), "Second, stale")), HttpStatusCode.Conflict, "PRODUCT_STALE");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body(null, "No version")), HttpStatusCode.BadRequest, "expectedVersion");

        await using var db = Db();
        (await db.Products.AsNoTracking().SingleAsync(x => x.Id == p.Id)).TitleEn.Should().Be("First edit");
    }

    [PostgresFact]
    public async Task Update_RefusesScriptsUnknownTagsBadReferencesAndOversizeText_With400AndFieldDetails()
    {
        var p = await PublishedAsync();
        using var admin = await AdminAsync();
        var data = await DataOf(await admin.GetAsync(Url(p.Id)));
        var category = data.GetProperty("categoryId").GetGuid();
        object Body(string html, Guid? cat = null, Guid? brand = null, string titleEn = "T") => new
        {
            titleEn,
            titleBn = "বাংলা",
            descriptionHtml = html,
            categoryId = cat ?? category,
            brandId = brand,
            expectedVersion = Version(data)
        };

        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<p>hi</p><script>alert(1)</script>")), HttpStatusCode.BadRequest, "descriptionHtml");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<img src=x onerror=alert(1)>")), HttpStatusCode.BadRequest, "descriptionHtml");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<p onclick=\"x()\">a</p>")), HttpStatusCode.BadRequest, "not allowed");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<a href=\"javascript:alert(1)\">x</a>")), HttpStatusCode.BadRequest, "descriptionHtml");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<iframe src=\"https://x\"></iframe>")), HttpStatusCode.BadRequest, "iframe");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body(new string('a', 20_001))), HttpStatusCode.BadRequest, "descriptionHtml");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<p>ok</p>", titleEn: new string('t', 256))), HttpStatusCode.BadRequest, "titleEn");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<p>ok</p>", titleEn: "  ")), HttpStatusCode.BadRequest, "titleEn");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<p>ok</p>", cat: Guid.NewGuid())), HttpStatusCode.BadRequest, "categoryId");
        await Refused(await admin.PutAsJsonAsync(Url(p.Id), Body("<p>ok</p>", brand: Guid.NewGuid())), HttpStatusCode.BadRequest, "brandId");
        (await admin.PutAsJsonAsync(Url(Guid.NewGuid()), Body("<p>ok</p>"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = Db();
        (await db.Products.AsNoTracking().SingleAsync(x => x.Id == p.Id)).TitleEn.Should().Be("Test Product", "nothing was saved");
    }

    // ----- variants -----

    [PostgresFact]
    public async Task VariantUpdate_ChangesPriceWeightAndStatus_AuditsThePriceWithOldAndNew_AndNeedsTheVersion()
    {
        var p = await PublishedAsync();
        using var admin = await AdminAsync();
        var data = await DataOf(await admin.GetAsync(Url(p.Id)));
        var path = Url(p.Id, $"variants/{p.VariantId}");

        var updated = await DataOf(await admin.PutAsJsonAsync(path, new { priceMinor = 175_000, weightKg = 2.345, expectedVersion = Version(data) }));
        var v = updated.GetProperty("variants")[0];
        (v.GetProperty("priceMinor").GetInt64(), v.GetProperty("weightKg").GetDecimal()).Should().Be((175_000, 2.345m));

        await using (var db = Db())
        {
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "catalog.variant.price_changed" && a.EntityId == p.VariantId.ToString());
            audit.OldValuesJson.Should().Contain("150000");
            audit.NewValuesJson.Should().Contain("175000");
            audit.ActorId.Should().NotBeNull();
            (await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == p.VariantId)).WeightKg.Should().Be(2.345m);
        }

        // Weight and status alone: no price audit row, but the change is recorded.
        var second = await DataOf(await admin.PutAsJsonAsync(path, new { status = "inactive", expectedVersion = Version(updated) }));
        second.GetProperty("variants")[0].GetProperty("status").GetString().Should().Be("inactive");
        await using (var db = Db())
            (await db.AuditLogs.CountAsync(a => a.Action == "catalog.variant.price_changed" && a.EntityId == p.VariantId.ToString())).Should().Be(1);

        // Stale and missing versions, bad values.
        await Refused(await admin.PutAsJsonAsync(path, new { priceMinor = 190_000, expectedVersion = Version(updated) }), HttpStatusCode.Conflict, "PRODUCT_STALE");
        long current = Version(second);
        await Refused(await admin.PutAsJsonAsync(path, new { priceMinor = 190_000 }), HttpStatusCode.BadRequest, "expectedVersion");
        foreach (var weight in new[] { "0", "-1", "1000.5", "1.2345" })
            await Refused(await admin.PutAsync(path, new StringContent($"{{\"weightKg\":{weight},\"expectedVersion\":{current}}}", System.Text.Encoding.UTF8, "application/json")),
                HttpStatusCode.BadRequest, "weightKg");
        await Refused(await admin.PutAsJsonAsync(path, new { priceMinor = 0, expectedVersion = current }), HttpStatusCode.BadRequest, "priceMinor");
        await Refused(await admin.PutAsJsonAsync(path, new { status = "gone", expectedVersion = current }), HttpStatusCode.BadRequest, "status");
        await Refused(await admin.PutAsJsonAsync(path, new { expectedVersion = current }), HttpStatusCode.BadRequest, "at least one");
        (await admin.PutAsJsonAsync(Url(p.Id, $"variants/{Guid.NewGuid()}"), new { weightKg = 1, expectedVersion = current })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- lifecycle -----

    [PostgresFact]
    public async Task Lifecycle_PublishArchiveRestore_RequestsReindexEachTime_AndRefusesWithReasonCodes()
    {
        var draft = await DraftAsync();
        using var admin = await AdminAsync();
        var data = await DataOf(await admin.GetAsync(Url(draft.Id)));
        data.GetProperty("allowedTransitions").EnumerateArray().Select(x => x.GetString()).Should().Equal("publish", "archive");

        async Task<int> Events()
        {
            await using var db = Db();
            return await db.OutboxMessages.CountAsync(o => o.EventType == nameof(ProductUpdatedEvent) && o.AggregateId == draft.Id);
        }

        int e0 = await Events();
        var published = await DataOf(await admin.PostAsJsonAsync(Url(draft.Id, "publish"), new { expectedVersion = Version(data) }));
        published.GetProperty("status").GetString().Should().Be("active");
        int e1 = await Events();
        e1.Should().BeGreaterThan(e0, "publishing requests a reindex");
        await using (var ob = Db())
            (await ob.OutboxMessages.AsNoTracking().Where(o => o.EventType == nameof(ProductUpdatedEvent) && o.AggregateId == draft.Id).Select(o => o.Payload).ToListAsync())
                .Should().Contain(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"""status""\s*:\s*(3|""Active""|""active"")"),
                    "the reindex carries the state after the publish, not the one under review");

        await Refused(await admin.PostAsJsonAsync(Url(draft.Id, "publish"), new { expectedVersion = Version(published) }), HttpStatusCode.Conflict, "INVALID_PRODUCT_TRANSITION");
        await Refused(await admin.PostAsJsonAsync(Url(draft.Id, "archive"), new { expectedVersion = Version(data) }), HttpStatusCode.Conflict, "PRODUCT_STALE");

        var archived = await DataOf(await admin.PostAsJsonAsync(Url(draft.Id, "archive"), new { expectedVersion = Version(published) }));
        archived.GetProperty("status").GetString().Should().Be("archived");
        (await Events()).Should().BeGreaterThan(e1);
        await Refused(await admin.PostAsJsonAsync(Url(draft.Id, "archive"), new { expectedVersion = Version(archived) }), HttpStatusCode.Conflict, "INVALID_PRODUCT_TRANSITION");

        var restored = await DataOf(await admin.PostAsJsonAsync(Url(draft.Id, "restore"), new { expectedVersion = Version(archived) }));
        restored.GetProperty("status").GetString().Should().Be("draft");

        await using var db2 = Db();
        (await db2.AuditLogs.AsNoTracking().Where(a => a.EntityId == draft.Id.ToString() && a.Action.StartsWith("catalog.product.") && a.Action != "catalog.product.edited")
            .Select(a => a.Action).ToListAsync()).Should().BeEquivalentTo("catalog.product.publish", "catalog.product.archive", "catalog.product.restore");
        await Refused(await admin.PostAsJsonAsync(Url(draft.Id, "restore"), new { expectedVersion = Version(restored) }), HttpStatusCode.Conflict, "INVALID_PRODUCT_TRANSITION");
        await Refused(await admin.PostAsJsonAsync(Url(draft.Id, "publish"), new { }), HttpStatusCode.BadRequest, "expectedVersion");
    }

    [PostgresFact]
    public async Task Publish_IsRefusedWithAReasonCode_WithoutAnImageAVariantOrAnActiveSku()
    {
        using var admin = await AdminAsync();

        var noMedia = await DraftAsync(withMedia: false);
        var d1 = await DataOf(await admin.GetAsync(Url(noMedia.Id)));
        await Refused(await admin.PostAsJsonAsync(Url(noMedia.Id, "publish"), new { expectedVersion = Version(d1) }), HttpStatusCode.Conflict, "NO_MEDIA");

        var noVariant = await DraftAsync(withVariant: false);
        var d2 = await DataOf(await admin.GetAsync(Url(noVariant.Id)));
        await Refused(await admin.PostAsJsonAsync(Url(noVariant.Id, "publish"), new { expectedVersion = Version(d2) }), HttpStatusCode.Conflict, "NO_SELLABLE_VARIANT");

        var inactive = await DraftAsync();
        var d3 = await DataOf(await admin.GetAsync(Url(inactive.Id)));
        var d3b = await DataOf(await admin.PutAsJsonAsync(Url(inactive.Id, $"variants/{inactive.VariantId}"), new { status = "inactive", expectedVersion = Version(d3) }));
        await Refused(await admin.PostAsJsonAsync(Url(inactive.Id, "publish"), new { expectedVersion = Version(d3b) }), HttpStatusCode.Conflict, "NO_SELLABLE_VARIANT");

        await using var db = Db();
        (await db.Products.AsNoTracking().Where(p => p.Id == noMedia.Id || p.Id == noVariant.Id || p.Id == inactive.Id).Select(p => p.Status).ToListAsync())
            .Should().OnlyContain(s => s == ProductStatus.Draft, "a refused publish leaves the draft as it was");
    }

    [PostgresAndRedisFact]
    public async Task TheStorefront_ShowsAProductAfterPublish_HidesItAfterArchive_AndShowsTheNewPrice()
    {
        await using var redisFactory = new DbBackedFactory(settings: new Dictionary<string, string> { ["ConnectionStrings:Redis"] = TestInfra.RedisConnection });
        using var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        await using var seedDb = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(redisFactory.ConnectionString).Options);
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 5);
        var product = await seedDb.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.ProductId);

        async Task Relay()
        {
            await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(redisFactory.ConnectionString).Options);
            var relay = new OutboxRelay(db, [new StorefrontCacheInvalidationPublisher(new RedisStorefrontCacheInvalidator(redis), db)], NullLogger<OutboxRelay>.Instance);
            while (await relay.ProcessBatchAsync(500) > 0) { }
        }

        using var admin = await SignedInAsync(redisFactory, AdminEmail, AdminPassword, AdminSecret);
        using var anonymous = redisFactory.CreateClient();
        string page = $"/api/v1/storefront/products/{product.Slug}";
        (await anonymous.GetAsync(page)).StatusCode.Should().Be(HttpStatusCode.OK);   // cached now

        var data = await DataOf(await admin.GetAsync(Url(product.Id)));
        var priced = await DataOf(await admin.PutAsJsonAsync(Url(product.Id, $"variants/{seeded.VariantId}"), new { priceMinor = 99_900, expectedVersion = Version(data) }));
        await Relay();
        using (var doc = JsonDocument.Parse(await anonymous.GetStringAsync(page)))
            doc.RootElement.GetProperty("data").GetProperty("variants")[0].GetProperty("pricing").GetProperty("price").GetProperty("amountMinor").GetInt64()
                .Should().Be(99_900, "the cached page was evicted by the price change");

        var archived = await DataOf(await admin.PostAsJsonAsync(Url(product.Id, "archive"), new { expectedVersion = Version(priced) }));
        await Relay();
        (await anonymous.GetAsync(page)).StatusCode.Should().Be(HttpStatusCode.NotFound, "an archived product is not shown");

        await DataOf(await admin.PostAsJsonAsync(Url(product.Id, "restore"), new { expectedVersion = Version(archived) }));
        var restored = await DataOf(await admin.GetAsync(Url(product.Id)));
        await DataOf(await admin.PostAsJsonAsync(Url(product.Id, "publish"), new { expectedVersion = Version(restored) }));
        await Relay();
        (await anonymous.GetAsync(page)).StatusCode.Should().Be(HttpStatusCode.OK, "published again");
    }

    private async Task<Guid> NewCategoryAsync()
    {
        await using var db = Db();
        var category = Category.CreateRoot(Guid.NewGuid(), "Second", "দ্বিতীয়", $"second-{Guid.NewGuid():N}"[..20]);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }
}
