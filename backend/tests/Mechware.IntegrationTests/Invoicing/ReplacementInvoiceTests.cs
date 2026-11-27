using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.5c, ADR-0012 §9.4: a wrong invoice (here: the buyer's BIN) is corrected by a FULL credit note with reason Error whose approval, by another
/// person, also issues the replacement 6.3 in the same transaction: new number, the buyer read again, the same lines, <c>replacement_no</c> + 1,
/// <c>replaces_invoice_id</c> = the old one. Anything short of a full credit is refused by the service and, at COMMIT, by the database.
/// </summary>
public class ReplacementInvoiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.66";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-e65c";
        public bool HasPermission(string permission) => true;
    }

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private async Task<T> As<T>(Guid actor, Guid seller, Func<CreditNoteService, Task<T>> act)
    {
        await using var db = fixture.CreateContext();
        var staff = new Staff(actor);
        return await act(new CreditNoteService(db, Allocator(db), new CreditLedgerService(db, staff, TimeProvider.System), InvoiceTestIssues.Options(seller),
            staff, TimeProvider.System));
    }

    private async Task<Guid> UserAsync()
    {
        await using var db = fixture.CreateContext();
        return await InvoiceTestIssues.UserAsync(db);
    }

    private async Task<long?> TaxCounterAsync(Guid seller)
    {
        await using var db = fixture.CreateContext();
        return await db.InvoiceNumberCounters.AsNoTracking().Where(c => c.SellerId == seller && c.Series == InvoiceSeries.TaxInvoice)
            .Select(c => (long?)c.NextSerial).SingleOrDefaultAsync();
    }

    private static RequestCreditNoteRequest Replace(IReadOnlyList<CreditNoteLineInput>? lines = null, string reason = "error") =>
        new(reason, "Wrong buyer BIN printed on the invoice", lines, true);

    [PostgresFact]
    public async Task AReplacementInvoice_NeedsTheOldOneFullyCredited()
    {
        Guid seller, companyId;
        Invoice original;
        await using (var db = fixture.CreateContext())
        {
            seller = await InvoiceTestIssues.SellerAsync(db);
            var company = await InvoiceTestIssues.ActiveCompanyAsync(db);
            companyId = company.Id;
            await using var tx = await db.Database.BeginTransactionAsync();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
            original = InvoiceTestIssues.Build(order, shipment, seller, await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
            db.Invoices.Add(original);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var (asker, approver) = (await UserAsync(), await UserAsync());
        var plc = original.Lines.Single(l => l.Sku == "PLC-1200").LineNo;
        var key = () => "rep-" + Guid.NewGuid().ToString("N");

        // Service: only reason error, and only a full credit.
        await Assert.ThrowsAsync<ValidationException>(() => As(asker, seller, s => s.RequestAsync(original.Id, Replace(reason: "goods_returned"), key())));
        (await Assert.ThrowsAsync<ConflictException>(() => As(asker, seller, s => s.RequestAsync(original.Id, Replace([new(plc, 1)]), key()))))
            .Code.Should().Be(CreditNoteService.NeedsFullCredit);

        // An earlier partial credit note counts: the replacement request then credits all that is left (no lines = everything left).
        var partial = await As(asker, seller, s => s.RequestAsync(original.Id, new("price_reduction", "Agreed discount after delivery", [new(plc, 0, 50_000)]), key()));
        await As(approver, seller, s => s.ApproveAsync(partial.Request.Id));
        var asked = (await As(asker, seller, s => s.RequestAsync(original.Id, Replace(), key()))).Request;
        asked.Replace.Should().BeTrue();
        asked.TotalMinor.Should().Be(original.TotalMinor - 50_000 - (long)decimal.Round(50_000 * 0.15m, 0, MidpointRounding.ToEven));

        // The company's BIN is fixed before the approval; the replacement reads it, the old invoice keeps what it printed.
        var newBin = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (char)('0' + b % 10)))[..13];
        await using (var db = fixture.CreateContext())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == companyId);
            db.Entry(company).Property(c => c.Bin).CurrentValue = newBin;
            await db.SaveChangesAsync();
        }
        (await Assert.ThrowsAsync<ConflictException>(() => As(asker, seller, s => s.ApproveAsync(asked.Id)))).Code.Should().Be(CreditNoteRequest.FourEyes);
        (await TaxCounterAsync(seller)).Should().Be(2, "a refused approval takes no tax-invoice number");

        var approved = await As(approver, seller, s => s.ApproveAsync(asked.Id));
        approved.ReplacementNumber.Should().EndWith("-000002").And.Contain("-INV-");
        (await TaxCounterAsync(seller)).Should().Be(3);
        await using (var db = fixture.CreateContext())
        {
            var replacement = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.Id == approved.ReplacementInvoiceId);
            (replacement.Kind, replacement.ReplacementNo, replacement.ShipmentId, replacement.IssuedBy, replacement.TotalMinor, replacement.Buyer.Bin)
                .Should().Be((InvoiceKind.TaxInvoice, 1, original.ShipmentId, approver, original.TotalMinor, newBin));
            (await db.Invoices.Where(i => i.Id == replacement.Id).Select(i => EF.Property<Guid?>(i, "ReplacesInvoiceId")).SingleAsync()).Should().Be(original.Id);
            replacement.Lines.Select(l => (l.LineNo, l.Kind, l.OrderItemId, l.Quantity, l.NetMinor, l.VatMinor, l.TotalMinor)).Should().Equal(
                original.Lines.OrderBy(l => l.LineNo).Select(l => (l.LineNo, l.Kind, l.OrderItemId, l.Quantity, l.NetMinor, l.VatMinor, l.TotalMinor)));
            replacement.ContentHash.Should().Be(InvoiceContentHash.Compute(replacement));
            (await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == original.Id)).Buyer.Bin.Should().Be(original.Buyer.Bin);
            (await db.AuditLogs.CountAsync(a => a.Action == "invoice.replaced" && a.EntityId == replacement.Id.ToString())).Should().Be(1);
            (await db.Invoices.Where(c => c.OriginalInvoiceId == original.Id).SumAsync(c => c.TotalMinor)).Should().Be(original.TotalMinor);
        }

        // Database: a replacement of the replacement, which nobody credited, fails at COMMIT (and so would one of a partly credited 6.3).
        await using (var db = fixture.CreateContext())
        {
            var previous = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.Id == approved.ReplacementInvoiceId);
            await using var tx = await db.Database.BeginTransactionAsync();
            var taken = await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice);
            var second = Invoice.Replacement(Guid.NewGuid(), previous, previous.Buyer, taken.Series, taken.FiscalYear, taken.Serial, taken.IssuedAtUtc,
                approver, false);
            db.Invoices.Add(second);
            db.Entry(second).Property("ReplacesInvoiceId").CurrentValue = previous.Id;
            await db.SaveChangesAsync();
            var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            (ex.SqlState, ex.MessageText).Should().Match<(string, string)>(x =>
                x.Item1 == PostgresErrorCodes.IntegrityConstraintViolation && x.Item2.Contains("before it is fully credited"));
        }
        (await TaxCounterAsync(seller)).Should().Be(3, "the refused replacement left no gap");
    }
}
