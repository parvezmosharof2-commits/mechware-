using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Quotes;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Quotes;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>A clock the quote desk reads (and nothing else: sign-ins and authenticator codes stay on the real clock), moved by a test.</summary>
public sealed class ShiftableClock : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

/// <summary>
/// The host of the D4.8 tests: roomy RFQ limits (as <see cref="RfqApiFactory"/>), a quote desk on a <see cref="ShiftableClock"/>, and a fixed
/// client address so the audit rows can be checked for it.
/// </summary>
public sealed class QuoteIssueFactory() : DbBackedFactory(null, new Dictionary<string, string>
{
    ["RateLimiting:QuoteRequestsPermitPerMinute"] = "10000",
    ["Quotes:MaxRfqsPerCompanyPerHour"] = "1000",
    ["Quotes:MaxOpenRfqsPerCompany"] = "1000",
})
{
    public const string ClientIp = "203.0.113.48";

    public ShiftableClock Clock { get; } = new();

    /// <summary>Staff signed in once per host and shared by the tests of a class (sign-ins are limited to 25 a minute per host).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<RfqApiKit.Person>>> Staff { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddScoped<IStaffQuoteService>(sp => ActivatorUtilities.CreateInstance<StaffQuoteService>(sp, (TimeProvider)Clock));
            s.AddTransient<IStartupFilter, FixedClientIp>();
        });
    }

    private sealed class FixedClientIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, call) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
                return call(context);
            });
            next(app);
        };
    }
}

