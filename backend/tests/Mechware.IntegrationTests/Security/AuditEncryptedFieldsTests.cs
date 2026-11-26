using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Invoicing;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Security;

/// <summary>
/// A2.x (threat model v1, H4): every field-encrypted property is saved through the real change interceptor, and the raw
/// <c>audit_logs</c> JSON is then searched for its plain value (and for any ciphertext copy). One case per property of the
/// reviewed whitelist in <c>Mechware.UnitTests/Audit/AuditEncryptedFieldsTests.cs</c>, which fails when a case is missing.
/// After the writes the hash chain still seals and verifies.
/// </summary>
public sealed class AuditEncryptedFieldsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 11, 17, 4, 0, 0, DateTimeKind.Utc);
    private static readonly QuoteOptions Options = new();
    private static readonly MarginFloorRow[] ZeroFloor = [new(MarginFloorScope.Global, null, null, 0m)];
    /// <summary>Random digits, so no plain value is a substring of another value in the log.</summary>
    private static string NextDigits(int length) => string.Concat(Enumerable.Range(0, length).Select(i => (char)('0' + (i == 0 ? 1 + Random.Shared.Next(9) : Random.Shared.Next(10)))));

    private sealed record Written(string Table, Guid EntityId, string[] Plain, string Key);

    [PostgresTheory]
    [InlineData("Company.Bin")]
    [InlineData("Company.Tin")]
    [InlineData("Company.TradeLicenceNo")]
    [InlineData("QuoteVersion.CompanySnapshot")]
    [InlineData("Invoice.Buyer")]
    [InlineData("User.TwoFactorSecret")]
    [InlineData("User.TwoFactorRecoveryCodesJson")]
    public async Task SavingAnEncryptedProperty_LeavesNoPlainValueInTheAuditLog_AndTheChainVerifies(string property)
    {
        var written = property switch
        {
            "Company.Bin" or "Company.Tin" or "Company.TradeLicenceNo" => await CompanyAsync(property),
            "QuoteVersion.CompanySnapshot" => await QuoteAsync(),
            "Invoice.Buyer" => await InvoiceAsync(),
            "User.TwoFactorSecret" or "User.TwoFactorRecoveryCodesJson" => await TwoFactorAsync(property),
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "no scenario"),
        };

        // The whole table, raw: no row of any entity may hold the plain value.
        foreach (string plain in written.Plain)
            (await CountAsync("strpos(coalesce(old_values::text, '') || coalesce(new_values::text, ''), @p) > 0", plain))
                .Should().Be(0, $"{property}: the plain value '{plain}' is encrypted at rest and must not be copied into audit_logs");

        // Not vacuous: the entity's rows exist, carry the property, and hold no ciphertext copy either.
        var rows = await RowsAsync(written.Table, written.EntityId);
        rows.Should().NotBeEmpty();
        rows.Should().Contain(r => r.Contains($"\"{written.Key}\":", StringComparison.Ordinal), $"{property} is audited (masked)");
        rows.Should().AllSatisfy(r => r.Should().NotContain(FieldProtector.Prefix, "no ciphertext copy: a key rotation could not re-encrypt it"));

        await using var db = fixture.CreateContext();
        var chain = new AuditChain(db);
        (await chain.SealAsync(10_000, TimeSpan.Zero)).Should().BeGreaterThan(0);
        var status = await chain.VerifyAsync();
        status.FirstBrokenPosition.Should().BeNull("the masked rows seal into an intact chain");
        status.Sealed.Should().BeGreaterThan(0);
    }

    [PostgresFact]
    public async Task TheQuoteSnapshot_KeepsItsShape_WithOnlyTheBinMasked()
    {
        var written = await QuoteAsync();
        var rows = await RowsAsync("quote_versions", written.EntityId);
        rows.Should().Contain(r => r.Contains(
            "\"CompanySnapshot\": {\"bin\": \"[encrypted]\", \"name\": \"A2X Quote Buyer Ltd\", \"billingAddress\": \"12 Tejgaon I/A, Dhaka 1208\"}",
            StringComparison.Ordinal));
    }

    // ---- scenarios (every save goes through the audit interceptor) ---------------------------------------------------------

    private MechwareDbContext Audited(Guid? actor = null) => new(new DbContextOptionsBuilder<MechwareDbContext>()
        .UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor(), new AuditLogSaveChangesInterceptor(new FixedUser(actor ?? Guid.NewGuid())))
        .Options);

    /// <summary>Created, all three identifiers changed (old and new values), then a second company deleted (old values).</summary>
    private async Task<Written> CompanyAsync(string property)
    {
        string bin = NextDigits(13), tin = NextDigits(12), licence = $"TL-A2X-{NextDigits(8)}";
        string bin2 = NextDigits(13), tin2 = NextDigits(12), licence2 = $"TL-A2X-{NextDigits(8)}";
        var company = Company.Register(Guid.NewGuid(), "A2X Mills Ltd", licence, bin, tin, null);
        var doomed = Company.Register(Guid.NewGuid(), "A2X Gone Ltd", $"TL-A2X-{NextDigits(8)}", NextDigits(13), NextDigits(12), null);
        await using (var db = Audited())
        {
            db.Companies.AddRange(company, doomed);
            await db.SaveChangesAsync();
        }

        await using (var db = Audited())
        {
            var stored = await db.Companies.SingleAsync(c => c.Id == company.Id);
            var entry = db.Entry(stored);
            entry.Property(c => c.Bin).CurrentValue = bin2;
            entry.Property(c => c.Tin).CurrentValue = tin2;
            entry.Property(c => c.TradeLicenceNo).CurrentValue = licence2;
            db.Companies.Remove(await db.Companies.SingleAsync(c => c.Id == doomed.Id));
            await db.SaveChangesAsync();
        }

        (await RowsAsync("companies", doomed.Id)).Should().Contain(r => r.Contains("A2X Gone Ltd"), "the delete wrote its old values");
        string key = property["Company.".Length..];
        return new Written("companies", company.Id, [bin, tin, licence, bin2, tin2, licence2, doomed.Bin, doomed.Tin!, doomed.TradeLicenceNo], key);
    }

    /// <summary>A Draft (no snapshot yet) then sent: the send writes the company snapshot with the buyer BIN.</summary>
    private async Task<Written> QuoteAsync()
    {
        string bin = NextDigits(13);
        Guid companyId, contact = await TestUsers.CreateAsync(fixture), variant, seller;
        await using (var db = fixture.CreateContext())
        {
            var company = Company.Register(Guid.NewGuid(), "A2X Quote Buyer Ltd", $"TL-A2X-{NextDigits(8)}", bin, null, null);
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyId = company.Id;
            var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
            variant = seeded.VariantId;
            seller = await db.Products.Where(p => p.Id == seeded.ProductId).Select(p => p.SellerId).SingleAsync();
        }

        var author = new QuoteActor(Guid.NewGuid(), []);
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), companyId, null, contact, author, Options, Now);
        var facts = new QuoteLineFacts(variant, true, seller, $"SKU-{variant.ToString("N")[..6]}", "6ES7214-1AG40", "Siemens S7-1200 CPU",
            "সিমেন্স এস৭-১২০০ সিপিইউ", 0.15m, 120_000, null);
        quote.EditDraft(1, new QuoteDraft(15, QuotePaymentTerms.AsAccount, QuoteShippingMode.Calculated, null, null, "Prices in BDT.", "আলোচনা অনুযায়ী।",
            "Margin is thin.", [new QuoteLineDraft(variant, 10, 100_000, facts, "Genuine part")], null), author, Options, Now);
        await using (var db = Audited())
        {
            db.Add(quote);
            await db.SaveChangesAsync();
        }

        Guid versionId;
        await using (var db = Audited())
        {
            var stored = await db.Quotes.Include(q => q.Versions).ThenInclude(v => v.Lines).Include(q => q.Versions).ThenInclude(v => v.Contributors)
                .AsSplitQuery().SingleAsync(q => q.Id == quote.Id);
            var v = stored.Version(1);
            var check = QuoteApprovalPolicy.Evaluate(v, v.Lines.Select(l => new VariantCostFacts(l.VariantId, 1, null, [], 0.15m)).ToList(), ZeroFloor,
                v.Lines.Select(l => new PriceResolution(l.VariantId, l.UnitPriceMinor, PriceSource.Contract, PriceBasis.NetOfVat, null, null, null, null,
                    l.UnitPriceMinor)).ToList(), Options, false);
            stored.Send(1, new QuoteIssue(new QuoteCompanySnapshot("A2X Quote Buyer Ltd", bin, "12 Tejgaon I/A, Dhaka 1208"), "Rahim Uddin",
                [facts], check, v.ShipTo), new QuoteActor(Guid.NewGuid(), []), false, Options, Now);
            await db.SaveChangesAsync();
            versionId = v.Id;
        }

        (await ScalarAsync($"SELECT company_snapshot->>'bin' FROM quote_versions WHERE id = '{versionId}'"))
            .Should().StartWith(FieldProtector.Prefix, "the column itself holds the ciphertext");
        return new Written("quote_versions", versionId, [bin], nameof(QuoteVersion.CompanySnapshot));
    }

    /// <summary>A Live 6.3 for a company buyer: the buyer snapshot carries the company BIN.</summary>
    private async Task<Written> InvoiceAsync()
    {
        await using var db = Audited();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        var buyer = await InvoiceTestIssues.ActiveCompanyAsync(db);
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, buyer);
        var taken = await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, InvoiceSeries.TaxInvoice);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller, taken, buyer);
        invoice.Buyer.Bin.Should().Be(buyer.Bin);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new Written("invoices", invoice.Id, [buyer.Bin, buyer.TradeLicenceNo], nameof(Invoice.Buyer));
    }

    /// <summary>The entity holds these as ciphertext already: neither the plain secret nor the ciphertext reaches the log.</summary>
    private async Task<Written> TwoFactorAsync(string property)
    {
        var user = User.Create($"a2x-{Guid.NewGuid():N}@example.test", null, null, "A2X Staff");
        await using (var db = Audited())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        string secret = $"JBSWY3DPEHPK{NextDigits(8)}", codes = $"[\"recovery-hash-{NextDigits(8)}\"]";
        await using (var db = Audited(user.Id))
        {
            var stored = await db.Users.SingleAsync(u => u.Id == user.Id);
            stored.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, secret), TwoFactorSecrets.SealRecoveryCodes(user.Id, codes), Now);
            await db.SaveChangesAsync();
        }

        return new Written("users", user.Id, [secret, codes[2..^2]], property["User.".Length..]);
    }

    // ---- raw reads -----------------------------------------------------------------------------------------------------------

    private async Task<List<string>> RowsAsync(string table, Guid entityId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT coalesce(old_values::text, '') || ' ' || coalesce(new_values::text, '') FROM audit_logs WHERE entity_name = @t AND entity_id = @id",
            connection);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("id", entityId.ToString());
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    private async Task<long> CountAsync(string where, string p)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM audit_logs WHERE {where}", connection);
        cmd.Parameters.AddWithValue("p", p);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.7";
        public string? UserAgent => null;
        public string? CorrelationId => null;
        public bool HasPermission(string permission) => false;
    }
}
