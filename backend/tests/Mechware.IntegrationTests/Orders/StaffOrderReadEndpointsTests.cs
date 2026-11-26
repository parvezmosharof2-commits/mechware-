using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Payments;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// G5.5 over HTTP: <c>GET /api/v1/admin/orders</c> (filters, paging, Dhaka dates, newest first) and <c>GET /api/v1/admin/orders/{id}</c>
/// (cart order and quote order contents), both behind <c>orders.view_all</c>, <c>private, no-store</c>, with a whitelist of JSON fields
/// so no provider reference, payment note or secret can leave. Own host (QuoteIssueFactory) so sign-ins have their own budget.
/// </summary>
public sealed class StaffOrderReadEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private readonly QuoteIssueKit _kit = new(factory);

    private static readonly HashSet<string> ListFields =
    [
        "id", "orderNumber", "status", "companyId", "companyName", "grandTotalMinor", "paymentState", "placedAtUtc", "legalDeliveryDeadlineUtc",
        "courierHandoverDeadlineUtc", "deliveryOverdue", "handoverOverdue", "source", "quoteNumber"
    ];

    private static readonly HashSet<string> DetailFields =
    [
        "id", "orderNumber", "status", "currency", "taxMode", "placedAtUtc", "confirmedAtUtc", "paymentDueAtUtc", "legalDeliveryDeadlineUtc",
        "courierHandoverDeadlineUtc", "deadlineSameCity", "purchaseOrderNumber", "subtotalMinor", "discountMinor", "taxMinor", "shippingMinor",
        "grandTotalMinor", "source", "quote", "quoteId", "number", "versionId", "companyId", "companyName", "buyer", "userId", "name", "phone",
        "shipTo", "recipientName", "line1", "line2", "upazila", "district", "division", "postalCode", "siteName", "paymentState", "paysOnCredit",
        "credit", "dueOn", "chargedMinor", "outstandingMinor", "subOrders", "subOrderNumber", "sellerId", "storeName", "shippingMinor", "totalMinor",
        "lines", "variantId", "sku", "title", "quantity", "unitPriceMinor", "vatRate", "grossMinor", "netMinor", "priceSnapshot", "source", "basis",
        "priceListId", "priceListVersion", "contractRef", "publicUnitPriceMinor", "quoteLineId", "events", "sequence", "fromStatus", "toStatus",
        "actorId", "reason", "atUtc", "payments", "method", "currency", "createdAtUtc", "capturedAtUtc", "refunds", "amountMinor", "requestedBy",
        "decidedBy", "decidedAtUtc",
        // F1.1: the version the fulfilment moves send back, and the shipments (staff id of the shipper as an id only).
        "rowVersion", "shipments", "courierName", "trackingNumber", "trackingUrl", "shippedAtUtc", "deliveredAtUtc", "createdBy", "invoiceNumbers"
    ];

    private const string Phone = "+8801711555123";

    private static HttpClient Anonymous(QuoteIssueFactory f) => f.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static void NoStore(HttpResponseMessage r)
    {
        r.Headers.CacheControl!.NoStore.Should().BeTrue();
        r.Headers.CacheControl.Private.Should().BeTrue();
    }

    private static void Names(JsonElement e, ISet<string> into)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) { into.Add(p.Name); Names(p.Value, into); }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) Names(x, into);
    }

    /// <summary>A bare order row (no lines) in an exact state; the number is read back.</summary>
    private async Task<(Guid Id, string Number)> BareAsync(Guid user, Guid? company, string status, DateTime placedUtc, bool onCredit = false)
    {
        await using var db = _kit.Db();
        var id = await CreditTestData.InsertOrderAsync(db, user, company, 12_345, onCredit, status, placedAtUtc: placedUtc);
        return (id, await db.Orders.Where(o => o.Id == id).Select(o => o.OrderNumber).SingleAsync());
    }

    private async Task AddPaymentAsync(Guid order, Action<Payment> state, string provider = "bank_transfer", string? reference = null)
    {
        await using var db = _kit.Db();
        var p = new Payment(Guid.NewGuid(), order, provider, reference ?? "PROVIDER-REF-SECRET-" + Guid.NewGuid().ToString("N"), 12_345, "BDT");
        state(p);
        db.Payments.Add(p);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CompanyWithOwnerAsync()
    {
        var w = await _kit.CompanyAsync("Staff Read Ltd");
        return w.Company;
    }

    private static Task<HttpResponseMessage> List(HttpClient c, string query) => c.GetAsync("/api/v1/admin/orders" + query);

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient c, string query)
    {
        var data = await DataAsync(await List(c, query));
        return data.GetProperty("items").EnumerateArray().ToList();
    }

    // ---- permission matrix ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Permission_matrix_anonymous_401_without_view_all_403_with_it_200_on_both_routes()
    {
        var order = (await BareAsync((await _kit.StaffAsync(Permissions.Roles.Buyer, "pm-buyer")).Id, null, "Confirmed", DateTime.UtcNow)).Id;
        using var anonymous = Anonymous(factory);
        (await anonymous.GetAsync("/api/v1/admin/orders")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/v1/admin/orders/{order}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator })
        {
            var who = await _kit.StaffAsync(role, "pm-" + role);
            (await List(who.Client, "")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await who.Client.GetAsync($"/api/v1/admin/orders/{order}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Admin })
        {
            var who = await _kit.StaffAsync(role, "pm-" + role);
            (await List(who.Client, "?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK, role);
            (await who.Client.GetAsync($"/api/v1/admin/orders/{order}")).StatusCode.Should().Be(HttpStatusCode.OK, role);
        }
    }

    // ---- list -----------------------------------------------------------------------------------------------------------

    private sealed record Set(Guid Company, Guid User, (Guid Id, string Number) Late, (Guid Id, string Number) EdgeIn, (Guid Id, string Number) EdgeOut,
        (Guid Id, string Number) Old, (Guid Id, string Number) Retail);

    /// <summary>One company with four orders around a Dhaka midnight (2026-03-10 18:00 UTC = 2026-03-11 00:00 Dhaka) and one retail order.</summary>
    private async Task<Set> SeedAsync()
    {
        var company = await CompanyWithOwnerAsync();
        var user = (await _kit.StaffAsync(Permissions.Roles.Buyer, "list-buyer")).Id;
        var edgeOut = await BareAsync(user, company, "PendingPayment", new DateTime(2026, 3, 10, 17, 59, 59, DateTimeKind.Utc), onCredit: true);
        var edgeIn = await BareAsync(user, company, "Confirmed", new DateTime(2026, 3, 10, 18, 0, 0, DateTimeKind.Utc));
        var old = await BareAsync(user, company, "Cancelled", new DateTime(2026, 3, 9, 6, 0, 0, DateTimeKind.Utc));
        var late = await BareAsync(user, company, "Confirmed", new DateTime(2026, 3, 12, 6, 0, 0, DateTimeKind.Utc));
        var retail = await BareAsync(user, null, "PendingPayment", new DateTime(2026, 3, 13, 6, 0, 0, DateTimeKind.Utc));
        await AddPaymentAsync(edgeIn.Id, p => p.MarkCaptured(DateTime.UtcNow));
        await AddPaymentAsync(old.Id, _ => { });
        await using (var db = _kit.Db())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE orders SET legal_delivery_deadline_at = {DateTime.UtcNow.AddDays(-3)}, courier_handover_deadline_at = {DateTime.UtcNow.AddDays(-4)} WHERE id = {late.Id}");
        return new Set(company, user, late, edgeIn, edgeOut, old, retail);
    }

    [PostgresFact]
    public async Task Each_filter_narrows_the_list_and_newest_comes_first()
    {
        var s = await SeedAsync();
        var admin = (await _kit.StaffAsync(Permissions.Roles.Admin, "list-admin")).Client;
        async Task<List<string>> Numbers(string query) => (await ItemsAsync(admin, $"?companyId={s.Company}&{query}")).Select(i => i.GetProperty("orderNumber").GetString()!).ToList();

        (await Numbers("")).Should().Equal(s.Late.Number, s.EdgeIn.Number, s.EdgeOut.Number, s.Old.Number);
        (await Numbers("status=confirmed")).Should().Equal(s.Late.Number, s.EdgeIn.Number);
        (await Numbers("status=pending_payment")).Should().Equal(s.EdgeOut.Number);
        (await Numbers("status=CANCELLED")).Should().Equal(s.Old.Number);
        (await Numbers("paymentState=captured")).Should().Equal(s.EdgeIn.Number);
        (await Numbers("paymentState=pending")).Should().Equal(s.Old.Number);
        (await Numbers("paymentState=on_credit")).Should().Equal(s.EdgeOut.Number);
        (await Numbers("paymentState=none")).Should().Equal(s.Late.Number);
        (await Numbers("paymentState=refunded")).Should().BeEmpty();
        (await Numbers("overdue=true")).Should().Equal(s.Late.Number);
        (await Numbers("overdue=false")).Should().Equal(s.EdgeIn.Number, s.EdgeOut.Number, s.Old.Number);
        (await Numbers($"orderNumber={s.Old.Number.ToLowerInvariant()}")).Should().Equal(s.Old.Number);
        (await Numbers("orderNumber=MW-2")).Should().HaveCount(4);

        // The order number filter works without the company filter too, and a company filter hides other companies and retail.
        (await ItemsAsync(admin, $"?orderNumber={s.Retail.Number}")).Should().ContainSingle()
            .Which.Should().Match<JsonElement>(i => i.GetProperty("companyId").ValueKind == JsonValueKind.Null && i.GetProperty("companyName").ValueKind == JsonValueKind.Null
                                                    && i.GetProperty("source").GetString() == "cart" && i.GetProperty("quoteNumber").ValueKind == JsonValueKind.Null);
        (await ItemsAsync(admin, $"?companyId={Guid.NewGuid()}")).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Dates_are_Dhaka_calendar_days_and_both_ends_are_inclusive()
    {
        var s = await SeedAsync();
        var admin = (await _kit.StaffAsync(Permissions.Roles.Admin, "date-admin")).Client;
        async Task<List<string>> Numbers(string query) => (await ItemsAsync(admin, $"?companyId={s.Company}&{query}")).Select(i => i.GetProperty("orderNumber").GetString()!).ToList();

        // 17:59:59Z on the 10th is still the 10th in Dhaka; 18:00:00Z is already the 11th.
        (await Numbers("from=2026-03-10&to=2026-03-10")).Should().Equal(s.EdgeOut.Number);
        (await Numbers("from=2026-03-11&to=2026-03-11")).Should().Equal(s.EdgeIn.Number);
        (await Numbers("from=2026-03-11")).Should().Equal(s.Late.Number, s.EdgeIn.Number);
        (await Numbers("to=2026-03-10")).Should().Equal(s.EdgeOut.Number, s.Old.Number);
        (await Numbers("from=2026-03-09&to=2026-03-12")).Should().HaveCount(4);
        (await Numbers("from=2026-03-14")).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Paging_has_a_total_and_a_page_size_limit_of_50()
    {
        var s = await SeedAsync();
        var admin = (await _kit.StaffAsync(Permissions.Roles.Admin, "page-admin")).Client;
        var p2 = await DataAsync(await List(admin, $"?companyId={s.Company}&page=2&pageSize=3"));
        (p2.GetProperty("totalCount").GetInt32(), p2.GetProperty("page").GetInt32(), p2.GetProperty("pageSize").GetInt32(), p2.GetProperty("items").GetArrayLength())
            .Should().Be((4, 2, 3, 1));
        (await DataAsync(await List(admin, "?pageSize=500"))).GetProperty("pageSize").GetInt32().Should().Be(50);
        (await DataAsync(await List(admin, "?pageSize=0&page=0"))).Should().Match<JsonElement>(d => d.GetProperty("pageSize").GetInt32() == 1 && d.GetProperty("page").GetInt32() == 1);
        (await DataAsync(await List(admin, $"?companyId={s.Company}&page=9"))).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [PostgresFact]
    public async Task Bad_filters_are_400()
    {
        var admin = (await _kit.StaffAsync(Permissions.Roles.Admin, "bad-admin")).Client;
        foreach (var query in new[]
                 {
                     "status=bogus", "status=1", "paymentState=bogus", "from=2026-13-01", "to=yesterday", "from=2026-03-12&to=2026-03-10", "from=10/03/2026",
                     "orderNumber=a%20b!", "orderNumber=" + new string('A', 41), "companyId=not-a-guid", "overdue=maybe", "page=x", "pageSize=x"
                 })
            (await List(admin, "?" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
    }

    [PostgresFact]
    public async Task List_rows_have_only_whitelisted_fields_no_personal_data_and_are_no_store()
    {
        var s = await SeedAsync();
        var support = (await _kit.StaffAsync(Permissions.Roles.Support, "wl-support")).Client;
        var r = await List(support, $"?companyId={s.Company}");
        NoStore(r);
        var raw = await r.Content.ReadAsStringAsync();
        var names = new HashSet<string>();
        Names(JsonDocument.Parse(raw).RootElement.GetProperty("data").GetProperty("items"), names);
        names.Should().BeEquivalentTo(ListFields);
        raw.Should().NotContain("Rfq buyer").And.NotContain("@example.test").And.NotContain(Phone);
    }

    // ---- detail ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Unknown_order_is_404_and_a_malformed_id_never_reaches_the_service()
    {
        var admin = (await _kit.StaffAsync(Permissions.Roles.Admin, "nf-admin")).Client;
        (await admin.GetAsync($"/api/v1/admin/orders/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/v1/admin/orders/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task A_cart_order_shows_lines_totals_address_events_payments_refunds_and_no_secrets()
    {
        var buyer = await _kit.StaffAsync(Permissions.Roles.Buyer, "detail-buyer");
        Guid address, variant;
        await using (var db = _kit.Db())
        {
            await db.Users.Where(u => u.Id == buyer.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Phone, Phone));
            address = await db.Addresses.Where(a => a.UserId == buyer.Id).Select(a => a.Id).FirstAsync();
            variant = (await CatalogTestData.SeedVariantAsync(db, 20, priceMinor: 100_000)).VariantId;
        }

        using var place = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new { shippingAddressId = address, lines = new[] { new { variantId = variant, quantity = 2 } } })
        };
        place.Headers.Add("Idempotency-Key", "g55-" + Guid.NewGuid().ToString("N"));
        var placed = await DataAsync(await buyer.Client.SendAsync(place), HttpStatusCode.Created);
        var orderId = placed.GetProperty("orderId").GetGuid();

        var staffId = (await _kit.StaffAsync(Permissions.Roles.Finance, "detail-finance")).Id;
        await AddPaymentAsync(orderId, p => p.MarkCaptured(DateTime.UtcNow), "sslcommerz");
        await AddPaymentAsync(orderId, p => p.MarkFailed("NOTE-SECRET-from-gateway", DateTime.UtcNow), "bkash");
        await using (var db = _kit.Db())
        {
            var paid = await db.Payments.SingleAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Captured);
            db.Refunds.Add(new Refund(Guid.NewGuid(), paid.Id, 5_000, "BDT", "customer returned one part", staffId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var support = (await _kit.StaffAsync(Permissions.Roles.Support, "detail-support")).Client;
        var r = await support.GetAsync($"/api/v1/admin/orders/{orderId}");
        NoStore(r);
        var raw = await r.Content.ReadAsStringAsync();
        var d = JsonDocument.Parse(raw).RootElement.GetProperty("data");

        d.GetProperty("id").GetGuid().Should().Be(orderId);
        d.GetProperty("source").GetString().Should().Be("cart");
        d.GetProperty("quote").ValueKind.Should().Be(JsonValueKind.Null);
        d.GetProperty("status").GetString().Should().Be("pending_payment");
        d.GetProperty("grandTotalMinor").GetInt64().Should().Be(placed.GetProperty("grandTotalMinor").GetInt64());
        d.GetProperty("buyer").GetProperty("userId").GetGuid().Should().Be(buyer.Id);
        d.GetProperty("buyer").GetProperty("name").GetString().Should().Be("Rfq buyer");
        d.GetProperty("buyer").GetProperty("phone").GetString().Should().Be(Phone);
        d.GetProperty("shipTo").GetProperty("line1").GetString().Should().NotBeNullOrWhiteSpace();
        d.GetProperty("paymentState").GetString().Should().Be("captured");
        d.GetProperty("credit").ValueKind.Should().Be(JsonValueKind.Null);

        var line = d.GetProperty("subOrders")[0].GetProperty("lines")[0];
        line.GetProperty("quantity").GetInt32().Should().Be(2);
        line.GetProperty("variantId").GetGuid().Should().Be(variant);
        line.GetProperty("priceSnapshot").GetProperty("source").GetString().Should().NotBeNull();
        d.GetProperty("subOrders").EnumerateArray().Sum(x => x.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("totalMinor").GetInt64()))
            .Should().BeLessThanOrEqualTo(d.GetProperty("grandTotalMinor").GetInt64());

        var events = d.GetProperty("events").EnumerateArray().ToList();
        events.Should().ContainSingle();
        (events[0].GetProperty("sequence").GetInt32(), events[0].GetProperty("reason").GetString()).Should().Be((1, "Order placed"));

        var payments = d.GetProperty("payments").EnumerateArray().ToList();
        payments.Select(p => p.GetProperty("method").GetString()).Should().BeEquivalentTo("sslcommerz", "bkash");
        var captured = payments.Single(p => p.GetProperty("status").GetString() == "captured");
        captured.GetProperty("refunds").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("amountMinor").GetInt64().Should().Be(5_000);

        var names = new HashSet<string>();
        Names(d, names);
        names.Should().BeSubsetOf(DetailFields);
        raw.Should().NotContain("PROVIDER-REF-SECRET").And.NotContain("NOTE-SECRET").And.NotContainEquivalentOf("providerRef").And.NotContainEquivalentOf("password");
    }

    [PostgresFact]
    public async Task A_quote_order_on_credit_shows_the_quote_link_the_price_source_and_the_credit_state()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support, "q-support");
        var buyer = await _kit.StaffAsync(Permissions.Roles.Buyer, "q-buyer");
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Staff Read Quote Ltd", (buyer.Id, CompanyRoles.Buyer));
        await using (var db = _kit.Db())
            await new CompanyService(db).AdminSetCreditAsync(w.Company, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SentAsync(support.Client, quote, "sent");
        var version = await VersionAsync(support.Client, quote);

        using var accept = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/companies/{w.Company}/quotes/{quote}/accept")
        {
            Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["versionNo"] = version.GetProperty("versionNo").GetInt32(),
                ["contentHash"] = version.GetProperty("contentHash").GetString(),
                ["expectedGrandTotalMinor"] = version.GetProperty("goodsTotalMinor").GetInt64(),
                ["payOnCredit"] = true
            })
        };
        accept.Headers.Add("Idempotency-Key", "g55q-" + Guid.NewGuid().ToString("N"));
        var accepted = await DataAsync(await buyer.Client.SendAsync(accept), HttpStatusCode.Created);
        var orderId = accepted.GetProperty("orderId").GetGuid();

        var admin = (await _kit.StaffAsync(Permissions.Roles.Admin, "q-admin")).Client;
        var d = await DataAsync(await admin.GetAsync($"/api/v1/admin/orders/{orderId}"));
        d.GetProperty("source").GetString().Should().Be("quote");
        d.GetProperty("quote").GetProperty("number").GetString().Should().Be(accepted.GetProperty("quoteNumber").GetString());
        d.GetProperty("quote").GetProperty("quoteId").GetGuid().Should().Be(quote);
        d.GetProperty("companyId").GetGuid().Should().Be(w.Company);
        d.GetProperty("companyName").GetString().Should().StartWith("Staff Read Quote Ltd");
        d.GetProperty("status").GetString().Should().Be("confirmed");
        d.GetProperty("paysOnCredit").GetBoolean().Should().BeTrue();
        d.GetProperty("paymentState").GetString().Should().Be("on_credit");
        d.GetProperty("legalDeliveryDeadlineUtc").ValueKind.Should().Be(JsonValueKind.String);
        var credit = d.GetProperty("credit");
        credit.GetProperty("status").GetString().Should().Be("open");
        credit.GetProperty("chargedMinor").GetInt64().Should().Be(d.GetProperty("grandTotalMinor").GetInt64());
        credit.GetProperty("outstandingMinor").GetInt64().Should().Be(d.GetProperty("grandTotalMinor").GetInt64());
        var snapshot = d.GetProperty("subOrders")[0].GetProperty("lines")[0].GetProperty("priceSnapshot");
        snapshot.GetProperty("source").GetString().Should().Be("quote");
        snapshot.GetProperty("quoteLineId").ValueKind.Should().Be(JsonValueKind.String);
        d.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("toStatus").GetString()).Should().Contain("confirmed");

        var row = (await ItemsAsync(admin, $"?companyId={w.Company}")).Single();
        (row.GetProperty("source").GetString(), row.GetProperty("quoteNumber").GetString()).Should().Be(("quote", accepted.GetProperty("quoteNumber").GetString()));
        row.GetProperty("paymentState").GetString().Should().Be("on_credit");
    }
}
