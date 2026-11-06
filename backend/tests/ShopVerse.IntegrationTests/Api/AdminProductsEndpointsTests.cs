using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Api;

/// <summary>
/// Staff edit a product's product-information data (sprint 3–4): supply status and successor, alternatives, order units.
/// Closed to anonymous callers and buyers; every input is validated; the change is audited and read back.
/// </summary>
public sealed class AdminProductsEndpointsTests : IDisposable
{
    private const string AdminEmail = "admin@mechware.com.bd";
    private const string AdminPassword = "MechAdmin#2026!";
    private const string AdminSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private static readonly TotpService Totp = new();
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private ShopVerseDbContext Db() => new(new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(_factory.ConnectionString).Options);

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

    private Task<HttpClient> AdminAsync() => SignedInAsync(AdminEmail, AdminPassword, AdminSecret);

    /// <summary>Published products of one seller (a successor must belong to the same seller).</summary>
    private async Task<Product[]> SameSellerProductsAsync(int count)
    {
        await using var db = Db();
        var first = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
        var seed = await db.Products.AsNoTracking().SingleAsync(p => p.Id == first.ProductId);
        var ids = new List<Guid> { seed.Id };
        for (int i = 1; i < count; i++)
        {
            var slug = $"pim-api-{Guid.NewGuid():N}"[..24];
            var p = new Product(Guid.NewGuid(), seed.SellerId, seed.CategoryId, null, "PIM API " + slug, "পিআইএম", slug, "s", "s", "<p>d</p>");
            p.AddVariant(Guid.NewGuid(), "SKU-" + slug, null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);
            p.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "front view", 1200, 1200);
            p.SubmitForReview();
            p.Approve();
            db.Products.Add(p);
            ids.Add(p.Id);
        }

