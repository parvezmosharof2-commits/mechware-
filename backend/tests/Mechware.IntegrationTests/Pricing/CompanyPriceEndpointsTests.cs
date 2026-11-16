using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Companies;
using Mechware.Application.Orders;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.Infrastructure.Pricing;
using Mechware.Infrastructure.Search;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Search;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;
using Entry = Mechware.IntegrationTests.Support.PriceListTestData.Entry;

namespace Mechware.IntegrationTests.Pricing;

/// <summary>[Fact] that needs PostgreSQL, Redis and Meilisearch (the public caches and the search index).</summary>
public sealed class PostgresRedisAndMeiliFactAttribute : FactAttribute
{
    public PostgresRedisAndMeiliFactAttribute()
    {
        if (!TestInfra.PostgresAvailable) Skip = "PostgreSQL not reachable (set MECHWARE_TEST_PG).";
        else if (!TestInfra.RedisAvailable) Skip = "Redis not reachable (set MECHWARE_TEST_REDIS).";
        else if (!MeiliInfra.Available) Skip = "Meilisearch not reachable (set MECHWARE_TEST_MEILI).";
    }
}

/// <summary>
/// D2.10: <c>GET /api/v1/companies/{companyId}/prices</c> through the real API (ADR-0008 §11; threat model §৮ PL6, PL9, PL12,
/// PL17). One host and one database per test (sign-ins have a per-host limit). The catalogue price 1,050.00 at 5 % VAT is
/// 1,000.00 net for a company (owner decision 2); contracts are approved two hours ago, so they have started.
/// </summary>
public sealed class CompanyPriceEndpointsTests : IAsyncLifetime
{
    private const string Password = "CompanyPrice#2026!";
    private const long Catalogue = 105_000;
    private static long _binSeq = 8_310_000_000_000L;
    private readonly DateTime _now = PriceListTestData.WholeSecond(DateTime.UtcNow);
    private readonly string _index = "d210_" + Guid.NewGuid().ToString("N")[..12];
    private readonly DbBackedFactory _factory;

