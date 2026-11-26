using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>A host in Specimen mode (allowed outside Production) with no issuer seller set, so every seller's sub-order gets a SPEC number.</summary>
public sealed class SpecimenInvoicingApiFactory() : DbBackedFactory(settings: new Dictionary<string, string> { ["Invoicing:Mode"] = "Specimen" });

/// <summary>E6.3c over HTTP: the ship answer, the staff order detail and the buyer's order (by id and by number) carry <c>invoiceNumbers</c>.</summary>
public sealed class InvoiceAtShipmentEndpointTests(SpecimenInvoicingApiFactory factory) : IClassFixture<SpecimenInvoicingApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "InvoiceStaff#2026!";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private async Task<(HttpClient Client, Guid UserId)> LoginAsync(string role)
    {
        factory.CreateClient().Dispose();
        var email = $"inv.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Invoice " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return (client, id);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static List<string?> Numbers(JsonElement e) => [.. e.GetProperty("invoiceNumbers").EnumerateArray().Select(n => n.GetString())];

    [PostgresFact]
    public async Task Ship_IssuesTheInvoice_AndItsNumberShowsToStaffAndToTheBuyer()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var support = await LoginAsync(Permissions.Roles.Support);
        Guid orderId;
        string orderNumber;
        await using (var db = Db())
        {
            var order = await InvoiceTestIssues.PackedOrderAsync(db, buyer.UserId, [await InvoiceTestIssues.SellerAsync(db)]);
            (orderId, orderNumber) = (order.Id, order.OrderNumber);
        }

        var before = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/orders/{orderId}"));
        Numbers(before).Should().BeEmpty();
        Numbers(await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{orderId}"))).Should().BeEmpty();

        var shipped = await DataAsync(await support.Client.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/ship",
            new { rowVersion = before.GetProperty("rowVersion").GetUInt32(), courierName = "Steadfast", trackingNumber = "SF-6-3", trackingUrl = (string?)null }));
        var number = Numbers(shipped).Should().ContainSingle().Subject!;
        number.Should().StartWith("SPEC-INV-").And.EndWith("-000001");

        Numbers(await DataAsync(await support.Client.GetAsync($"/api/v1/admin/orders/{orderId}"))).Should().Equal(number);
        Numbers(await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{orderId}"))).Should().Equal(number);
        Numbers(await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/by-number/{orderNumber}"))).Should().Equal(number);
    }
}

/// <summary>ADR-0012 §3 wired into the API host (Program.cs): Production names the mode; Specimen and Live without the issuer facts stop it.</summary>
public sealed class InvoicingStartupTests
{
    private static Dictionary<string, string> Settings(string environment, string? mode)
    {
        var settings = environment == "Production" ? OrderShippingStartupTests.ProductionSettings() : [];
        settings.Remove("Invoicing:Mode");
        if (mode == "LiveWithFacts")
        {
            var facts = InvoiceTestIssues.Options(Guid.NewGuid());
            settings["Invoicing:IssuerSellerId"] = facts.IssuerSellerId!.Value.ToString();
            settings["Invoicing:LegalNameEn"] = facts.LegalNameEn!;
            settings["Invoicing:LegalNameBn"] = facts.LegalNameBn!;
            settings["Invoicing:Bin"] = facts.Bin!;
            settings["Invoicing:RegisteredAddressEn"] = facts.RegisteredAddressEn!;
            settings["Invoicing:RegisteredAddressBn"] = facts.RegisteredAddressBn!;
            settings["Invoicing:ResponsiblePersonName"] = facts.ResponsiblePersonName!;
            settings["Invoicing:ResponsiblePersonDesignation"] = facts.ResponsiblePersonDesignation!;
            mode = "Live";
        }
        if (mode is not null) settings["Invoicing:Mode"] = mode;
        return settings;
    }

    [PostgresTheory]
    [InlineData("Production", null, "Invoicing:Mode must be written in the Production config")]
    [InlineData("Production", "Specimen", "Invoicing:Mode Specimen is not allowed in Production")]
    [InlineData("Production", "Live", "Invoicing:IssuerSellerId is required in Live mode")]
    [InlineData("Development", "Live", "Invoicing:Bin is required in Live mode")]
    [InlineData("Production", "Sometimes", "Invoicing")]
    public async Task TheHost_RefusesToStart(string environment, string? mode, string message)
    {
        await using var factory = new DbBackedFactory(environment, Settings(environment, mode));
        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>().Where(e => e.ToString().Contains(message));
    }

    [PostgresTheory]
    [InlineData("Production", "Off", InvoicingMode.Off)]
    [InlineData("Production", "LiveWithFacts", InvoicingMode.Live)]
    [InlineData("Development", null, InvoicingMode.Off)]
    [InlineData("Development", "Specimen", InvoicingMode.Specimen)]
    public async Task TheHost_Starts_InTheConfiguredMode(string environment, string? mode, InvoicingMode expected)
    {
        await using var factory = new DbBackedFactory(environment, Settings(environment, mode));
        factory.CreateClient().Dispose();
        factory.Services.GetRequiredService<InvoicingOptions>().EffectiveMode.Should().Be(expected);
    }
}