        await db.SaveChangesAsync();
        return [.. await db.Products.AsNoTracking().Include(p => p.Variants).Where(p => ids.Contains(p.Id)).ToListAsync()
            .ContinueWith(t => t.Result.OrderBy(p => ids.IndexOf(p.Id)))];
    }

    private static string Url(Guid product, string tail) => $"/api/v1/admin/products/{product}/{tail}";

    private static async Task<JsonElement> DataOf(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> FieldOfError(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsStringAsync();
    }

    // ----- who may call -----

    [PostgresFact]
    public async Task Anonymous_Gets401_AndABuyer_Gets403_OnEveryEndpoint()
    {
        var id = Guid.NewGuid();
        (HttpMethod Method, string Path, object? Body)[] endpoints =
        [
            (HttpMethod.Get, Url(id, "pim"), null),
            (HttpMethod.Put, Url(id, "supply"), new { supplyStatus = "nrnd" }),
            (HttpMethod.Put, Url(id, "alternatives"), new { alternativeProductIds = Array.Empty<Guid>() }),
            (HttpMethod.Put, Url(id, $"variants/{Guid.NewGuid()}/order-units"), new { minOrderQty = 1, packSize = 1 })
        ];
        HttpRequestMessage Build((HttpMethod Method, string Path, object? Body) e) =>
            new(e.Method, e.Path) { Content = e.Body is null ? null : JsonContent.Create(e.Body) };

        using var anonymous = _factory.CreateClient();
        foreach (var e in endpoints)
            (await anonymous.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, e.Path);

        using var buyer = await SignedInAsync("buyer@mechware.com.bd", "BuyerPass#2026!", null);
        foreach (var e in endpoints)
            (await buyer.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Forbidden, e.Path);
    }

    // ----- reading -----

    [PostgresFact]
    public async Task Get_ShowsTheProductsPimState_AndAnUnknownProduct_Gets404()
    {
        var p = (await SameSellerProductsAsync(1))[0];
        using var admin = await AdminAsync();

        var data = await DataOf(await admin.GetAsync(Url(p.Id, "pim")));

        data.GetProperty("id").GetGuid().Should().Be(p.Id);
        data.GetProperty("supplyStatus").GetString().Should().Be("active");
        data.GetProperty("successor").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("alternatives").GetArrayLength().Should().Be(0);
        var variant = data.GetProperty("variants")[0];
        (variant.GetProperty("minOrderQty").GetInt32(), variant.GetProperty("packSize").GetInt32()).Should().Be((1, 1));

        (await admin.GetAsync(Url(Guid.NewGuid(), "pim"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task ADeletedProduct_IsGone_ForEveryEndpoint_AndCannotBeLinkedTo()
    {
        var (live, deleted) = (await SameSellerProductsAsync(2)) is [var a, var b] ? (a, b) : throw new InvalidOperationException();
        await using (var db = Db())
        {
            var p = await db.Products.SingleAsync(x => x.Id == deleted.Id);
            p.SoftDelete(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        using var admin = await AdminAsync();
        (await admin.GetAsync(Url(deleted.Id, "pim"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync(Url(deleted.Id, "supply"), new { supplyStatus = "nrnd" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await FieldOfError(await admin.PutAsJsonAsync(Url(live.Id, "supply"), new { supplyStatus = "nrnd", successorProductId = deleted.Id })))
            .Should().Contain("does not exist");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(live.Id, "alternatives"), new { alternativeProductIds = new[] { deleted.Id } })))
            .Should().Contain("do not exist");
    }

    // ----- supply status and successor -----

    [PostgresFact]
    public async Task SetSupply_StoresTheStatusSuccessorAndNote_AndIsReadBack()
    {
        var (old, next) = (await SameSellerProductsAsync(2)) is [var a, var b] ? (a, b) : throw new InvalidOperationException();
        using var admin = await AdminAsync();

        var data = await DataOf(await admin.PutAsJsonAsync(Url(old.Id, "supply"),
            new { supplyStatus = "NRND", successorProductId = next.Id, successorIsDropIn = false, successorNote = "  Firmware V4.  " }));

        data.GetProperty("supplyStatus").GetString().Should().Be("nrnd");
        var successor = data.GetProperty("successor");
        successor.GetProperty("productId").GetGuid().Should().Be(next.Id);
        (successor.GetProperty("isDropIn").GetBoolean(), successor.GetProperty("note").GetString()).Should().Be((false, "Firmware V4."));

        await using var db = Db();
        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == old.Id);
        (stored.SupplyStatus, stored.SuccessorProductId, stored.SuccessorNote).Should().Be((SupplyStatus.Nrnd, next.Id, "Firmware V4."));

        // A later call that leaves the note out keeps it while the successor stays the same.
        var again = await DataOf(await admin.PutAsJsonAsync(Url(old.Id, "supply"),
            new { supplyStatus = "obsolete", successorProductId = next.Id }));
        again.GetProperty("successor").GetProperty("note").GetString().Should().Be("Firmware V4.");

        // Back in supply: no successor.
        var active = await DataOf(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "active" }));
        active.GetProperty("successor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task SetSupply_StoresTheDropInFlag_KeepsItWhenLeftOut_AndChangesItWhenGiven()
    {
        var (old, next) = (await SameSellerProductsAsync(2)) is [var a, var b] ? (a, b) : throw new InvalidOperationException();
        using var admin = await AdminAsync();
        static bool DropIn(JsonElement data) => data.GetProperty("successor").GetProperty("isDropIn").GetBoolean();

        DropIn(await DataOf(await admin.PutAsJsonAsync(Url(old.Id, "supply"),
            new { supplyStatus = "nrnd", successorProductId = next.Id, successorIsDropIn = true }))).Should().BeTrue();
        DropIn(await DataOf(await admin.PutAsJsonAsync(Url(old.Id, "supply"),
            new { supplyStatus = "obsolete", successorProductId = next.Id }))).Should().BeTrue("left out, so kept");
        DropIn(await DataOf(await admin.PutAsJsonAsync(Url(old.Id, "supply"),
            new { supplyStatus = "obsolete", successorProductId = next.Id, successorIsDropIn = false }))).Should().BeFalse();

        await using var db = Db();
        (await db.Products.AsNoTracking().SingleAsync(p => p.Id == old.Id)).SuccessorIsDropIn.Should().BeFalse();
    }

    [PostgresFact]
    public async Task SetSupply_RefusesBadInput_WithA400_NeverA500()
    {
        var (old, next) = (await SameSellerProductsAsync(2)) is [var a, var b] ? (a, b) : throw new InvalidOperationException();
        var otherSeller = (await SameSellerProductsAsync(1))[0];
        using var admin = await AdminAsync();

        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "discontinued" }))).Should().Contain("supplyStatus");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { }))).Should().Contain("supplyStatus");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "active", successorProductId = next.Id })))
            .Should().Contain("successorProductId");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "nrnd", successorProductId = old.Id })))
            .Should().Contain("successorProductId");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "nrnd", successorProductId = Guid.NewGuid() })))
            .Should().Contain("does not exist");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "nrnd", successorProductId = otherSeller.Id })))
            .Should().Contain("same seller");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"),
            new { supplyStatus = "nrnd", successorProductId = next.Id, successorNote = new string('x', Product.MaxSuccessorNoteLength + 1) })))
            .Should().Contain("successorNote");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(old.Id, "supply"), new { supplyStatus = "nrnd", successorNote = "no successor" })))
            .Should().Contain("successorProductId");
        (await admin.PutAsync(Url(old.Id, "supply"), new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync(Url(Guid.NewGuid(), "supply"), new { supplyStatus = "nrnd" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = Db();
        (await db.Products.AsNoTracking().SingleAsync(p => p.Id == old.Id)).SupplyStatus.Should().Be(SupplyStatus.Active, "nothing was saved");
    }

    // ----- alternatives -----

    [PostgresFact]
    public async Task SetAlternatives_StoresTheListInOrder_ReplacesIt_AndClearsIt()
    {
        var three = await SameSellerProductsAsync(3);
        using var admin = await AdminAsync();

        var first = await DataOf(await admin.PutAsJsonAsync(Url(three[0].Id, "alternatives"),
            new { alternativeProductIds = new[] { three[2].Id, three[1].Id } }));
        first.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("productId").GetGuid())
            .Should().Equal(three[2].Id, three[1].Id);

        var swapped = await DataOf(await admin.PutAsJsonAsync(Url(three[0].Id, "alternatives"),
            new { alternativeProductIds = new[] { three[1].Id } }));
        swapped.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("productId").GetGuid()).Should().Equal(three[1].Id);

        await using (var db = Db())
            (await db.Set<ProductAlternative>().CountAsync(a => a.ProductId == three[0].Id)).Should().Be(1);

        var cleared = await DataOf(await admin.PutAsJsonAsync(Url(three[0].Id, "alternatives"), new { alternativeProductIds = Array.Empty<Guid>() }));
        cleared.GetProperty("alternatives").GetArrayLength().Should().Be(0);
    }

    [PostgresFact]
    public async Task SetAlternatives_ShowsOneThatIsNoLongerListed_AndRefusesToSaveItAgain()
    {
        var two = await SameSellerProductsAsync(2);
        using var admin = await AdminAsync();
        await DataOf(await admin.PutAsJsonAsync(Url(two[0].Id, "alternatives"), new { alternativeProductIds = new[] { two[1].Id } }));

        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET supply_status = 'obsolete' WHERE id = {two[1].Id}");

        var data = await DataOf(await admin.GetAsync(Url(two[0].Id, "pim")));
        var alt = data.GetProperty("alternatives")[0];
        (alt.GetProperty("listed").GetBoolean(), alt.GetProperty("supplyStatus").GetString()).Should().Be((false, "obsolete"));

        (await FieldOfError(await admin.PutAsJsonAsync(Url(two[0].Id, "alternatives"), new { alternativeProductIds = new[] { two[1].Id } })))
            .Should().Contain("not obsolete");
    }

    [PostgresFact]
    public async Task SetAlternatives_RefusesBadInput_WithA400_NeverA500()
    {
        var two = await SameSellerProductsAsync(2);
        using var admin = await AdminAsync();
        var url = Url(two[0].Id, "alternatives");

        (await FieldOfError(await admin.PutAsJsonAsync(url, new { }))).Should().Contain("required");
        (await FieldOfError(await admin.PutAsJsonAsync(url, new { alternativeProductIds = new[] { two[1].Id, two[1].Id } }))).Should().Contain("twice");
        (await FieldOfError(await admin.PutAsJsonAsync(url, new { alternativeProductIds = new[] { two[0].Id } }))).Should().Contain("own alternative");
        (await FieldOfError(await admin.PutAsJsonAsync(url, new { alternativeProductIds = new[] { Guid.NewGuid() } }))).Should().Contain("do not exist");
        (await FieldOfError(await admin.PutAsJsonAsync(url,
            new { alternativeProductIds = Enumerable.Range(0, Product.MaxAlternatives + 1).Select(_ => Guid.NewGuid()).ToArray() })))
            .Should().Contain("at most");
        (await admin.PutAsJsonAsync(Url(Guid.NewGuid(), "alternatives"), new { alternativeProductIds = Array.Empty<Guid>() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- order units -----

    [PostgresFact]
    public async Task SetOrderUnits_StoresThem_AndRefusesBadOnesWithA400()
    {
        var p = (await SameSellerProductsAsync(1))[0];
        var variant = p.Variants.Single().Id;
        using var admin = await AdminAsync();
        var url = Url(p.Id, $"variants/{variant}/order-units");

        var data = await DataOf(await admin.PutAsJsonAsync(url, new { minOrderQty = 100, packSize = 50 }));
        var stored = data.GetProperty("variants")[0];
        (stored.GetProperty("minOrderQty").GetInt32(), stored.GetProperty("packSize").GetInt32()).Should().Be((100, 50));

        (await FieldOfError(await admin.PutAsJsonAsync(url, new { minOrderQty = 25, packSize = 10 }))).Should().Contain("whole number of packs");
        (await FieldOfError(await admin.PutAsJsonAsync(url, new { minOrderQty = 0, packSize = 1 }))).Should().Contain("minOrderQty");
        (await FieldOfError(await admin.PutAsJsonAsync(url, new { minOrderQty = 1, packSize = 0 }))).Should().Contain("packSize");
        (await FieldOfError(await admin.PutAsJsonAsync(Url(p.Id, $"variants/{Guid.NewGuid()}/order-units"), new { minOrderQty = 1, packSize = 1 })))
            .Should().NotBeNull();

        await using var db = Db();
        var after = await db.ProductVariants.AsNoTracking().SingleAsync(v => v.Id == variant);
        (after.MinOrderQty, after.PackSize).Should().Be((100, 50), "the refused calls changed nothing");
    }

    // ----- audit -----

    [PostgresFact]
    public async Task AChange_IsAudited_WithWhoMadeIt()
    {
        var p = (await SameSellerProductsAsync(1))[0];
        using var admin = await AdminAsync();
        await DataOf(await admin.PutAsJsonAsync(Url(p.Id, $"variants/{p.Variants.Single().Id}/order-units"), new { minOrderQty = 20, packSize = 10 }));

        await using var db = Db();
        var adminId = await db.Users.Where(u => u.Email == AdminEmail).Select(u => u.Id).SingleAsync();
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == p.Variants.Single().Id.ToString()).ToListAsync();
        rows.Should().Contain(a => a.ActorId == adminId && a.NewValuesJson!.Contains("20"));
    }
}
