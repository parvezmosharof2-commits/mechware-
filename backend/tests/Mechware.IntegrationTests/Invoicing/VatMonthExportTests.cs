using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>Its own host and database: five exports a minute per person, so the limit test needs only six calls.</summary>
public sealed class VatExportFactory() : DbBackedFactory(settings: new Dictionary<string, string> { ["RateLimiting:InvoiceExportPermitPerMinute"] = "5" });

/// <summary>
/// E6.7 (ADR-0012 §13, threat model IT12, IT17): the monthly 9.1 export over real invoices issued the way ship issues them (counter last), then
/// moved to the instant a test needs with every trigger off (<c>session_replication_role = replica</c>, the E6.3d pattern, this class's own
/// database). Each test owns a different 2025 month, so tests never see each other's rows.
/// </summary>
public sealed class VatMonthExportTests(VatExportFactory factory) : IClassFixture<VatExportFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "VatExport#2026!";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.7";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-e67";
        public bool HasPermission(string permission) => true;
    }

    private async Task<VatMonthReport> PreviewAsync(string month, InvoicingOptions? options = null)
    {
        await using var db = Db();
        return await new VatMonthExportService(db, new Staff(Guid.NewGuid()), options ?? new InvoicingOptions(), TimeProvider.System).PreviewAsync(month);
    }

    private async Task<(Guid Seller, Company Company)> SellerAsync()
    {
        await using var db = Db();
        return (await InvoiceTestIssues.SellerAsync(db), await InvoiceTestIssues.ActiveCompanyAsync(db));
    }

    /// <summary>A shipped order's tax invoice (counter last, one transaction), then moved to <paramref name="at"/>.</summary>
    private async Task<Invoice> IssueAsync(Guid seller, Company? company, DateTime at, InvoiceSeries series = InvoiceSeries.TaxInvoice)
    {
        Invoice invoice;
        await using (var db = Db())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
            invoice = InvoiceTestIssues.Build(order, shipment, seller,
                await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, series), company);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await MoveAsync(invoice.Id, at);
        return invoice;
    }

    /// <summary>A credit note of one PLC unit (net and VAT given) of <paramref name="original"/>, then moved to <paramref name="at"/>.</summary>
    private async Task<Guid> CreditAsync(Invoice original, long net, long vat, DateTime at)
    {
        Guid id;
        await using (var db = Db())
        {
            var requester = await InvoiceTestIssues.UserAsync(db);
            await using var tx = await db.Database.BeginTransactionAsync();
            var series = original.Mode == InvoicingMode.Live ? InvoiceSeries.CreditNote : InvoiceSeries.SpecimenCreditNote;
            var taken = await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(original.SellerId, series);
            var line = original.Lines.Single(l => l.Sku == "PLC-1200").LineNo;
            id = await InvoiceTestIssues.CreditNoteAsync(db, original.Id, taken, requester, [new InvoiceTestIssues.CreditSpec(line, 1, net, vat)]);
            await tx.CommitAsync();
        }

        await MoveAsync(id, at);
        return id;
    }

    private Task MoveAsync(Guid invoiceId, DateTime at) =>
        RawAsync("UPDATE invoices SET issued_at = @p1, created_at = @p1 WHERE id = @p0", invoiceId, at);

    private async Task RawAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var off = new NpgsqlCommand("SET LOCAL session_replication_role = replica", conn, tx)) await off.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("p" + i, args[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    [PostgresFact]
    public async Task VatExport_TakesTheDhakaMonth_NotTheUtcMonth()
    {
        var (seller, company) = await SellerAsync();
        var lastOfDecember = await IssueAsync(seller, company, Utc(2024, 12, 31, 17, 59, 59));   // 23:59:59 on 31 Dec in Dhaka
        var firstOfJanuary = await IssueAsync(seller, company, Utc(2024, 12, 31, 18, 0, 0));    // 00:00 on 1 Jan in Dhaka
        var lastOfJanuary = await IssueAsync(seller, null, Utc(2025, 1, 31, 17, 30));            // 23:30 on 31 Jan in Dhaka
        var firstOfFebruary = await IssueAsync(seller, null, Utc(2025, 1, 31, 20, 0));           // 02:00 on 1 Feb in Dhaka, still January in UTC

        var january = await PreviewAsync("2025-01");

        january.Sales.Select(s => s.Number).Should().Equal(firstOfJanuary.Number, lastOfJanuary.Number);
        (january.FromUtc, january.ToUtc).Should().Be((Utc(2024, 12, 31, 18), Utc(2025, 1, 31, 18)));
        january.Sales[0].IssuedAtDhaka.Should().Be("2025-01-01T00:00:00+06:00");
        (await PreviewAsync("2024-12")).Sales.Select(s => s.Number).Should().Contain(lastOfDecember.Number).And.NotContain(firstOfJanuary.Number);
        (await PreviewAsync("2025-02")).Sales.Select(s => s.Number).Should().Equal(firstOfFebruary.Number);
    }

    [PostgresFact]
    public async Task VatExport_NeverContainsSpecimens_OrAnotherMonth()
    {
        var (seller, company) = await SellerAsync();
        var live = await IssueAsync(seller, company, Utc(2025, 3, 10, 6));
        var specimen = await IssueAsync(seller, company, Utc(2025, 3, 11, 6), InvoiceSeries.SpecimenTaxInvoice);
        var specimenCredit = await CreditAsync(specimen, 1_000_000, 150_000, Utc(2025, 3, 12, 6));
        await IssueAsync(seller, company, Utc(2025, 4, 2, 6));   // another month

        var march = await PreviewAsync("2025-03", new InvoicingOptions { Mushak610ThresholdMinor = 0 });

        march.Sales.Select(s => s.InvoiceId).Should().Equal(live.Id);
        march.CreditNotes.Should().BeEmpty();
        march.Mushak610Candidates.Select(c => c.Number).Should().Equal(live.Number);
        march.Totals.SalesVatMinor.Should().Be(live.VatMinor);
        march.RateTotals.Sum(r => r.SalesVatMinor).Should().Be(live.VatMinor, "a specimen line never reaches the per-rate totals either");
        march.RateTotals.Sum(r => r.CreditVatMinor).Should().Be(0);
        specimenCredit.Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task VatExport_ACreditNoteReducesTheMonthItIsIssuedIn()
    {
        var (seller, company) = await SellerAsync();
        var may = await IssueAsync(seller, company, Utc(2025, 5, 20, 6));
        var june = await IssueAsync(seller, company, Utc(2025, 6, 3, 6));
        var credit = await CreditAsync(may, 1_000_000, 150_000, Utc(2025, 6, 9, 6));

        var report = await PreviewAsync("2025-06");

        report.Sales.Select(s => s.InvoiceId).Should().Equal(june.Id);
        var row = report.CreditNotes.Should().ContainSingle().Subject;
        (row.InvoiceId, row.OriginalNumber, row.OriginalMonth, row.NetMinor, row.VatMinor).Should().Be((credit, may.Number, "2025-05", 1_000_000L, 150_000L));
        report.Totals.NetVatMinor.Should().Be(june.VatMinor - 150_000);
        report.RateTotals.Single(r => r.VatRate == 0.15m).CreditVatMinor.Should().Be(150_000);
        (await PreviewAsync("2025-05")).Totals.Should().Match<VatMonthTotals>(t => t.CreditNotes == 0 && t.SalesVatMinor == may.VatMinor, "May itself is never rewritten");
    }

    [PostgresFact]
    public async Task VatExport_SumsEqualTheInvoicesOfTheMonth()
    {
        var (seller, company) = await SellerAsync();
        var august = new List<Invoice>();
        for (var day = 1; day <= 4; day++) august.Add(await IssueAsync(seller, day % 2 == 0 ? company : null, Utc(2025, 8, day * 5, 9)));
        await CreditAsync(august[1], 500_000, 75_000, Utc(2025, 8, 25, 9));
        await CreditAsync(august[2], 300_000, 45_000, Utc(2025, 8, 26, 9));
        await IssueAsync(seller, company, Utc(2025, 8, 31, 18, 0, 1));   // 1 September in Dhaka

        var report = await PreviewAsync("2025-08");

        // Independent of VatMonth: the bounds are written out, Dhaka midnight = 18:00 UTC the day before.
        async Task<long[]> SqlAsync(string kind)
        {
            await using var conn = new NpgsqlConnection(factory.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*), coalesce(sum(net_minor), 0)::bigint, coalesce(sum(sd_minor), 0)::bigint, coalesce(sum(vat_minor), 0)::bigint, coalesce(sum(total_minor), 0)::bigint
                  FROM invoices WHERE mode = 'Live' AND kind = @k AND issued_at >= '2025-07-31 18:00:00+00' AND issued_at < '2025-08-31 18:00:00+00'
                """, conn);
            cmd.Parameters.AddWithValue("k", kind);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return [r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)];
        }

        var t = report.Totals;
        (await SqlAsync("TaxInvoice")).Should().Equal(t.Sales, t.SalesNetMinor, t.SalesSdMinor, t.SalesVatMinor, t.SalesTotalMinor);
        (await SqlAsync("CreditNote")).Should().Equal(t.CreditNotes, t.CreditNetMinor, t.CreditSdMinor, t.CreditVatMinor, t.CreditTotalMinor);
        t.Sales.Should().Be(4);
        t.SalesVatMinor.Should().Be(august.Sum(i => i.VatMinor));
        (t.CreditNetMinor, t.CreditVatMinor).Should().Be((800_000L, 120_000L));
        (report.RateTotals.Sum(r => r.SalesNetMinor), report.RateTotals.Sum(r => r.SalesVatMinor), report.RateTotals.Sum(r => r.CreditVatMinor))
            .Should().Be((t.SalesNetMinor, t.SalesVatMinor, t.CreditVatMinor), "the per-rate totals add up to the invoices");
        report.Sales.Sum(s => s.TotalMinor).Should().Be(t.SalesTotalMinor);
    }

    // ---- over HTTP -------------------------------------------------------------------------------------------------------

    private sealed record Person(HttpClient Client, Guid UserId);

    private async Task<Person> PersonAsync(string role)
    {
        var email = $"vx.{Guid.NewGuid():N}"[..22] + "@mechware.com.bd";
        var staff = role != Permissions.Roles.Buyer;
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "VAT export " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            if (staff)
            {
                user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
                user.EnableTwoFactor(DateTime.UtcNow);
            }
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", staff
            ? new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) }
            : (object)new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(client, id);
    }

    private async Task<string> CodeAsync(Person who)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == who.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    private static Task<HttpResponseMessage> ExportAsync(Person who, string month, string? format = null, string? code = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/invoices/exports/vat-month?month={month}" + (format is null ? "" : "&format=" + format));
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [PostgresFact]
    public async Task VatExport_NeedsPermissionAndStepUp_AndIsAudited()
    {
        var (seller, company) = await SellerAsync();
        var invoice = await IssueAsync(seller, company, Utc(2025, 10, 14, 5));
        const string evil = "=HYPERLINK(\"http://evil.test\",\"open\")";
        await RawAsync("UPDATE invoices SET buyer_snapshot = jsonb_set(buyer_snapshot, '{name}', to_jsonb(@p1::text)) WHERE id = @p0", invoice.Id, evil);

        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Moderator, Permissions.Roles.Buyer })
        {
            var who = await PersonAsync(role);
            (await ExportAsync(who, "2025-10")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await ExportAsync(who, "2025-10", "csv", await CodeAsync(who))).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
        (await factory.CreateClient().GetAsync("/api/v1/admin/invoices/exports/vat-month?month=2025-10")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var finance = await PersonAsync(Permissions.Roles.Finance);
        var preview = await ExportAsync(finance, "2025-10");
        var json = await preview.Content.ReadAsStringAsync();
        preview.StatusCode.Should().Be(HttpStatusCode.OK, json);
        (preview.Headers.CacheControl is { NoStore: true, Private: true }).Should().BeTrue();
        using (var doc = JsonDocument.Parse(json))
        {
            var sale = doc.RootElement.GetProperty("data").GetProperty("sales")[0];
            sale.GetProperty("number").GetString().Should().Be(invoice.Number);
            sale.GetProperty("buyerBin").GetString().Should().Be("*********" + company.Bin[^4..], "the preview masks the BIN; only the CSV with a code has it whole");
        }
        json.Should().NotContain(company.Bin);

        (await ErrorCodeAsync(await ExportAsync(finance, "2025-10", "csv"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await ExportAsync(finance, "2025-10", "csv", "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await ExportAsync(finance, "2025-1x", "csv", await CodeAsync(finance))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var csv = await ExportAsync(finance, "2025-10", "csv", await CodeAsync(finance));
        var text = await csv.Content.ReadAsStringAsync();
        csv.StatusCode.Should().Be(HttpStatusCode.OK, text);
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        (csv.Headers.CacheControl is { NoStore: true, Private: true }).Should().BeTrue();
        csv.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("mushak-9_1-2025-10.csv");
        text.Should().Contain($"\"{invoice.Number}\"").And.Contain($"\"{company.Bin}\"")
            .And.Contain("\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"open\"\")\"", "IT17: the formula is neutralised").And.NotContain(",\"=HYPERLINK");

        await using var db = Db();
        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.Action == VatMonthExportService.ExportedAction).ToListAsync();
        var audit = audits.Should().ContainSingle("the preview and the refused attempts are not exports").Subject;
        (audit.ActorId, audit.EntityId).Should().Be((finance.UserId, "2025-10"));
        using var details = JsonDocument.Parse(audit.NewValuesJson!);
        details.RootElement.GetProperty("sales").GetInt32().Should().Be(1);
        details.RootElement.GetProperty("salesVatMinor").GetInt64().Should().Be(invoice.VatMinor);
        audit.NewValuesJson.Should().NotContain(company.Bin);
    }

    [PostgresFact]
    public async Task VatExport_IsRateLimitedPerPerson()
    {
        var finance = await PersonAsync(Permissions.Roles.Finance);
        for (var n = 0; n < 5; n++) (await ExportAsync(finance, "2025-12")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExportAsync(finance, "2025-12")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ExportAsync(await PersonAsync(Permissions.Roles.Admin), "2025-12")).StatusCode.Should().Be(HttpStatusCode.OK, "the budget is per person");
    }
}