/// <summary>
/// Shared set-up of the D4.8 send / approve tests: real staff sign-ins (all with the same authenticator secret), buyers that never sign in,
/// priced catalogue items with a cost, the global floor, drafts written through the D4.7 editor, and the D4.8 calls.
/// </summary>
public sealed class QuoteIssueKit(QuoteIssueFactory factory)
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    public StaffQuoteKit Desk { get; } = new(factory);

    public QuoteIssueFactory Factory => factory;

    public sealed record World(Guid Company, Guid Owner, Guid Buyer);

    public sealed record Item(Guid Variant, long Net, long Cost);

    public MechwareDbContext Db() => Desk.Db();

    /// <summary>A signed-in staff member of <paramref name="role"/>; the same <paramref name="name"/> is the same person in every test of the class.</summary>
    public Task<RfqApiKit.Person> StaffAsync(string role, string? name = null) =>
        factory.Staff.GetOrAdd($"{role}/{name ?? role}", _ => new Lazy<Task<RfqApiKit.Person>>(() => Desk.Rfq.PersonAsync(role))).Value;

    /// <summary>The current authenticator code of <paramref name="userId"/>, usable once more: its replay guard is cleared first.</summary>
    public async Task<string> CodeAsync(Guid userId)
    {
        await using var db = Db();
        return await CodeAsync(db, userId);
    }

    public static async Task<string> CodeAsync(MechwareDbContext db, Guid userId)
    {
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>An active company with an owner (Admin) and a Buyer member who is the contact; <paramref name="members"/> join as given.</summary>
    public async Task<World> CompanyAsync(string name, params (Guid User, CompanyRoles Roles)[] members)
    {
        var (owner, buyer) = (await Desk.BuyerAsync(), await Desk.BuyerAsync());
        var company = await Desk.Rfq.CompanyAsync(owner, name + " " + Guid.NewGuid().ToString("N")[..6], true, [(buyer, CompanyRoles.Buyer), .. members]);
        return new World(company, owner, buyer);
    }

    /// <summary>The owner's 10 % global floor (cost + 10 %), whatever an earlier test of this database left.</summary>
    public async Task TenPercentFloorAsync()
    {
        await using var db = Db();
        var global = await db.MarginFloors.FirstOrDefaultAsync(f => f.Scope == MarginFloorScope.Global);
        if (global is null) db.MarginFloors.Add(MarginFloor.Create(Guid.NewGuid(), MarginFloorScope.Global, null, null, 10m, Guid.NewGuid(), DateTime.UtcNow));
        else global.ChangeMarkup(10m, Guid.NewGuid(), DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A sellable item whose cost is <paramref name="costPercent"/> % of its net public price (null: no cost recorded). With the 10 % floor and
    /// a cost of 50 %, the floor is 55 % of the net price; a price within 5 % of the net price is within the discount authority.
    /// </summary>
    public async Task<Item> ItemAsync(int? costPercent = 50)
    {
        var variant = (await Desk.Rfq.VariantAsync(null)).VariantId;
        await using var db = Db();
        var row = await (from v in db.ProductVariants
                         join p in db.Products on v.ProductId equals p.Id
                         join c in db.Categories on p.CategoryId equals c.Id
                         where v.Id == variant
                         select new { v.Price, c.VatRate }).SingleAsync();
        var net = PriceResolver.ToOrderBasis(row.Price.ToMinorUnits(), row.VatRate, TaxMode.Exclusive);
        long? cost = costPercent is { } pct ? net * pct / 100 : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {cost} WHERE id = {variant}");
        return new Item(variant, net, cost ?? 0);
    }

    public async Task SetCostAsync(Guid variant, long cost)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET cost_price_minor = {cost} WHERE id = {variant}");
    }

    public static object Line(Item item, long price, int quantity = 2, string? reason = null) =>
        new { variantId = item.Variant, quantity, unitPriceMinor = price, belowFloorReason = reason };

    /// <summary>A quote for <paramref name="w"/> made by <paramref name="author"/> (from the RFQ when given) whose version 1 holds <paramref name="lines"/>.</summary>
    public async Task<Guid> DraftAsync(HttpClient author, World w, object[] lines, int validityDays = 15, Guid? rfqId = null, object? header = null)
    {
        var quote = await StaffQuoteKit.CreateAsync(author, rfqId is { } r ? new { rfqId = r } : new { companyId = w.Company, contactUserId = w.Buyer });
        await EditAsync(author, quote, 1, lines, validityDays, header);
        return quote;
    }

    public static async Task EditAsync(HttpClient editor, Guid quote, int n, object[] lines, int validityDays = 15, object? header = null)
    {
        var rowVersion = (await VersionAsync(editor, quote, n)).GetProperty("rowVersion").GetUInt32();
        var body = header ?? new { };
        var json = JsonSerializer.SerializeToNode(body)!.AsObject();
        json["rowVersion"] = rowVersion;
        json["validityDays"] = validityDays;
        json["lines"] = JsonSerializer.SerializeToNode(lines);
        (await editor.PutAsync($"/api/v1/admin/quotes/{quote}/versions/{n}", new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static string Url(Guid quote, int n, string action) => $"/api/v1/admin/quotes/{quote}/versions/{n}/{action}";

    public static Task<HttpResponseMessage> SendAsync(HttpClient who, Guid quote, int n = 1) => StaffQuoteKit.PostAsync(who, Url(quote, n, "send"));

    /// <summary>Sends and expects <paramref name="status"/> (<c>sent</c> or <c>pending_approval</c>); answers the send's body.</summary>
    public static async Task<JsonElement> SentAsync(HttpClient who, Guid quote, string status, int n = 1)
    {
        var data = await StaffQuoteKit.DataAsync(await SendAsync(who, quote, n));
        data.GetProperty("status").GetString().Should().Be(status);
        return data;
    }

    public static Task<HttpResponseMessage> ApproveWithAsync(HttpClient who, Guid quote, int n, object body, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(quote, n, "approve")) { Content = JsonContent.Create(body) };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.SendAsync(request);
    }

    /// <summary>An approval by <paramref name="who"/> with a fresh code that returns the stored hash and exactly the stored flags.</summary>
    public async Task<HttpResponseMessage> ApproveAsync(RfqApiKit.Person who, Guid quote, int n = 1, string? comment = null)
    {
        var v = await VersionAsync(who.Client, quote, n);
        var flags = v.GetProperty("flags");
        return await ApproveWithAsync(who.Client, quote, n, new
        {
            contentHash = v.GetProperty("contentHash").GetString(),
            belowFloorLineIds = flags.GetProperty("belowFloorLineIds"),
            overAuthorityLineIds = flags.GetProperty("overAuthorityLineIds"),
            comment,
        }, await CodeAsync(who.Id));
    }

    public static async Task<JsonElement> VersionAsync(HttpClient who, Guid quote, int n = 1) =>
        (await StaffQuoteKit.DataAsync(await who.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[n - 1];

    public static List<Guid> Ids(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetGuid())];

    public async Task<Quote> LoadAsync(Guid quote)
    {
        await using var db = Db();
        return await StaffQuoteKit.LoadAsync(db, quote);
    }

    public async Task<List<AuditLog>> AuditAsync(Guid quote, string? action = null)
    {
        await using var db = Db();
        return await db.AuditLogs.Where(a => a.EntityId == quote.ToString() && a.Action.StartsWith("quote.") && (action == null || a.Action == action))
            .OrderBy(a => a.CreatedAtUtc).ToListAsync();
    }

    public async Task<List<string>> OutboxAsync(Guid quote)
    {
        await using var db = Db();
        return await db.OutboxMessages.Where(o => o.AggregateId == quote).OrderBy(o => o.OccurredAtUtc).Select(o => o.EventType).ToListAsync();
    }

    public async Task<Rfq> RfqOfAsync(Guid rfqId)
    {
        await using var db = Db();
        return await db.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfqId);
    }
}
