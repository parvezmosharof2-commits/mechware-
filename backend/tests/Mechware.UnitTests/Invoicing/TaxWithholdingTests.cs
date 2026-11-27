using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Invoicing;

namespace Mechware.UnitTests.Invoicing;

/// <summary>E6.6a, threat model IT14, ADR-0012 §11: the VDS/AIT caps, the certificate number rules, four eyes and the one ledger line.</summary>
public class TaxWithholdingTests
{
    private static readonly Guid Recorder = Guid.Parse("4ec0d000-0000-4000-8000-000000000001");
    private static readonly Guid Checker = Guid.Parse("c7ec0000-0000-4000-8000-000000000002");
    private static readonly DateOnly InvoiceDay = new(2026, 11, 21);
    private static readonly DateTime Later = InvoiceFixtures.Now.AddDays(10);

    private static Invoice CompanyInvoice() => InvoiceFixtures.CompanyInvoice().Invoice;

    private static WithholdingCaps Fresh(Invoice i) => new(i.NetMinor, i.VatMinor, 0, 0, 0, 0);

    private static TaxWithholding Record(TaxWithholdingKind kind, long amount, WithholdingCaps? caps = null, string number = "VDS-1", DateOnly? date = null)
    {
        var invoice = CompanyInvoice();
        return TaxWithholding.Record(Guid.NewGuid(), invoice, kind, number, date ?? InvoiceDay, amount, caps ?? Fresh(invoice), Recorder, Later);
    }

    [Fact]
    public void TheCaps_LeaveOutCreditNotesAndEarlierCertificates_PerKind()
    {
        var caps = new WithholdingCaps(NetMinor: 1_000_000, VatMinor: 150_000, CreditedNetMinor: 200_000, CreditedVatMinor: 30_000, WithheldVdsMinor: 20_000,
            WithheldAitMinor: 50_000);
        caps.Left(TaxWithholdingKind.Vds).Should().Be(100_000, "VAT 150 000 − credited VAT 30 000 − VDS 20 000");
        caps.Left(TaxWithholdingKind.Ait).Should().Be(750_000, "net 1 000 000 − credited net 200 000 − AIT 50 000");
    }

    [Theory]
    [InlineData(TaxWithholdingKind.Vds, TaxWithholding.ExceedsVat)]
    [InlineData(TaxWithholdingKind.Ait, TaxWithholding.ExceedsNet)]
    public void Record_TakesExactlyWhatIsLeft_AndRefusesOnePoishaMore(TaxWithholdingKind kind, string code)
    {
        var invoice = CompanyInvoice();
        var caps = new WithholdingCaps(invoice.NetMinor, invoice.VatMinor, 1_000, 100, 0, 0);
        var left = caps.Left(kind);
        Record(kind, left, caps).AmountMinor.Should().Be(left);
        var over = () => Record(kind, left + 1, caps);
        over.Should().Throw<ConflictException>().Which.Code.Should().Be(code);
        var zero = () => Record(kind, 0, caps);
        zero.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Record_NeedsATaxInvoiceOfACompany()
    {
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("SNS-1", 3, 10_000, 0.15m)], 5_000, null, out var shipment);
        var personal = InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, buyerName: "Nusrat Jahan"), InvoiceFixtures.Live);
        var act = () => TaxWithholding.Record(Guid.NewGuid(), personal, TaxWithholdingKind.Vds, "VDS-1", InvoiceDay, 1, Fresh(personal), Recorder, Later);
        act.Should().Throw<ConflictException>().Which.Code.Should().Be(TaxWithholding.NeedsCompanyInvoice);
    }

    [Theory]
    [InlineData("VDS-2026/0012", "VDS-2026/0012", "VDS20260012")]
    [InlineData("  vds   2026 0012 ", "VDS 2026 0012", "VDS20260012")]
    [InlineData("6.6-77", null, null)]
    [InlineData("", null, null)]
    [InlineData("-12", null, null)]
    [InlineData("=HYPERLINK(1)", null, null)]
    [InlineData("A12345678901234567890123456789012345678901", null, null)]
    public void CertificateNumbers_AreKeptAsPrinted_AndComparedByLettersAndDigits(string raw, string? number, string? key)
    {
        var act = () => TaxWithholding.NormaliseCertificate(raw);
        if (number is null) act.Should().Throw<ValidationException>();
        else act().Should().Be((number, key!));
    }

    [Fact]
    public void TheCertificateDate_LiesBetweenTheInvoiceDayAndToday_InDhaka()
    {
        Record(TaxWithholdingKind.Vds, 1, date: InvoiceDay).CertificateDate.Should().Be(InvoiceDay);
        Record(TaxWithholdingKind.Vds, 1, date: CreditCalendar.DhakaDate(Later)).Should().NotBeNull();
        var early = () => Record(TaxWithholdingKind.Vds, 1, date: InvoiceDay.AddDays(-1));
        var future = () => Record(TaxWithholdingKind.Vds, 1, date: CreditCalendar.DhakaDate(Later).AddDays(1));
        early.Should().Throw<ValidationException>();
        future.Should().Throw<ValidationException>();
    }

    [Fact]
    public void VerifyOrReject_ByTheRecorder_IsRefused_AndACertificateMovesOnce()
    {
        var w = Record(TaxWithholdingKind.Vds, 500);
        var self = () => w.Verify(Recorder, Later);
        self.Should().Throw<ConflictException>().Which.Code.Should().Be(TaxWithholding.FourEyes);
        var selfReject = () => w.Reject(Recorder, "Wrong certificate", Later);
        selfReject.Should().Throw<ConflictException>().Which.Code.Should().Be(TaxWithholding.FourEyes);
        var noReason = () => w.Reject(Checker, " no ", Later);
        noReason.Should().Throw<ValidationException>();

        w.Verify(Checker, Later);
        (w.Status, w.DecidedBy, w.DecidedAtUtc).Should().Be((TaxWithholdingStatus.Verified, Checker, Later));
        var again = () => w.Reject(Checker, "Wrong certificate", Later);
        again.Should().Throw<ConflictException>().Which.Code.Should().Be(TaxWithholding.NotRecorded);
    }

    [Fact]
    public void AVerifiedCertificate_PostsOneWithholdingLine_ThatSettlesLikeAPayment()
    {
        var w = Record(TaxWithholdingKind.Vds, 500);
        var early = () => w.SettlementPosting();
        early.Should().Throw<InvalidOperationException>("only a verified certificate settles");
        w.Verify(Checker, Later);

        var p = w.SettlementPosting();
        (p.Kind, p.ReceivableDeltaMinor, p.OnAccountDeltaMinor, p.OrderId, p.WithholdingId, p.IdempotencyKey, p.OccurredOn).Should().Be(
            (CreditLedgerKind.WithholdingApplied, -500L, 0L, (Guid?)w.OrderId, (Guid?)w.Id, $"withholding:{w.Id:N}", CreditCalendar.DhakaDate(Later)));
        var valid = () => CreditLedgerEntry.Validate(p);
        valid.Should().NotThrow();
        foreach (var bad in new[] { p with { WithholdingId = null }, p with { PaymentId = Guid.NewGuid() }, p with { ReceivableDeltaMinor = 500 },
                     p with { Kind = CreditLedgerKind.PaymentApplied, PaymentId = Guid.NewGuid() } })
        {
            var act = () => CreditLedgerEntry.Validate(bad);
            act.Should().Throw<ValidationException>();
        }
    }
}
