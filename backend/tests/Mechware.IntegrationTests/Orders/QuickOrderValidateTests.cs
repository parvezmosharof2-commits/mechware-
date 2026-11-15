using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Identity;
using Mechware.IntegrationTests.Identity;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Pricing;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Support.PriceListTestData;

namespace Mechware.IntegrationTests.Orders;

public sealed class QuickOrderFactory() : DbBackedFactory(null, new Dictionary<string, string>
{
    ["Cart:NewGuestCartsPerIpPerHour"] = "100000",
    ["RateLimiting:QuickOrderPermitPerMinute"] = "30"
});

/// <summary>D5.2: POST /api/v1/quick-order/validate against real PostgreSQL (and Redis for the cart parity check).</summary>
public sealed class QuickOrderValidateTests(QuickOrderFactory factory) : IClassFixture<QuickOrderFactory>
{
    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    /// <summary>Hex digits only: no O, I or L, so a random prefix never changes under the look-alike rule.</summary>
    private static string Prefix() => "Q" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private sealed record Seeded(Guid ProductId, Guid VariantId, string Sku, string Slug);

    private async Task<Seeded> SeedAsync(string? partNumber, int onHand = 100, long price = 100_000, int min = 1, int pack = 1)
    {
        await using var db = Db();
        var s = await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
        var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == s.ProductId);
        var v = p.Variants.Single();
        if (partNumber is not null) p.SetManufacturerPartNumber(v.Id, partNumber);
        if (min != 1 || pack != 1) p.SetOrderUnits(v.Id, min, pack);
        await db.SaveChangesAsync();
        return new Seeded(p.Id, v.Id, v.Sku, p.Slug);
    }

    private async Task MutateProductAsync(Guid productId, Action<Product> change)
    {
        await using var db = Db();
        var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == productId);
        change(p);
        await db.SaveChangesAsync();
    }

    private static string RandomIp() => $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string text, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quick-order/validate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip ?? RandomIp());
        return await client.SendAsync(request);
    }

    private async Task<(JsonElement Data, string Raw)> ValidateAsync(string text)
    {
        using var client = factory.CreateClient();
        var response = await PostAsync(client, text);
        string raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return (JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone(), raw);
    }

    private static JsonElement Line(JsonElement data, int index = 0) => data.GetProperty("lines")[index];

    [PostgresFact]
    public async Task AFoundPart_IsPricedWithThePublicPrice_AndReportsStockAndNoCost()
    {
        string pn = Prefix() + "LC1D0912";
        var s = await SeedAsync(pn, onHand: 3, price: 123_400);
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = 77123 WHERE id = {s.VariantId}");

        var (data, raw) = await ValidateAsync($"{pn.ToLowerInvariant()} 2");
        var line = Line(data);
        line.GetProperty("status").GetString().Should().Be("OK");
        line.GetProperty("valid").GetBoolean().Should().BeTrue();
        line.GetProperty("quantity").GetInt32().Should().Be(2);
        line.GetProperty("unitPriceMinor").GetInt64().Should().Be(123_400);
        line.GetProperty("lineTotalMinor").GetInt64().Should().Be(246_800);
        line.GetProperty("priceSource").GetString().Should().Be("list");
        line.GetProperty("stock").GetProperty("status").GetString().Should().Be("low_stock");
        line.GetProperty("variant").GetProperty("slug").GetString().Should().Be(s.Slug);
        line.GetProperty("variant").GetProperty("sku").GetString().Should().Be(s.Sku);
        line.GetProperty("lineNumbers")[0].GetInt32().Should().Be(1);
        data.GetProperty("summary").GetProperty("valid").GetInt32().Should().Be(1);
        data.GetProperty("summary").GetProperty("subtotalMinor").GetInt64().Should().Be(246_800);

        raw.Should().NotContainEquivalentOf("cost").And.NotContain("77123").And.NotContainEquivalentOf("margin")
            .And.NotContainEquivalentOf("supplier").And.NotContainEquivalentOf("commission");
    }

    [PostgresFact]
    public async Task BanglaDigits_AreRead_AndAnOForAZeroGivesDidYouMean_WithPerLineErrors()
    {
        string pre = Prefix();
        var s = await SeedAsync(pre + "LC1D0912", price: 50_000);

        string bangla = pre + "LC1D০৯১২";                // digits typed on a Bangla keyboard
        string typo = pre + "LC1DO912";                  // letter O for the digit 0
        var (data, _) = await ValidateAsync($"{bangla}\t৫\n{typo} 4\n{pre}ZZ9 2.5\n{pre}NONE9 3");
        var lines = data.GetProperty("lines");

        lines[0].GetProperty("status").GetString().Should().Be("OK");
        lines[0].GetProperty("quantity").GetInt32().Should().Be(5);

        lines[1].GetProperty("status").GetString().Should().Be("PART_NOT_FOUND");
        lines[1].GetProperty("valid").GetBoolean().Should().BeFalse();
        var suggestions = lines[1].GetProperty("suggestions");
        suggestions.GetArrayLength().Should().Be(1);
        suggestions[0].GetProperty("sku").GetString().Should().Be(s.Sku);

        lines[2].GetProperty("status").GetString().Should().Be("PART_NOT_FOUND");   // text line 3 is a parse error (not a line); text line 4 is unknown
        lines[2].GetProperty("suggestions").GetArrayLength().Should().Be(0);
        data.GetProperty("errors").EnumerateArray().Should().ContainSingle(e => e.GetProperty("code").GetString() == "QTY_NOT_INTEGER" && e.GetProperty("lineNumber").GetInt32() == 3);
        data.GetProperty("summary").GetProperty("needsAttention").GetInt32().Should().Be(2);
    }

    [PostgresFact]
    public async Task APartNumberSharedByTwoVariants_ListsTheCandidates_AndNeverGuesses()
    {
        string pn = Prefix() + "SHARED77";
        var a = await SeedAsync(pn);
        var b = await SeedAsync(pn);
        var (data, _) = await ValidateAsync($"{pn} 1");
        var line = Line(data);
        line.GetProperty("status").GetString().Should().Be("PART_AMBIGUOUS");
        line.GetProperty("variant").ValueKind.Should().Be(JsonValueKind.Null);
        line.GetProperty("unitPriceMinor").ValueKind.Should().Be(JsonValueKind.Null);
        line.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("sku").GetString()).Should().BeEquivalentTo([a.Sku, b.Sku]);
    }

    [PostgresFact]
    public async Task DraftArchivedAndInactive_AreIndistinguishableFromAnUnknownPartNumber()
    {
        string pre = Prefix();
        var archived = await SeedAsync(pre + "ARCH1");
        await MutateProductAsync(archived.ProductId, p => p.Archive());
        var draft = await SeedAsync(pre + "DRAFT1");
        await MutateProductAsync(draft.ProductId, p => { p.Archive(); p.ReturnToDraft(); });
        var inactive = await SeedAsync(pre + "OFF1");
        await MutateProductAsync(inactive.ProductId, p => p.Variants.Single().Deactivate());

        var (data, raw) = await ValidateAsync($"{pre}ARCH1 1\n{pre}DRAFT1 1\n{pre}OFF1 1\n{pre}NEVERHEARD1 1");
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(4);
        foreach (var l in lines)
        {
            l.GetProperty("status").GetString().Should().Be("PART_NOT_FOUND");
            l.GetProperty("message").GetString().Should().Be(lines[3].GetProperty("message").GetString());
            l.GetProperty("variant").ValueKind.Should().Be(JsonValueKind.Null);
            l.GetProperty("suggestions").GetArrayLength().Should().Be(0);
            l.GetProperty("candidates").GetArrayLength().Should().Be(0);
        }

        raw.Should().NotContain(archived.Slug).And.NotContain(draft.Slug).And.NotContain(inactive.Slug);
        data.GetProperty("summary").GetProperty("subtotalMinor").GetInt64().Should().Be(0);
    }

    [PostgresFact]
    public async Task AnObsoletePart_ReturnsItsSuccessor_AndIsNeverSwappedOrPriced()
    {
        string pre = Prefix();
        var old = await SeedAsync(pre + "OLD1");
        var next = await SeedAsync(pre + "NEW1");
        await using (var db = Db())
        {
            // A successor must belong to the same seller: move the second product to the first one's seller.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE products SET seller_id = (SELECT seller_id FROM products WHERE id = {old.ProductId}) WHERE id = {next.ProductId}");
            var oldProduct = await db.Products.SingleAsync(p => p.Id == old.ProductId);
            var nextProduct = await db.Products.SingleAsync(p => p.Id == next.ProductId);
            oldProduct.SetSupply(SupplyStatus.Obsolete, nextProduct, true, "Same footprint");
            await db.SaveChangesAsync();
        }

        var (data, _) = await ValidateAsync($"{pre}OLD1 2\n{pre}NEW1 1");
        var obsolete = Line(data, 0);
        obsolete.GetProperty("status").GetString().Should().Be("PART_OBSOLETE");
        obsolete.GetProperty("valid").GetBoolean().Should().BeFalse();
        obsolete.GetProperty("unitPriceMinor").ValueKind.Should().Be(JsonValueKind.Null);
        obsolete.GetProperty("variant").GetProperty("sku").GetString().Should().Be(old.Sku);
        var successor = obsolete.GetProperty("successor");
        successor.GetProperty("variant").GetProperty("sku").GetString().Should().Be(next.Sku);
        successor.GetProperty("variant").GetProperty("id").GetGuid().Should().Be(next.VariantId);
        successor.GetProperty("isDropIn").GetBoolean().Should().BeTrue();
        successor.GetProperty("note").GetString().Should().Be("Same footprint");
        Line(data, 1).GetProperty("status").GetString().Should().Be("OK");
    }

    [PostgresTheory]
    [InlineData(3, 10, 5, 10, "belowMinimumOrder")]
    [InlineData(12, 10, 5, 15, "quantityAdjustedToPack")]
    [InlineData(20, 10, 5, 20, null)]
    public async Task MinimumAndPackSize_AreAppliedWithTheCartsRules_AndTheCartTakesTheResult(int asked, int min, int pack, int expected, string? notice)
    {
        string pn = Prefix() + "PACK5";
        var s = await SeedAsync(pn, onHand: 500, min: min, pack: pack);
        var (data, _) = await ValidateAsync($"{pn} {asked}");
        var line = Line(data);
        line.GetProperty("status").GetString().Should().Be("OK");
        line.GetProperty("requestedQuantity").GetInt32().Should().Be(asked);
        line.GetProperty("quantity").GetInt32().Should().Be(expected);
        line.GetProperty("lineTotalMinor").GetInt64().Should().Be(100_000L * expected);
        var kinds = line.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList();
        if (notice is null) kinds.Should().BeEmpty(); else kinds.Should().Equal(notice);

        // The real cart accepts exactly that quantity.
        if (!TestInfra.RedisAvailable) return;
        using var client = factory.CreateClient();
        var add = await client.PostAsJsonAsync("/api/v1/cart/items", new { variantId = s.VariantId, quantity = expected });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Stock_OutOfStockAndInsufficient_NeedAttention_ButStayPriced()
    {
        string pre = Prefix();
        await SeedAsync(pre + "NONE1", onHand: 0);
        await SeedAsync(pre + "FEW1", onHand: 10);
        await SeedAsync(pre + "PLENTY1", onHand: 1000);
        var (data, _) = await ValidateAsync($"{pre}NONE1 1\n{pre}FEW1 20\n{pre}PLENTY1 7");
        Line(data, 0).GetProperty("status").GetString().Should().Be("OUT_OF_STOCK");
        Line(data, 0).GetProperty("stock").GetProperty("status").GetString().Should().Be("out_of_stock");
        Line(data, 1).GetProperty("status").GetString().Should().Be("INSUFFICIENT_STOCK");
        Line(data, 1).GetProperty("unitPriceMinor").GetInt64().Should().Be(100_000);
        Line(data, 2).GetProperty("status").GetString().Should().Be("OK");
        Line(data, 2).GetProperty("stock").GetProperty("status").GetString().Should().Be("in_stock");
        data.GetProperty("summary").GetProperty("subtotalMinor").GetInt64().Should().Be(700_000);
        data.GetProperty("summary").GetProperty("needsAttention").GetInt32().Should().Be(2);
    }

    [PostgresFact]
    public async Task APublicPriceList_AppliesAtItsBreak_ThroughTheResolver_AndOnTheTotalOfAVariantOnSeveralLines()
    {
        string pn = Prefix() + "BREAK2";
        var s = await SeedAsync(pn, onHand: 1000, price: 100_000);
        DateTime now = WholeSecond(DateTime.UtcNow);
        await using (var db = Db())
            await ApprovedAsync(db, PriceListScope.General, null, now.AddHours(-3), now.AddDays(30), now.AddHours(-2), new Entry(s.VariantId, 10, 80_000));

        var (below, _) = await ValidateAsync($"{pn} 9");
        Line(below).GetProperty("unitPriceMinor").GetInt64().Should().Be(100_000);
        Line(below).GetProperty("priceSource").GetString().Should().Be("list");
        var (atBreak, _) = await ValidateAsync($"{pn} 10");
        Line(atBreak).GetProperty("unitPriceMinor").GetInt64().Should().Be(80_000);
        Line(atBreak).GetProperty("priceSource").GetString().Should().Be("quantityBreak");
        Line(atBreak).GetProperty("lineTotalMinor").GetInt64().Should().Be(800_000);

        // The same variant by part number and by SKU: 6 + 6 = 12 units, priced together past the break of 10.
        var (twice, _) = await ValidateAsync($"{pn} 6\n{s.Sku} 6");
        var lines = twice.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines.Should().OnlyContain(l => l.GetProperty("unitPriceMinor").GetInt64() == 80_000);
        lines.Should().OnlyContain(l => l.GetProperty("notices").EnumerateArray().Any(n => n.GetProperty("kind").GetString() == "sameVariantOnSeveralLines"));
    }

    [PostgresFact]
    public async Task GuestAndSignedInBuyer_GetTheSamePublicResult()
    {
        string pn = Prefix() + "SAME1";
        await SeedAsync(pn);
        using var guest = factory.CreateClient();
        string guestRaw = await (await PostAsync(guest, pn + " 3")).Content.ReadAsStringAsync();

        string email = $"qo.{Guid.NewGuid():N}@example.test";
        await using (var db = Db())
        {
            var user = Mechware.Domain.Identity.User.Create(email, null, new PasswordHasherService().HashPassword("Buyer#Test2026xyz"), "Q Buyer", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Mechware.Domain.Identity.Permissions.Roles.Buyer);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { identifier = email, password = "Buyer#Test2026xyz" }) };
        login.Headers.TryAddWithoutValidation("X-Forwarded-For", RandomIp());
        var loginResponse = await guest.SendAsync(login);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string token = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;

        using var signedIn = factory.CreateClient();
        signedIn.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        string userRaw = await (await PostAsync(signedIn, pn + " 3")).Content.ReadAsStringAsync();
        userRaw.Should().Be(guestRaw);
    }

    private async Task<List<string>> SeedManyAsync(string pre, int count)
    {
        var seeded = await SeedAsync(null, onHand: 1000, price: 25_000);
        var numbers = new List<string>();
        await using var db = Db();
        var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == seeded.ProductId);
        var warehouse = await db.InventoryItems.Where(i => i.VariantId == seeded.VariantId).Select(i => i.WarehouseId).SingleAsync();
        var items = new List<Mechware.Domain.Inventory.InventoryItem>();
        for (int i = 0; i < count; i++)
        {
            string pn = $"{pre}X0{i:D4}";
            Guid id = i == 0 ? seeded.VariantId : Guid.NewGuid();
            var v = i == 0 ? p.Variants.Single() : p.AddVariant(id, $"{pre}-SKU-{i:D4}", null, new Dictionary<string, string> { ["Model"] = $"M{i}" },
                Money.FromMinorUnits(25_000, Currency.BDT), null, null, 0.5m, false);
            p.SetManufacturerPartNumber(v.Id, pn);
            if (i > 0) items.Add(new Mechware.Domain.Inventory.InventoryItem(Guid.NewGuid(), id, warehouse, 1000, 0));
            numbers.Add(pn);
        }

        await db.SaveChangesAsync();
        db.InventoryItems.AddRange(items);
        await db.SaveChangesAsync();
        return numbers;
    }

    /// <summary>300 found + 100 unknown + 50 O/0 typos + 30 bad quantities + 20 repeats of found parts (merged) = 500 content lines.</summary>
    private static string FiveHundredLines(string pre, List<string> have)
    {
        var lines = new List<string>();
        lines.AddRange(have.Take(300).Select((pn, i) => $"{pn}\t{i % 7 + 1}"));
        lines.AddRange(Enumerable.Range(0, 100).Select(i => $"{pre}NOPE{i:D4} 2"));
        lines.AddRange(have.Skip(300).Take(50).Select(pn => pn.Replace("X0", "XO") + " 1"));
        lines.AddRange(Enumerable.Range(0, 30).Select(i => $"{pre}BAD{i:D4} 2.5"));
        lines.AddRange(Enumerable.Range(0, 20).Select(i => $"{have[i]} 1"));
        return string.Join("\n", lines);
    }

    [PostgresFact]
    public async Task FiveHundredMixedLines_ValidateInOneRequestUnderTwoSeconds()
    {
        string pre = Prefix();
        var have = await SeedManyAsync(pre, 350);
        string text = FiveHundredLines(pre, have);
        using var client = factory.CreateClient();
        (await PostAsync(client, "warm 1")).StatusCode.Should().Be(HttpStatusCode.OK);

        var clock = Stopwatch.StartNew();
        var response = await PostAsync(client, text);
        clock.Stop();
        string raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        clock.ElapsedMilliseconds.Should().BeLessThan(2000);
        Console.WriteLine($"QUICK-ORDER-500-LINES ms={clock.ElapsedMilliseconds}");

        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        data.GetProperty("summary").GetProperty("valid").GetInt32().Should().Be(300);
        data.GetProperty("errors").GetArrayLength().Should().Be(30);
        data.GetProperty("lines").EnumerateArray().Count(l => l.GetProperty("suggestions").GetArrayLength() > 0).Should().Be(50);
    }

    [PostgresFact]
    public async Task TheNumberOfSqlQueries_DoesNotGrowWithTheNumberOfLines()
    {
        string pre = Prefix();
        var have = await SeedManyAsync(pre, 350);
        var recorder = new SqlRecorder();
        await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).AddInterceptors(recorder).Options);
        var validator = new QuickOrderValidator(db, new PriceListResolver(db), TimeProvider.System);

        await validator.ValidateAsync("warm 1");
        recorder.Commands.Clear();
        var small = await validator.ValidateAsync($"{have[0]} 1\n{pre}NOPE 1");
        int smallQueries = recorder.Commands.Count;
        recorder.Commands.Clear();
        var big = await validator.ValidateAsync(FiveHundredLines(pre, have));
        int bigQueries = recorder.Commands.Count;

        big.Summary.Lines.Should().BeGreaterThan(400);
        small.Summary.Lines.Should().Be(2);
        bigQueries.Should().BeLessThanOrEqualTo(6, string.Join("\n---\n", recorder.Commands));
        bigQueries.Should().Be(smallQueries);
        Console.WriteLine($"QUICK-ORDER-QUERIES big={bigQueries} small={smallQueries}");
    }

    [PostgresFact]
    public async Task The501stLine_IsRefusedWith422_AndBlankAndCommentLinesDoNotCount()
    {
        using var client = factory.CreateClient();
        string ok = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"ZZ{i:D4} 1")) + "\n\n# comment\n";
        (await PostAsync(client, ok)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await PostAsync(client, string.Join("\n", Enumerable.Range(1, 501).Select(i => $"ZZ{i:D4} 1")));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("TOO_MANY_LINES");
    }

    [PostgresFact]
    public async Task AnOversizedBody_Is413_AndSoIsTooLongText()
    {
        using var client = factory.CreateClient();
        var huge = await PostAsync(client, new string('A', 600_000));
        huge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var tooLong = await PostAsync(client, string.Join("\n", Enumerable.Repeat("AB", 50_001)));   // ~150,000 characters, under the byte limit
        tooLong.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await tooLong.Content.ReadAsStringAsync()).Should().Contain("INPUT_TOO_LARGE");
    }

    [PostgresFact]
    public async Task TheThirtyFirstRequestInAMinute_IsRateLimited_PerIp()
    {
        using var client = factory.CreateClient();
        string ip = RandomIp();
        for (int i = 0; i < 30; i++) (await PostAsync(client, "ZZ0001 1", ip)).StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1}");

        var limited = await PostAsync(client, "ZZ0001 1", ip);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await limited.Content.ReadAsStringAsync()).Should().Contain("RATE_LIMITED");
        (await PostAsync(client, "ZZ0001 1", RandomIp())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task EmptyText_IsAnEmptyResult_AndNothingButTextIsRead()
    {
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quick-order/validate")
        {
            Content = new StringContent("""{ "text": "", "companyId": "00000000-0000-0000-0000-000000000001", "unitPriceMinor": 1 }""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", RandomIp());
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("lines").GetArrayLength().Should().Be(0);
    }
}