    public CompanyPriceEndpointsTests() => _factory = new DbBackedFactory(settings: new Dictionary<string, string>
    {
        ["Search:Url"] = MeiliInfra.Url,
        ["Search:ApiKey"] = MeiliInfra.Key,
        ["Search:IndexName"] = _index,
    });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        if (!MeiliInfra.Available) return;
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        await http.DeleteAsync($"indexes/{_index}");
    }

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    // ---- people and companies ---------------------------------------------------------------------------------------

    private sealed record Person(Guid Id, HttpClient Client);

    private async Task<Guid> UserRowAsync(string role, string email, string? passwordHash)
    {
        await using var db = Db();
        var user = User.Create(email, null, passwordHash, "Company Price " + role, "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.NewGuid(), role);
        db.Users.Add(user);
        db.Addresses.Add(TestUsers.DefaultAddress(user.Id));
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A signed-in account with this platform role.</summary>
    private async Task<Person> PersonAsync(string role = Permissions.Roles.Buyer)
    {
        _factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"cp.{Guid.NewGuid():N}"[..22] + "@example.test";
        var id = await UserRowAsync(role, email, IdentityTestKit.Hasher.HashPassword(Password));
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(id, client);
    }

    /// <summary>A company with these accepted members; verified by MECHWARE unless <paramref name="activate"/> is false.</summary>
    private async Task<Guid> CompanyAsync(bool activate, params (Guid User, CompanyRoles Roles)[] members)
    {
        _factory.CreateClient().Dispose();
        var owner = await UserRowAsync(Permissions.Roles.Buyer, $"owner.{Guid.NewGuid():N}@example.test", null);
        await using var db = Db();
        var companies = new CompanyService(db);
        var bin = Interlocked.Increment(ref _binSeq).ToString("D13");
        var company = await companies.RegisterAsync(owner, new RegisterCompanyRequest("Price Buyer Ltd", "TL-D210-" + bin[^5..], bin, null, null));
        foreach (var (user, roles) in members) await companies.AddAcceptedMemberAsync(owner, company.Id, new AddMemberRequest(user, roles, null, null));
        if (activate) await companies.AdminActivateAsync(company.Id);
        return company.Id;
    }

    private Task<Guid> CompanyAsync(params (Guid User, CompanyRoles Roles)[] members) => CompanyAsync(true, members);

    // ---- catalogue and lists ----------------------------------------------------------------------------------------

    private async Task<SeededVariant> SeededAsync(long price = Catalogue)
    {
        await using var db = Db();
        return await CatalogTestData.SeedVariantAsync(db, onHand: 500, priceMinor: price);
    }

    private async Task<Guid> VariantAsync(long price = Catalogue) => (await SeededAsync(price)).VariantId;

    private async Task<PriceList> ListAsync(PriceListScope scope, Guid? company, params Entry[] entries)
    {
        await using var db = Db();
        return await PriceListTestData.ApprovedAsync(db, scope, company, _now.AddHours(-3), _now.AddDays(30), _now.AddHours(-2), entries);
    }

    private Task<PriceList> ContractAsync(Guid company, params Entry[] entries) => ListAsync(PriceListScope.Company, company, entries);

    // ---- calls ----------------------------------------------------------------------------------------------------

    private static string Items(params (Guid Variant, int? Qty)[] items) =>
        string.Join(',', items.Select(i => i.Qty is { } q ? $"{i.Variant}:{q}" : i.Variant.ToString()));

    private static Task<HttpResponseMessage> PricesAsync(HttpClient client, Guid company, string? items) =>
        client.GetAsync($"/api/v1/companies/{company}/prices" + (items is null ? "" : "?items=" + Uri.EscapeDataString(items)));

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Dictionary<Guid, JsonElement>> PricedAsync(HttpClient client, Guid company, params (Guid Variant, int? Qty)[] items) =>
        (await DataAsync(await PricesAsync(client, company, Items(items)))).GetProperty("items").EnumerateArray()
        .ToDictionary(i => i.GetProperty("variantId").GetGuid());

    private static async Task<JsonElement> OneAsync(HttpClient client, Guid company, Guid variant, int qty = 1) =>
        (await PricedAsync(client, company, (variant, qty)))[variant];

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    /// <summary>A 404 body without what differs per request (trace id, path), to compare two refusals.</summary>
    private static async Task<string> NotFoundBodyAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.NotFound, text);
        var node = JsonNode.Parse(text)!.AsObject();
        node.Remove("traceId");
        node.Remove("instance");
        node["error"]!.AsObject().Remove("traceId");
        return node.ToJsonString();
    }

    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static List<(int MinQty, long Price)> Breaks(JsonElement e) =>
        e.GetProperty("breaks").EnumerateArray().Select(b => (b.GetProperty("minQty").GetInt32(), b.GetProperty("unitPriceMinorNet").GetInt64())).ToList();

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

    // ---- the price --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ACompanyBuyer_GetsTheContractPrice_WithTheLineMoneyOfAnOrder()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var list = await ContractAsync(company, new Entry(v, 1, 90_000));

        var data = await DataAsync(await PricesAsync(buyer.Client, company, Items((v, 2))));
        data.GetProperty("companyId").GetGuid().Should().Be(company);
        var item = data.GetProperty("items").EnumerateArray().Single();

        (Text(item, "status"), item.GetProperty("quantity").GetInt32(), Long(item, "unitPriceMinorNet"), Long(item, "publicUnitPriceMinorNet"),
                Long(item, "guestUnitPriceMinorInclVat"), Text(item, "source"), Text(item, "taxMode"), item.GetProperty("vatRate").GetDecimal())
            .Should().Be(("available", 2, 90_000L, 100_000L, Catalogue, "contract", "exclusive", 0.05m));
        (item.GetProperty("priceListId").GetGuid(), Text(item, "contractRef"), item.GetProperty("validToUtc").GetDateTime().ToUniversalTime())
            .Should().Be((list.Id, list.ContractRef, list.ValidToUtc));
        (Long(item, "lineNetMinor"), Long(item, "lineTaxMinor"), Long(item, "lineTotalMinor")).Should().Be((180_000L, 9_000L, 189_000L));
        Breaks(item).Should().BeEmpty("a contract with one tier from 1 unit has no break table");
        item.TryGetProperty("quantityProblem", out _).Should().BeFalse();
    }

    [PostgresFact]
    public async Task ThePriceShown_IsWhatAnOrderForTheSameCartIsCharged()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var tiered = await VariantAsync();
        var plain = await VariantAsync(123_457);
        var publicBreak = await VariantAsync();
        await ContractAsync(company, new Entry(tiered, 1, 95_000), new Entry(tiered, 10, 90_001));
        await ListAsync(PriceListScope.General, null, new Entry(publicBreak, 5, 99_750));
        (Guid Variant, int Qty)[] cart = [(tiered, 12), (plain, 3), (publicBreak, 5)];

        var shown = await PricedAsync(buyer.Client, company, cart.Select(c => (c.Variant, (int?)c.Qty)).ToArray());

        await using var db = Db();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new PriceListResolver(db));
        var lines = cart.Select(c => new PlaceOrderLine(c.Variant, c.Qty)).ToList();
        var quote = await orders.QuoteShippingAsync(new ShippingQuoteRequest(buyer.Id, lines, null, company));
        var placed = await orders.PlaceAsync(new PlaceOrderRequest(buyer.Id, "key-" + Guid.NewGuid().ToString("N"), lines, null, company));

        var items = await db.OrderItems.AsNoTracking().Where(i => db.SubOrders.Any(s => s.Id == i.SubOrderId && s.OrderId == placed.OrderId)).ToListAsync();
        items.Should().HaveCount(3);
        foreach (var item in items)
        {
            var s = shown[item.VariantId];
            (item.Quantity, item.UnitPriceMinor, item.NetMinor, item.TaxMinor, item.TotalMinor, item.DiscountMinor)
                .Should().Be((s.GetProperty("quantity").GetInt32(), Long(s, "unitPriceMinorNet"), Long(s, "lineNetMinor"), Long(s, "lineTaxMinor"), Long(s, "lineTotalMinor"), 0L));
            ((string?)OrderService.ToWire(item.PriceSource!.Value), item.ContractRef, item.PublicUnitPriceMinor)
                .Should().Be((Text(s, "source"), Text(s, "contractRef"), (long?)Long(s, "publicUnitPriceMinorNet")));
            if (item.PriceSource == PriceSource.Contract) item.PriceListId.Should().Be(s.GetProperty("priceListId").GetGuid());
        }

        Text(shown[tiered], "source").Should().Be("contract");
        Long(shown[tiered], "unitPriceMinorNet").Should().Be(90_001, "12 units reach the 10-unit tier");
        Text(shown[plain], "source").Should().Be("list");
        Long(shown[plain], "unitPriceMinorNet").Should().Be(117_578, "123,457 incl. 5 % VAT is 117,578.09 net, rounded down");
        Text(shown[publicBreak], "source").Should().Be("quantity_break");
        Long(shown[publicBreak], "unitPriceMinorNet").Should().Be(95_000, "the public break 99,750 incl. VAT is 95,000 net");

        var goods = shown.Values.Sum(s => Long(s, "lineTotalMinor"));
        (quote.TaxMinor, quote.GrandTotalMinor - quote.TotalMinor).Should().Be((shown.Values.Sum(s => Long(s, "lineTaxMinor")), goods));
        (placed.GrandTotalMinor - quote.TotalMinor).Should().Be(goods, "the order total is the shown lines plus shipping");
    }

    [PostgresFact]
    public async Task AContractPriceAboveThePublicPrice_Loses_AndThePublicPriceIsShownWithSourceList()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await ContractAsync(company, new Entry(v, 1, 100_001));

        var item = await OneAsync(buyer.Client, company, v);

        (Long(item, "unitPriceMinorNet"), Text(item, "source"), Long(item, "lineTotalMinor")).Should().Be((100_000L, "list", Catalogue));
        item.TryGetProperty("priceListId", out _).Should().BeFalse();
        item.TryGetProperty("contractRef", out _).Should().BeFalse();
        item.TryGetProperty("validToUtc", out _).Should().BeFalse();
    }

    [PostgresFact]
    public async Task TheQuantityPicksTheTier_AndTheBreakTableIsOnlyTheCallersOwnContract()
    {
        var a = await PersonAsync();
        var b = await PersonAsync();
        var none = await PersonAsync();
        var companyA = await CompanyAsync((a.Id, CompanyRoles.Buyer));
        var companyB = await CompanyAsync((b.Id, CompanyRoles.Buyer));
        var companyNone = await CompanyAsync((none.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await ContractAsync(companyA, new Entry(v, 1, 95_111), new Entry(v, 10, 90_111), new Entry(v, 50, 85_111));
        await ContractAsync(companyB, new Entry(v, 1, 80_222), new Entry(v, 5, 70_222));

        foreach (var (qty, expected) in new[] { (1, 95_111L), (9, 95_111L), (10, 90_111L), (49, 90_111L), (50, 85_111L), (10_000, 85_111L) })
            Long(await OneAsync(a.Client, companyA, v, qty), "unitPriceMinorNet").Should().Be(expected, $"{qty} units");

        var textA = await (await PricesAsync(a.Client, companyA, Items((v, 1)))).Content.ReadAsStringAsync();
        var textB = await (await PricesAsync(b.Client, companyB, Items((v, 1)))).Content.ReadAsStringAsync();
        Breaks(await OneAsync(a.Client, companyA, v)).Should().Equal((1, 95_111L), (10, 90_111L), (50, 85_111L));
        Breaks(await OneAsync(b.Client, companyB, v)).Should().Equal((1, 80_222L), (5, 70_222L));
        foreach (var n in new[] { "80222", "70222" }) textA.Should().NotContain(n);
        foreach (var n in new[] { "95111", "90111", "85111" }) textB.Should().NotContain(n);

        var plain = await OneAsync(none.Client, companyNone, v, 50);
        (Long(plain, "unitPriceMinorNet"), Text(plain, "source")).Should().Be((100_000L, "list"));
        Breaks(plain).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task AWithdrawnOrExpiredContract_IsNoLongerShown_NorAreItsTiers()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var withdrawn = await VariantAsync();
        var expired = await VariantAsync();
        var live = await VariantAsync();
        var gone = await ContractAsync(company, new Entry(withdrawn, 1, 90_000), new Entry(withdrawn, 10, 80_000));
        await using (var db = Db())
        {
            await PriceListTestData.WithdrawAsync(db, gone.Id, _now.AddMinutes(-1));
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, company, _now.AddHours(-5), _now.AddHours(-1), _now.AddHours(-4),
                new Entry(expired, 1, 90_000), new Entry(expired, 10, 80_000));
        }

        await ContractAsync(company, new Entry(live, 1, 91_000), new Entry(live, 10, 81_000));

        var shown = await PricedAsync(buyer.Client, company, (withdrawn, 10), (expired, 10), (live, 10));
        foreach (var v in new[] { withdrawn, expired })
        {
            (Long(shown[v], "unitPriceMinorNet"), Text(shown[v], "source")).Should().Be((100_000L, "list"));
            Breaks(shown[v]).Should().BeEmpty();
        }

        (Long(shown[live], "unitPriceMinorNet"), Text(shown[live], "source")).Should().Be((81_000L, "contract"));
        Breaks(shown[live]).Should().Equal((1, 91_000L), (10, 81_000L));
    }

    [PostgresFact]
    public async Task UnknownUnpublishedOrInactiveVariants_AreUnavailable_WithoutPricesAndAlike()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var sellable = await VariantAsync();
        var draft = await SeededAsync();
        var inactive = await VariantAsync();
        await ContractAsync(company, new Entry(draft.VariantId, 1, 77_777), new Entry(inactive, 1, 77_777));
        await using (var db = Db())
        {
            await db.Products.Where(p => p.Id == draft.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProductStatus.Draft));
            await db.ProductVariants.Where(v => v.Id == inactive).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VariantStatus.Inactive));
        }

        var unknown = Guid.NewGuid();
        var response = await PricesAsync(buyer.Client, company, Items((unknown, 3), (draft.VariantId, 1), (sellable, 1), (inactive, 2)));
        var text = await response.Content.ReadAsStringAsync();
        var items = (await DataAsync(response)).GetProperty("items").EnumerateArray().ToList();

        items.Select(i => i.GetProperty("variantId").GetGuid()).Should().Equal(unknown, draft.VariantId, sellable, inactive);
        foreach (var i in new[] { items[0], items[1], items[3] })
            i.EnumerateObject().Select(p => p.Name).Should().Equal(["variantId", "status"], "an unavailable variant has no price and nothing that tells why");
        items.Select(i => Text(i, "status")).Should().Equal("unavailable", "unavailable", "available", "unavailable");
        text.Should().NotContain("77777");
    }

    [PostgresFact]
    public async Task MalformedItems_Are400_BeforeAnythingIsPriced()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();

        string?[] bad =
        [
            null, "", " ", "abc", ":5", $"{v}:0", $"{v}:-1", $"{v}:1.5", $"{v}:10001", $"{v}:", $"{v}:x", $"{v}:+3", $"{v},{v}", $"{v}:1,{v}:2",
            Guid.Empty.ToString(), v.ToString("N"), $"{{{v}}}", $"{v},", $"{v};{Guid.NewGuid()}", $"{v}:1:2",
        ];
        foreach (var items in bad)
            (await ErrorCodeAsync(await PricesAsync(buyer.Client, company, items), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", $"items={items}");

        (await PricesAsync(buyer.Client, company, $" {v} : 10000 ")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no blanks inside an item");
        Long(await OneAsync(buyer.Client, company, v, 10_000), "lineNetMinor").Should().Be(1_000_000_000);
        (await DataAsync(await PricesAsync(buyer.Client, company, v.ToString()))).GetProperty("items")[0].GetProperty("quantity").GetInt32()
            .Should().Be(1, "the quantity defaults to 1");
    }

    [PostgresFact]
    public async Task PriceQuery_WithMoreThanTheMaximumVariants_IsRefused()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        var hundred = Enumerable.Range(0, 99).Select(_ => (Guid.NewGuid(), (int?)1)).Prepend((v, 1)).ToArray();

        var ok = await DataAsync(await PricesAsync(buyer.Client, company, Items(hundred)));
        ok.GetProperty("items").GetArrayLength().Should().Be(100);

        var tooMany = hundred.Append((Guid.NewGuid(), (int?)1)).ToArray();
        (await ErrorCodeAsync(await PricesAsync(buyer.Client, company, Items(tooMany)), HttpStatusCode.BadRequest)).Should().Be("TOO_MANY_VARIANTS");
    }

    // ---- who may ask ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Idor_PricesOfAnotherCompany_Are404()
    {
        var a = await PersonAsync();
        var b = await PersonAsync();
        var companyA = await CompanyAsync((a.Id, CompanyRoles.Buyer));
        var companyB = await CompanyAsync((b.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await ContractAsync(companyB, new Entry(v, 1, 66_666), new Entry(v, 10, 55_555));

        Long(await OneAsync(b.Client, companyB, v), "unitPriceMinorNet").Should().Be(66_666, "B's buyer sees B's contract");
        Long(await OneAsync(a.Client, companyA, v), "unitPriceMinorNet").Should().Be(100_000, "A has no contract");

        var other = await PricesAsync(a.Client, companyB, Items((v, 10)));
        var otherText = await other.Content.ReadAsStringAsync();
        var unknown = await PricesAsync(a.Client, Guid.NewGuid(), Items((v, 10)));
        var otherBody = await NotFoundBodyAsync(other);
        otherBody.Should().Be(await NotFoundBodyAsync(unknown), "another company and no company answer alike");
        otherText.Should().NotContain("66666").And.NotContain("55555").And.NotContain("Price Buyer");

        using var anonymous = _factory.CreateClient();
        (await PricesAsync(anonymous, companyB, Items((v, 1)))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task OnlyABuyerOfAnActiveCompany_SeesPrices_EveryoneElseGetsTheSame404()
    {
        var buyer = await PersonAsync();
        var adminOnly = await PersonAsync();
        var approverAndRequester = await PersonAsync();
        var invited = await PersonAsync();
        var pendingBuyer = await PersonAsync();
        var seller = await PersonAsync(Permissions.Roles.Seller);
        var outsider = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer), (adminOnly.Id, CompanyRoles.Admin),
            (approverAndRequester.Id, CompanyRoles.Approver | CompanyRoles.Requester));
        var pending = await CompanyAsync(false, (pendingBuyer.Id, CompanyRoles.Buyer));
        await using (var db = Db())
        {
            var owner = await db.CompanyMembers.Where(m => m.CompanyId == company && (m.Roles & CompanyRoles.Admin) != 0 && m.UserId != adminOnly.Id)
                .Select(m => m.UserId).FirstAsync();
            await new CompanyService(db).AddMemberAsync(owner, company, new AddMemberRequest(invited.Id, CompanyRoles.Buyer, null, null));
        }

        var v = await VariantAsync();
        await ContractAsync(company, new Entry(v, 1, 66_666));
        await ContractAsync(pending, new Entry(v, 1, 66_666));
        var items = Items((v, 1));

        Long(await OneAsync(buyer.Client, company, v), "unitPriceMinorNet").Should().Be(66_666);
        var reference = await NotFoundBodyAsync(await PricesAsync(outsider.Client, company, items));
        foreach (var (who, ask, why) in new[]
                 {
                     (adminOnly, company, "a company admin without the Buyer role"),
                     (approverAndRequester, company, "approver and requester roles"),
                     (invited, company, "an invitation not accepted yet"),
                     (pendingBuyer, pending, "a buyer of a company MECHWARE has not verified"),
                     (seller, company, "a seller"),
                 })
            (await NotFoundBodyAsync(await PricesAsync(who.Client, ask, items))).Should().Be(reference, why);

        await using (var db = Db())
            await db.Companies.Where(c => c.Id == company).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CompanyStatus.Suspended));
        (await NotFoundBodyAsync(await PricesAsync(buyer.Client, company, items))).Should().Be(reference, "a suspended company's buyer");
    }

    [PostgresFact]
    public async Task AMemberOfTwoCompanies_GetsEachCompanysOwnPrice_NeverMerged()
    {
        var buyer = await PersonAsync();
        var companyA = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var companyB = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var shared = await VariantAsync();
        var onlyB = await VariantAsync();
        await ContractAsync(companyA, new Entry(shared, 1, 90_000));
        await ContractAsync(companyB, new Entry(shared, 1, 80_000), new Entry(onlyB, 1, 70_000), new Entry(onlyB, 5, 60_000));

        var forA = await PricedAsync(buyer.Client, companyA, (shared, 1), (onlyB, 5));
        var forB = await PricedAsync(buyer.Client, companyB, (shared, 1), (onlyB, 5));

        (Long(forA[shared], "unitPriceMinorNet"), Long(forA[onlyB], "unitPriceMinorNet"), Text(forA[onlyB], "source")).Should().Be((90_000L, 100_000L, "list"));
        Breaks(forA[onlyB]).Should().BeEmpty();
        (Long(forB[shared], "unitPriceMinorNet"), Long(forB[onlyB], "unitPriceMinorNet"), Text(forB[onlyB], "source")).Should().Be((80_000L, 60_000L, "contract"));
        Breaks(forB[onlyB]).Should().Equal((1, 70_000L), (5, 60_000L));
    }

    // ---- what the answer may carry ------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task CompanyPriceEndpoint_SendsPrivateNoStore()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        await ContractAsync(company, new Entry(v, 1, 90_000));

        foreach (var (response, status) in new[]
                 {
                     (await PricesAsync(buyer.Client, company, Items((v, 1))), HttpStatusCode.OK),
                     (await PricesAsync(buyer.Client, Guid.NewGuid(), Items((v, 1))), HttpStatusCode.NotFound),
                     (await PricesAsync(buyer.Client, company, "nonsense"), HttpStatusCode.BadRequest),
                 })
        {
            response.StatusCode.Should().Be(status);
            var cache = response.Headers.CacheControl;
            cache.Should().NotBeNull(status.ToString());
            (cache!.Private, cache.NoStore, cache.Public, cache.MaxAge).Should().Be((true, true, false, (TimeSpan?)null), status.ToString());
            response.Headers.Vary.Should().Contain("Authorization", status.ToString());
        }
    }

    [PostgresFact]
    public async Task BuyerPriceResponse_NeverContainsCostOrFloor()
    {
        var buyer = await PersonAsync();
        var company = await CompanyAsync((buyer.Id, CompanyRoles.Buyer));
        var v = await VariantAsync();
        const long cost = 61_234;   // floor at the seeded 10 %: ceil(61,234 × 1.1) = 67,358
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {cost} WHERE id = {v}");
            var list = await PriceListTestData.DraftAsync(db, PriceListScope.Company, company, _now.AddHours(-3), _now.AddDays(30), _now.AddHours(-2),
                new Entry(v, 1, 90_000), new Entry(v, 10, 88_000));
            MarginFloorEvaluation Floors() => MarginFloorPolicy.Evaluate(list, [new VariantCostFacts(v, cost, null, [], 0.05m)],
                [new MarginFloorRow(MarginFloorScope.Global, null, null, 10m)]);
            list.Submit(Guid.NewGuid(), Floors(), false, _now.AddHours(-2));
            await db.SaveChangesAsync();
            list.Approve(Guid.NewGuid(), false, list.ContentHash!, [], Floors(), false, _now.AddHours(-2));
            await db.SaveChangesAsync();
            (await db.PriceLists.AsNoTracking().Where(p => p.Id == list.Id).SelectMany(p => p.Entries).Select(e => e.CostMinorAtApproval).Distinct().ToListAsync())
                .Should().Equal(new long?[] { cost }, "the approval stored the cost on the entries (the data the answer must not carry)");
        }

        var response = await PricesAsync(buyer.Client, company, Items((v, 10)));
        var text = await response.Content.ReadAsStringAsync();
        var data = await DataAsync(response);

        Long(data.GetProperty("items")[0], "unitPriceMinorNet").Should().Be(88_000);
        var names = PropertyNames(data).Select(n => n.ToLowerInvariant()).Distinct().ToList();
        string[] forbidden = ["cost", "floor", "margin", "markup", "belowfloor", "version", "entryid", "approved", "submitted", "createdby", "companyid_of"];
        names.Where(n => forbidden.Any(f => n.Contains(f))).Should().BeEmpty();
        names.Should().BeSubsetOf(["companyid", "asofutc", "items", "variantid", "status", "quantity", "unitpriceminornet", "publicunitpriceminornet",
            "guestunitpriceminorinclvat", "source", "pricelistid", "contractref", "validtoutc", "taxmode", "vatrate", "linenetminor", "linetaxminor",
            "linetotalminor", "breaks", "minqty", "quantityproblem"]);
        text.Should().NotContain(cost.ToString()).And.NotContain("67358").And.NotContain("612.34").And.NotContain("673.58");
    }

    // ---- the public side stays public (PL17) ------------------------------------------------------------------------

    private const long PublicGross = 9_876_500;   // 94,061.90 net at 5 %
    private const long ContractNet = 8_765_432;
    private static readonly string[] ContractNumbers = ["8765432", "87654.32", "9203704", "92037.04", "8123456", "81234.56"];

    private sealed record PublicSetup(Guid ProductId, Guid VariantId, string Slug, string CategorySlug, string Sku, Person Member, Guid Company);

    /// <summary>A product with a contract price for one company, indexed for search, and that company's buyer having asked for it.</summary>
    private async Task<PublicSetup> PublicSetupAsync()
    {
        var member = await PersonAsync();
        var company = await CompanyAsync((member.Id, CompanyRoles.Buyer));
        var seeded = await SeededAsync(PublicGross);
        await ContractAsync(company, new Entry(seeded.VariantId, 1, ContractNet), new Entry(seeded.VariantId, 10, 8_123_456));
        string slug, categorySlug, sku;
        await using (var db = Db())
        {
            var p = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.ProductId);
            slug = p.Slug;
            categorySlug = await db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Slug).SingleAsync();
            sku = await db.ProductVariants.Where(x => x.Id == seeded.VariantId).Select(x => x.Sku).SingleAsync();
            var provider = _factory.Services.GetRequiredService<ISearchProvider>();
            await provider.EnsureIndexAsync();
            await provider.IndexProductsAsync(await new ProductSearchDocumentLoader(db, TimeProvider.System).LoadAsync([seeded.ProductId]));
        }

        var shown = await OneAsync(member.Client, company, seeded.VariantId, 10);
        (Long(shown, "unitPriceMinorNet"), Text(shown, "source")).Should().Be((8_123_456L, "contract"), "the company endpoint ran and found the contract");
        return new PublicSetup(seeded.ProductId, seeded.VariantId, slug, categorySlug, sku, member, company);
    }

    private static string[] PublicUrls(PublicSetup s) =>
    [
        $"/api/v1/storefront/products/{s.Slug}",
        $"/api/v1/storefront/categories/{s.CategorySlug}/products",
        $"/api/v1/search?q={Uri.EscapeDataString(s.Sku)}",
    ];

    /// <summary>The body as sent, except the search engine's own timing (<c>meta.processingMs</c>), which differs per request.</summary>
    private static async Task<string> PublicBodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, url + " " + text);
        response.Headers.CacheControl!.Public.Should().BeTrue(url);
        if (!url.StartsWith("/api/v1/search", StringComparison.Ordinal)) return text;
        var node = JsonNode.Parse(text)!;
        node["meta"]!.AsObject().Remove("processingMs");
        return node.ToJsonString();
    }

    [PostgresRedisAndMeiliFact]
    public async Task PublicProductResponse_IsTheSameForGuestAndCompanyMember()
    {
        var s = await PublicSetupAsync();
        using var guest = _factory.CreateClient();

        foreach (var url in PublicUrls(s))
        {
            var asGuest = await PublicBodyAsync(guest, url);
            var asMember = await PublicBodyAsync(s.Member.Client, url);
            var asGuestAgain = await PublicBodyAsync(guest, url);
            asMember.Should().Be(asGuest, url);
            asGuestAgain.Should().Be(asGuest, url);
            asGuest.Should().Contain(s.Slug, url);
            foreach (var n in ContractNumbers) asMember.Should().NotContain(n, url);
        }
    }

    [PostgresRedisAndMeiliFact]
    public async Task StorefrontCache_HoldsNoCompanyPrices()
    {
        var s = await PublicSetupAsync();
        foreach (var url in PublicUrls(s)) await PublicBodyAsync(s.Member.Client, url);   // the member fills the public caches
        (await PricesAsync(s.Member.Client, s.Company, Items((s.VariantId, 1)))).StatusCode.Should().Be(HttpStatusCode.OK);

        // Every sf:* key in Redis, whatever its type.
        using var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        var db = redis.GetDatabase();
        var values = new Dictionary<string, string>();
        foreach (var endpoint in redis.GetEndPoints())
            foreach (var key in redis.GetServer(endpoint).Keys(pattern: "sf:*"))
            {
                var type = await db.KeyTypeAsync(key);
                values[key!] = type switch
                {
                    RedisType.String => (string?)await db.StringGetAsync(key) ?? "",
                    RedisType.Hash => string.Join("|", (await db.HashGetAllAsync(key)).Select(e => $"{e.Name}={e.Value}")),
                    RedisType.Set => string.Join("|", (await db.SetMembersAsync(key)).Select(m => m.ToString())),
                    RedisType.SortedSet => string.Join("|", (await db.SortedSetRangeByRankWithScoresAsync(key)).Select(e => $"{e.Element}:{e.Score}")),
                    RedisType.List => string.Join("|", (await db.ListRangeAsync(key)).Select(m => m.ToString())),
                    _ => "",
                };
            }

        values.Keys.Should().Contain($"sf:product:v2:{s.Slug}", "the product page was cached, so the scan saw it");
        values[$"sf:product:v2:{s.Slug}"].Should().Contain(PublicGross.ToString(), "the cached page holds the public price");
        foreach (var (key, value) in values)
            foreach (var n in ContractNumbers)
                (key + " " + value).Should().NotContain(n, key);

        // The search document as the index holds it, and as the indexer would build it now.
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        var indexed = await http.GetAsync($"indexes/{_index}/documents/{s.ProductId}");
        var indexedJson = await indexed.Content.ReadAsStringAsync();
        indexed.StatusCode.Should().Be(HttpStatusCode.OK, indexedJson);
        indexedJson.Should().Contain(s.Slug).And.Contain(PublicGross.ToString());
        await using var ctx = Db();
        var rebuilt = JsonSerializer.Serialize(await new ProductSearchDocumentLoader(ctx, TimeProvider.System).LoadAsync([s.ProductId]));
        foreach (var n in ContractNumbers)
        {
            indexedJson.Should().NotContain(n);
            rebuilt.Should().NotContain(n);
        }
    }
}
