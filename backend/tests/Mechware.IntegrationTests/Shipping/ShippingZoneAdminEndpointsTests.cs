using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Orders;
using Mechware.Application.Shipping;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Shipping;

/// <summary>
/// E8.4a: the staff API for shipping zones. Own database per class, so the zones made here never reach other tests. Support and
/// Finance may view (and preview), only Admin manages; the data entered here is what real order pricing reads.
/// </summary>
public sealed class ShippingZoneAdminEndpointsTests : IDisposable
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "ShipStaff#2026!";
    private const string Zones = "/api/v1/admin/shipping/zones";
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => Db(_factory.ConnectionString);

    private static MechwareDbContext Db(string connection) => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(connection).Options);

    private static Task<HttpClient> StaffAsync(DbBackedFactory factory, string role) => StaffAsync(factory, factory.ConnectionString, role);

    private static async Task<HttpClient> StaffAsync(DbBackedFactory factory, string connection, string role)
    {
        factory.CreateClient().Dispose();
        var email = $"ship.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        await using (var db = Db(connection))
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Shipping Staff", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    private Task<HttpClient> StaffAsync(string role) => StaffAsync(_factory, role);

    private async Task<HttpClient> BuyerAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<(Guid Id, string NameEn)[]> DistrictsAsync(int n, string? connection = null)
    {
        await using var db = Db(connection ?? _factory.ConnectionString);
        var list = Enumerable.Range(0, n).Select(_ => { var id = Guid.NewGuid(); return (Id: id, NameEn: "Dist " + id.ToString("N")[..8]); }).ToArray();
        foreach (var d in list) db.BdLocations.Add(new BdLocation(d.Id, null, "district", d.NameEn, "জেলা " + d.NameEn, "outside_dhaka"));
        await db.SaveChangesAsync();
        return list;
    }

    private static string NewCode() => "z" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Test variants weigh 0.5 kg. Up to 1 kg: 60.00; up to 5 kg: 120.00; free from 500.00; a unit over 8 kg or a side over 1 m is quote-only.</summary>
    private static object Body(string code, IEnumerable<Guid> districts, bool active = true, long? expectedVersion = null,
        object[]? bands = null, long maxFee = 50_000, string nameEn = "Zone One") => new
        {
            code,
            nameEn,
            nameBn = "জোন এক",
            isActive = active,
            districtIds = districts,
            expectedVersion,
            rates = new
            {
                bands = bands ?? [new { maxGrams = 1_000, feeMinor = 6_000 }, new { maxGrams = 5_000, feeMinor = 12_000 }],
                freeShippingThresholdMinor = 50_000L,
                heavyUnitGrams = 8_000,
                oversizeDimensionMm = 1_000,
                maxFeeMinor = maxFee,
            },
        };

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, raw);
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, raw);
        return JsonDocument.Parse(raw).RootElement.GetProperty("error").Clone();
    }

    private async Task<JsonElement> CreateAsync(HttpClient admin, string code, IEnumerable<Guid> districts, bool active = true)
    {
        using var r = await admin.PostAsJsonAsync(Zones, Body(code, districts, active));
        return await DataAsync(r, HttpStatusCode.Created);
    }

    // ---- permissions ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task EveryEndpoint_Anonymous401_Buyer403_SupportAndFinanceCanViewButNotManage_AdminDoesAll()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(3);
        var zone = await CreateAsync(admin, NewCode(), [d[0].Id]);
        var id = zone.GetProperty("id").GetGuid();
        var version = zone.GetProperty("version").GetInt64();

        using var anonymous = _factory.CreateClient();
        using var buyer = await BuyerAsync();
        using var support = await StaffAsync(Permissions.Roles.Support);
        using var finance = await StaffAsync(Permissions.Roles.Finance);
        var preview = new { districtId = d[0].Id, subtotalMinor = 1, lines = new[] { new { quantity = 1, unitWeightGrams = 100 } } };

        async Task<HttpStatusCode> Call(HttpClient c, string verb, string url, object? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(verb), url) { Content = body is null ? null : JsonContent.Create(body) };
            using var response = await c.SendAsync(request);
            return response.StatusCode;
        }

        var viewCalls = new (string Verb, string Url, object? Body)[]
        {
            ("GET", Zones, null), ("GET", $"{Zones}/{id}", null), ("GET", "/api/v1/admin/shipping/coverage", null), ("POST", $"{Zones}/preview", preview),
        };
        foreach (var (verb, url, body) in viewCalls)
        {
            (await Call(anonymous, verb, url, body)).Should().Be(HttpStatusCode.Unauthorized, $"{verb} {url}");
            (await Call(buyer, verb, url, body)).Should().Be(HttpStatusCode.Forbidden, $"{verb} {url}");
            (await Call(support, verb, url, body)).Should().Be(HttpStatusCode.OK, $"{verb} {url} support");
            (await Call(finance, verb, url, body)).Should().Be(HttpStatusCode.OK, $"{verb} {url} finance");
            (await Call(admin, verb, url, body)).Should().Be(HttpStatusCode.OK, $"{verb} {url} admin");
        }

        var manageCalls = new (string Verb, string Url, object Body)[]
        {
            ("POST", Zones, Body(NewCode(), [d[1].Id])), ("PUT", $"{Zones}/{id}", Body(zone.GetProperty("code").GetString()!, [d[0].Id], expectedVersion: version)),
            ("DELETE", $"{Zones}/{id}", new { }),
        };
        foreach (var (verb, url, body) in manageCalls)
        {
            (await Call(anonymous, verb, url, body)).Should().Be(HttpStatusCode.Unauthorized, $"{verb} {url}");
            foreach (var c in new[] { buyer, support, finance }) (await Call(c, verb, url, body)).Should().Be(HttpStatusCode.Forbidden, $"{verb} {url}");
        }

        await using var db = Db();
        (await db.ShippingZones.CountAsync()).Should().Be(1, "the refused calls created and deleted nothing");
        (await Call(admin, "POST", Zones, manageCalls[0].Body)).Should().Be(HttpStatusCode.Created);
    }

    // ---- create / read ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Create_ThenRead_RoundTripsRatesAndDistrictNames_AndIgnoresClientOwnedFields()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(2);
        var code = NewCode();
        var clientId = Guid.NewGuid();
        var node = JsonSerializer.SerializeToNode(Body(code.ToUpperInvariant(), [d[0].Id, d[1].Id]))!.AsObject();
        node["id"] = clientId;
        node["version"] = 5;
        node["createdAtUtc"] = "2001-01-01T00:00:00Z";
        node["updatedAtUtc"] = "2001-01-01T00:00:00Z";
        node["createdBy"] = clientId;
        var body = node.ToJsonString();
        using var created = await admin.PostAsync(Zones, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var data = await DataAsync(created, HttpStatusCode.Created);

        var id = data.GetProperty("id").GetGuid();
        id.Should().NotBe(clientId, "the server owns the id");
        data.GetProperty("createdAtUtc").GetDateTime().Year.Should().BeGreaterThan(2001);
        data.GetProperty("code").GetString().Should().Be(code, "codes are stored lower-case");
        created.Headers.Location!.ToString().Should().EndWith(id.ToString());

        using var read = await admin.GetAsync($"{Zones}/{id}");
        var zone = await DataAsync(read, HttpStatusCode.OK);
        zone.GetProperty("nameEn").GetString().Should().Be("Zone One");
        zone.GetProperty("isActive").GetBoolean().Should().BeTrue();
        zone.GetProperty("districts").EnumerateArray().Select(x => (x.GetProperty("id").GetGuid(), x.GetProperty("nameEn").GetString()))
            .Should().BeEquivalentTo(d.Select(x => (x.Id, (string?)x.NameEn)));
        var rates = zone.GetProperty("rates");
        rates.GetProperty("bands").EnumerateArray().Select(b => (b.GetProperty("maxGrams").GetInt32(), b.GetProperty("feeMinor").GetInt64()))
            .Should().Equal((1_000, 6_000L), (5_000, 12_000L));
        (rates.GetProperty("freeShippingThresholdMinor").GetInt64(), rates.GetProperty("heavyUnitGrams").GetInt32(),
            rates.GetProperty("oversizeDimensionMm").GetInt32(), rates.GetProperty("maxFeeMinor").GetInt64()).Should().Be((50_000L, 8_000, 1_000, 50_000L));

        var list = await DataAsync(await admin.GetAsync(Zones), HttpStatusCode.OK);
        var row = list.EnumerateArray().Single(z => z.GetProperty("id").GetGuid() == id);
        (row.GetProperty("districtCount").GetInt32(), row.GetProperty("bandCount").GetInt32()).Should().Be((2, 2));

        using var duplicate = await admin.PostAsJsonAsync(Zones, Body(code, [d[0].Id]));
        (await ErrorAsync(duplicate, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("SHIPPING_ZONE_CODE_TAKEN");
        using var missing = await admin.GetAsync($"{Zones}/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task ADistrictOwnedByAnotherZone_IsA409NamingTheDistrictAndTheZone_ActiveOrNot_OnCreateAndUpdate()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(3);
        var ownerCode = NewCode();
        await CreateAsync(admin, ownerCode, [d[0].Id], active: true);
        var parkedCode = NewCode();
        await CreateAsync(admin, parkedCode, [d[1].Id], active: false);

        using var onCreate = await admin.PostAsJsonAsync(Zones, Body(NewCode(), [d[2].Id, d[0].Id]));
        var error = await ErrorAsync(onCreate, HttpStatusCode.Conflict);
        error.GetProperty("code").GetString().Should().Be("SHIPPING_DISTRICT_ALREADY_IN_ZONE");
        error.GetProperty("message").GetString().Should().Contain(d[0].NameEn).And.Contain(ownerCode).And.Contain("active");

        var third = await CreateAsync(admin, NewCode(), [d[2].Id]);
        using var onUpdate = await admin.PutAsJsonAsync($"{Zones}/{third.GetProperty("id").GetGuid()}",
            Body(third.GetProperty("code").GetString()!, [d[2].Id, d[1].Id], expectedVersion: third.GetProperty("version").GetInt64()));
        var parked = await ErrorAsync(onUpdate, HttpStatusCode.Conflict);
        parked.GetProperty("message").GetString().Should().Contain(d[1].NameEn).And.Contain(parkedCode).And.Contain("inactive");

        await using var db = Db();
        (await db.ShippingZones.Where(z => z.Id == third.GetProperty("id").GetGuid()).Select(z => z.Districts.Count).SingleAsync()).Should().Be(1);
    }

    [PostgresFact]
    public async Task InvalidTariffs_AreRefusedWith400AndFieldErrors_AndNothingIsStored()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(1);
        var ghost = Guid.NewGuid();

        async Task<string[]> FieldsAsync(object body)
        {
            using var r = await admin.PostAsJsonAsync(Zones, body);
            var error = await ErrorAsync(r, HttpStatusCode.BadRequest);
            return error.GetProperty("details").EnumerateArray().Select(x => x.GetProperty("field").GetString()!).ToArray();
        }

        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: [new { maxGrams = 2_000_000, feeMinor = 100 }]))).Should().Contain("rates.bands[0].maxGrams");
        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: [new { maxGrams = 1_000, feeMinor = 20_000_000 }], maxFee: 20_000_000))).Should().Contain("rates.bands[0].feeMinor").And.Contain("rates.maxFeeMinor");
        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: [new { maxGrams = 0, feeMinor = -1 }]))).Should().Contain("rates.bands[0].maxGrams").And.Contain("rates.bands[0].feeMinor");
        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: []))).Should().Contain("rates.bands");
        (await FieldsAsync(Body("Bad Code!", [d[0].Id]))).Should().Contain("code");
        (await FieldsAsync(Body(NewCode(), [d[0].Id], nameEn: " "))).Should().Contain("nameEn");
        (await FieldsAsync(Body(NewCode(), [ghost]))).Should().Contain("districtIds");
        (await FieldsAsync(Body(NewCode(), [d[0].Id, d[0].Id]))).Should().Contain("districtIds");

        // The domain's own band rules (not the sanity limits): same weight twice, a heavier band that costs less, a fee above the cap.
        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: [new { maxGrams = 1_000, feeMinor = 100 }, new { maxGrams = 1_000, feeMinor = 200 }]))).Should().Contain("bands");
        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: [new { maxGrams = 1_000, feeMinor = 500 }, new { maxGrams = 2_000, feeMinor = 400 }]))).Should().Contain("bands");
        (await FieldsAsync(Body(NewCode(), [d[0].Id], bands: [new { maxGrams = 1_000, feeMinor = 60_000 }]))).Should().Contain("bands");

        using var noRates = await admin.PostAsJsonAsync(Zones, new { code = NewCode(), nameEn = "x", nameBn = "y", isActive = true, districtIds = new[] { d[0].Id } });
        (await ErrorAsync(noRates, HttpStatusCode.BadRequest)).GetProperty("details").EnumerateArray().Select(x => x.GetProperty("field").GetString()).Should().Contain("rates");

        await using var db = Db();
        (await db.ShippingZones.CountAsync()).Should().Be(0);
    }

    // ---- update / delete --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Update_ReplacesTheWholeDocument_AStaleVersionIs409_AndTheCodeCannotChange()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(3);
        var created = await CreateAsync(admin, NewCode(), [d[0].Id, d[1].Id]);
        var id = created.GetProperty("id").GetGuid();
        var code = created.GetProperty("code").GetString()!;
        var v1 = created.GetProperty("version").GetInt64();

        var replacement = Body(code, [d[1].Id, d[2].Id], active: false, expectedVersion: v1, nameEn: "Renamed",
            bands: [new { maxGrams = 1_000, feeMinor = 7_000 }, new { maxGrams = 3_000, feeMinor = 9_000 }]);
        using var ok = await admin.PutAsJsonAsync($"{Zones}/{id}", replacement);
        var updated = await DataAsync(ok, HttpStatusCode.OK);
        var v2 = updated.GetProperty("version").GetInt64();
        v2.Should().NotBe(v1);
        updated.GetProperty("nameEn").GetString().Should().Be("Renamed");
        updated.GetProperty("isActive").GetBoolean().Should().BeFalse();
        updated.GetProperty("districts").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Should().BeEquivalentTo([d[1].Id, d[2].Id]);
        updated.GetProperty("rates").GetProperty("bands").EnumerateArray().Select(b => (b.GetProperty("maxGrams").GetInt32(), b.GetProperty("feeMinor").GetInt64()))
            .Should().Equal((1_000, 7_000L), (3_000, 9_000L));
        (await DataAsync(await admin.GetAsync($"{Zones}/{id}"), HttpStatusCode.OK)).GetProperty("version").GetInt64().Should().Be(v2);

        using var stale = await admin.PutAsJsonAsync($"{Zones}/{id}", Body(code, [d[1].Id], expectedVersion: v1, nameEn: "Lost update"));
        (await ErrorAsync(stale, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("SHIPPING_ZONE_STALE");
        (await DataAsync(await admin.GetAsync($"{Zones}/{id}"), HttpStatusCode.OK)).GetProperty("nameEn").GetString().Should().Be("Renamed");

        using var noVersion = await admin.PutAsJsonAsync($"{Zones}/{id}", Body(code, [d[1].Id], expectedVersion: null));
        (await ErrorAsync(noVersion, HttpStatusCode.BadRequest)).GetProperty("details")[0].GetProperty("field").GetString().Should().Be("expectedVersion");
        using var otherCode = await admin.PutAsJsonAsync($"{Zones}/{id}", Body(NewCode(), [d[1].Id], expectedVersion: v2));
        (await ErrorAsync(otherCode, HttpStatusCode.BadRequest)).GetProperty("details")[0].GetProperty("field").GetString().Should().Be("code");
        using var unknown = await admin.PutAsJsonAsync($"{Zones}/{Guid.NewGuid()}", Body(code, [d[1].Id], expectedVersion: v2));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The omitted code is fine; a district dropped from the zone is free for another zone again.
        var withoutCode = new { nameEn = "Renamed", nameBn = "নাম", isActive = false, districtIds = new[] { d[2].Id }, expectedVersion = v2, rates = new { bands = new[] { new { maxGrams = 1_000, feeMinor = 100 } }, maxFeeMinor = 1_000L } };
        using var again = await admin.PutAsJsonAsync($"{Zones}/{id}", withoutCode);
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        await CreateAsync(admin, NewCode(), [d[1].Id]);
    }

    [PostgresFact]
    public async Task Delete_IsRefusedWhileActive_AllowedWhenInactive_AndEveryChangeWritesAnAuditRow()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(1);
        var created = await CreateAsync(admin, NewCode(), [d[0].Id]);
        var id = created.GetProperty("id").GetGuid();
        var code = created.GetProperty("code").GetString()!;

        using var refused = await admin.DeleteAsync($"{Zones}/{id}");
        (await ErrorAsync(refused, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("SHIPPING_ZONE_ACTIVE");

        using var off = await admin.PutAsJsonAsync($"{Zones}/{id}", Body(code, [d[0].Id], active: false, expectedVersion: created.GetProperty("version").GetInt64(),
            bands: [new { maxGrams = 1_000, feeMinor = 6_500 }]));
        off.StatusCode.Should().Be(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());
        using var gone = await admin.DeleteAsync($"{Zones}/{id}");
        gone.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"{Zones}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"{Zones}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = Db();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM shipping_zone_districts WHERE zone_id = {id}").SingleAsync()).Should().Be(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM shipping_rate_bands WHERE zone_id = {id}").SingleAsync()).Should().Be(0);
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.EntityName == "shipping_zones" && a.EntityId == id.ToString() && a.Action.StartsWith("shipping.zone.")).ToListAsync();
        rows.Select(r => r.Action).Should().BeEquivalentTo("shipping.zone.created", "shipping.zone.updated", "shipping.zone.deleted");
        rows.Should().OnlyContain(r => r.ActorId != null);
        var updated = rows.Single(r => r.Action == "shipping.zone.updated");
        updated.OldValuesJson!.Replace(" ", "").Should().Contain("6000").And.Contain("\"IsActive\":true");
        updated.NewValuesJson!.Replace(" ", "").Should().Contain("6500").And.Contain("\"IsActive\":false");
        rows.Single(r => r.Action == "shipping.zone.deleted").OldValuesJson.Should().Contain(code);

        // The district is free again.
        await CreateAsync(admin, NewCode(), [d[0].Id]);
    }

    // ---- preview and coverage --------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Preview_PricesQuotesAndRefusesLikeTheCalculator_AgainstActiveZonesOnly_AndWritesNothing()
    {
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        using var support = await StaffAsync(Permissions.Roles.Support);
        var d = await DistrictsAsync(3);
        var zone = await CreateAsync(admin, NewCode(), [d[0].Id]);
        await CreateAsync(admin, NewCode(), [d[1].Id], active: false);
        var (sellerA, sellerB) = (Guid.NewGuid(), Guid.NewGuid());

        await using var before = Db();
        var zonesBefore = await before.ShippingZones.CountAsync();
        var auditBefore = await before.AuditLogs.CountAsync(a => a.EntityName == "shipping_zones");

        async Task<JsonElement> Preview(object body, HttpStatusCode status = HttpStatusCode.OK)
        {
            using var r = await support.PostAsJsonAsync($"{Zones}/preview", body);
            return status == HttpStatusCode.OK ? await DataAsync(r, status) : await ErrorAsync(r, status);
        }

        var priced = await Preview(new { districtId = d[0].Id, subtotalMinor = 10_000, lines = new[] { new { sellerId = sellerA, quantity = 2, unitWeightGrams = 500 }, new { sellerId = sellerB, quantity = 1, unitWeightGrams = 2_000 } } });
        priced.GetProperty("outcome").GetString().Should().Be("Priced");
        priced.GetProperty("zoneId").GetGuid().Should().Be(zone.GetProperty("id").GetGuid());
        priced.GetProperty("totalMinor").GetInt64().Should().Be(6_000 + 12_000);
        priced.GetProperty("perSeller").EnumerateArray().Select(s => (s.GetProperty("sellerId").GetGuid(), s.GetProperty("shippingMinor").GetInt64(), s.GetProperty("chargeableGrams").GetInt64()))
            .Should().Equal((sellerA, 6_000L, 1_000L), (sellerB, 12_000L, 2_000L));

        var free = await Preview(new { districtId = d[0].Id, subtotalMinor = 50_000, lines = new[] { new { quantity = 1, unitWeightGrams = 500 } } });
        (free.GetProperty("outcome").GetString(), free.GetProperty("totalMinor").GetInt64(), free.GetProperty("freeShippingApplied").GetBoolean()).Should().Be(("Priced", 0L, true));

        var heavy = await Preview(new { districtId = d[0].Id, subtotalMinor = 0, lines = new object[] { new { quantity = 1, unitWeightGrams = 100 }, new { quantity = 1, unitWeightGrams = 9_000 } } });
        heavy.GetProperty("outcome").GetString().Should().Be("QuoteOnly");
        heavy.GetProperty("reasons").EnumerateArray().Select(r => (r.GetProperty("code").GetString(), r.GetProperty("lineIndex").ValueKind == JsonValueKind.Null ? -1 : r.GetProperty("lineIndex").GetInt32()))
            .Should().Contain(("HEAVY_ITEM", 1));
        var over = await Preview(new { districtId = d[0].Id, subtotalMinor = 0, lines = new[] { new { quantity = 1, unitWeightGrams = 100, lengthMm = 1_200 } } });
        over.GetProperty("reasons")[0].GetProperty("code").GetString().Should().Be("OVERSIZE_ITEM");
        var beyond = await Preview(new { districtId = d[0].Id, subtotalMinor = 0, lines = new[] { new { sellerId = sellerA, quantity = 20, unitWeightGrams = 500 } } });
        beyond.GetProperty("reasons")[0].GetProperty("code").GetString().Should().Be("ABOVE_LAST_BAND");

        var inactive = await Preview(new { districtId = d[1].Id, subtotalMinor = 0, lines = new[] { new { quantity = 1, unitWeightGrams = 100 } } });
        inactive.GetProperty("outcome").GetString().Should().Be("NoZone");
        (await Preview(new { districtId = d[2].Id, subtotalMinor = 0, lines = new[] { new { quantity = 1, unitWeightGrams = 100 } } })).GetProperty("outcome").GetString().Should().Be("NoZone");

        (await Preview(new { districtId = d[0].Id, subtotalMinor = 0, lines = new[] { new { quantity = 1, unitWeightGrams = 0 } } }, HttpStatusCode.BadRequest))
            .GetProperty("details")[0].GetProperty("field").GetString().Should().Be("lines[0].unitWeightGrams");
        await Preview(new { districtId = d[0].Id, subtotalMinor = 0, lines = Array.Empty<object>() }, HttpStatusCode.BadRequest);
        await Preview(new { districtId = d[0].Id, subtotalMinor = -1, lines = new[] { new { quantity = 1, unitWeightGrams = 5 } } }, HttpStatusCode.BadRequest);

        await using var after = Db();
        (await after.ShippingZones.CountAsync()).Should().Be(zonesBefore);
        (await after.AuditLogs.CountAsync(a => a.EntityName == "shipping_zones")).Should().Be(auditBefore, "a preview writes nothing, not even an audit row");
    }

    [PostgresFact]
    public async Task Coverage_ListsUncoveredDistricts_AndUpdatesAfterAZoneIsCreated()
    {
        using var finance = await StaffAsync(Permissions.Roles.Finance);
        using var admin = await StaffAsync(Permissions.Roles.Admin);
        var d = await DistrictsAsync(2);

        var first = await DataAsync(await finance.GetAsync("/api/v1/admin/shipping/coverage"), HttpStatusCode.OK);
        Func<JsonElement, List<Guid>> ids = list => list.EnumerateArray().Select(x => x.GetProperty("districtId").GetGuid()).ToList();
        ids(first.GetProperty("uncovered")).Should().Contain([d[0].Id, d[1].Id]);
        ids(first.GetProperty("covered")).Should().NotContain(d[0].Id);
        var uncoveredBefore = first.GetProperty("uncovered").GetArrayLength();

        var zone = await CreateAsync(admin, NewCode(), [d[0].Id]);
        await CreateAsync(admin, NewCode(), [d[1].Id], active: false);

        var second = await DataAsync(await finance.GetAsync("/api/v1/admin/shipping/coverage"), HttpStatusCode.OK);
        ids(second.GetProperty("covered")).Should().Contain(d[0].Id);
        second.GetProperty("covered").EnumerateArray().Single(x => x.GetProperty("districtId").GetGuid() == d[0].Id).GetProperty("zoneCode").GetString()
            .Should().Be(zone.GetProperty("code").GetString());
        ids(second.GetProperty("uncovered")).Should().Contain(d[1].Id, "an inactive zone does not cover a district")
            .And.NotContain(d[0].Id);
        var parked = second.GetProperty("uncovered").EnumerateArray().Single(x => x.GetProperty("districtId").GetGuid() == d[1].Id);
        parked.GetProperty("zoneCode").GetString().Should().NotBeNullOrEmpty();
        parked.GetProperty("zoneActive").GetBoolean().Should().BeFalse();
        second.GetProperty("uncovered").GetArrayLength().Should().Be(uncoveredBefore - 1);
        second.GetProperty("activeZoneCount").GetInt32().Should().Be(1);
    }

    // ---- the admin data is what orders are priced from -------------------------------------------------------------------

    [PostgresFact]
    public async Task AZoneCreatedThroughTheApi_IsWhatAProductionHostPricesOrdersFrom()
    {
        await using var factory = new DbBackedFactory("Production", OrderShippingStartupTests.ProductionSettings());
        using var admin = await StaffAsync(factory, Permissions.Roles.Admin);
        var d = await DistrictsAsync(1, factory.ConnectionString);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MechwareDbContext>();
        var buyer = User.Create($"buyer.{Guid.NewGuid():N}@example.test", null, null, "Buyer");
        buyer.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(buyer);
        var address = ShippingTestKit.AddressIn(buyer.Id, d[0].NameEn, isDefault: true);
        db.Addresses.Add(address);
        await db.SaveChangesAsync();
        var v = await CatalogTestData.SeedVariantAsync(db, 10, priceMinor: 10_000);
        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var lines = new List<PlaceOrderLine> { new(v.VariantId, 2) };   // 2 x 0.5 kg = 1 kg

        var refused = () => orders.QuoteShippingAsync(new ShippingQuoteRequest(buyer.Id, lines, address.Id));
        (await refused.Should().ThrowAsync<UnprocessableException>()).Which.Code.Should().Be(IOrderShippingQuoter.NotAvailable, "no zone yet: production fails closed");

        using var made = await admin.PostAsJsonAsync(Zones, Body(NewCode(), [d[0].Id]));
        made.StatusCode.Should().Be(HttpStatusCode.Created);

        var quote = await orders.QuoteShippingAsync(new ShippingQuoteRequest(buyer.Id, lines, address.Id));
        quote.TotalMinor.Should().Be(6_000);
        var placed = await orders.PlaceAsync(new PlaceOrderRequest(buyer.Id, "key-" + Guid.NewGuid().ToString("N"), lines, address.Id));
        placed.GrandTotalMinor.Should().Be(20_000 + 6_000, "the goods plus the fee the admin entered, once");
    }
}
