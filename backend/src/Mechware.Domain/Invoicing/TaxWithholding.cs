using System.Text.RegularExpressions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Credit;
using Mechware.Domain.Quotes;

namespace Mechware.Domain.Invoicing;

/// <summary>VDS = VAT deducted at source (Mushak-6.6, claimed in the 9.1); AIT = advance income tax deducted at source (not VAT, never in the 9.1).</summary>
public enum TaxWithholdingKind
{
    Vds = 1,
    Ait = 2
}

/// <summary>Recorded by Finance → Verified by another Finance/Admin (the ledger line follows, E6.6b) | Rejected; nothing moves after that.</summary>
public enum TaxWithholdingStatus
{
    Recorded = 1,
    Verified = 2,
    Rejected = 3
}

/// <summary>
/// What one tax invoice still has room for (ADR-0012 §11, threat model IT14, I8): VDS ≤ its VAT less the VAT its credit notes took, AIT ≤ its
/// value excl. VAT less the credited value; each less what earlier recorded or verified withholdings of that kind already took (rejected
/// ones count for nothing). The real AIT rate is consultant §19 Q11; this is only the upper bound.
/// </summary>
public readonly record struct WithholdingCaps(long NetMinor, long VatMinor, long CreditedNetMinor, long CreditedVatMinor, long WithheldVdsMinor, long WithheldAitMinor)
{
    public long Left(TaxWithholdingKind kind) => kind == TaxWithholdingKind.Vds
        ? VatMinor - CreditedVatMinor - WithheldVdsMinor
        : NetMinor - CreditedNetMinor - WithheldAitMinor;
}

/// <summary>
/// A VDS or AIT certificate a buyer company gave for one of our tax invoices (ADR-0012 §11, §16). The invoice itself never changes; a verified
/// withholding settles the order's receivable like a payment (<see cref="CreditLedgerKind.WithholdingApplied"/>). A certificate number is
/// unique per issuing company and kind (compared without spaces and punctuation); <c>TaxWithholdingSql</c> repeats the caps at COMMIT under
/// the invoice's lock and keeps the recorded facts fixed.
/// </summary>
public sealed partial class TaxWithholding : Entity<Guid>
{
    public const int MaxCertificateLength = 40;
    public const string ExceedsVat = "WITHHOLDING_EXCEEDS_VAT";
    public const string ExceedsNet = "WITHHOLDING_EXCEEDS_NET";
    public const string NeedsCompanyInvoice = "WITHHOLDING_NEEDS_COMPANY_INVOICE";
    public const string FourEyes = "WITHHOLDING_FOUR_EYES";
    public const string NotRecorded = "WITHHOLDING_NOT_RECORDED";

    public TaxWithholdingKind Kind { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid OrderId { get; private set; }
    /// <summary>The issuer of the certificate: the invoice's buyer company.</summary>
    public Guid CompanyId { get; private set; }
    /// <summary>As on the certificate: upper case, single spaces.</summary>
    public string CertificateNumber { get; private set; } = string.Empty;
    /// <summary>Letters and digits only: "VDS-2026/12" and "vds 2026 12" are one certificate.</summary>
    public string CertificateKey { get; private set; } = string.Empty;
    public DateOnly CertificateDate { get; private set; }
    public long AmountMinor { get; private set; }
    public TaxWithholdingStatus Status { get; private set; }
    public Guid RecordedBy { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionReason { get; private set; }

    private TaxWithholding() { }

    /// <summary>Finance types the certificate facts (no upload before C3/V5). <paramref name="caps"/> is read under the invoice's lock.</summary>
    public static TaxWithholding Record(Guid id, Invoice invoice, TaxWithholdingKind kind, string? certificateNumber, DateOnly certificateDate,
        long amountMinor, WithholdingCaps caps, Guid recordedBy, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (!Enum.IsDefined(kind)) throw new ValidationException("kind", "Choose VDS or AIT.");
        if (invoice.Kind != InvoiceKind.TaxInvoice || invoice.CompanyId is not { } companyId)
            throw new ConflictException(NeedsCompanyInvoice, "A withholding certificate belongs to a tax invoice of a company.");
        var (number, key) = NormaliseCertificate(certificateNumber);
        if (certificateDate < CreditCalendar.DhakaDate(invoice.IssuedAtUtc) || certificateDate > CreditCalendar.DhakaDate(nowUtc))
            throw new ValidationException("certificateDate", "The certificate date lies between the invoice date and today.");
        if (amountMinor < 1) throw new ValidationException("amountMinor", "The amount is at least 1 poisha.");
        if (amountMinor > caps.Left(kind))
            throw kind == TaxWithholdingKind.Vds
                ? new ConflictException(ExceedsVat, "VAT deducted at source cannot exceed the invoice's VAT left after credit notes and earlier certificates.")
                : new ConflictException(ExceedsNet, "Tax deducted at source cannot exceed the invoice's value left after credit notes and earlier certificates.");
        return new TaxWithholding
        {
            Id = id,
            Kind = kind,
            InvoiceId = invoice.Id,
            OrderId = invoice.OrderId,
            CompanyId = companyId,
            CertificateNumber = number,
            CertificateKey = key,
            CertificateDate = certificateDate,
            AmountMinor = amountMinor,
            Status = TaxWithholdingStatus.Recorded,
            RecordedBy = recordedBy,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    /// <summary>Upper case, trimmed, single spaces, 1–40 of <c>A-Z 0-9 space / -</c> starting with a letter or digit; the key keeps letters and digits.</summary>
    public static (string Number, string Key) NormaliseCertificate(string? raw)
    {
        var number = Spaces().Replace(raw?.Trim().ToUpperInvariant() ?? string.Empty, " ");
        if (!CertificatePattern().IsMatch(number))
            throw new ValidationException("certificateNumber", $"Type the certificate number as printed: 1 to {MaxCertificateLength} letters, digits, spaces, / or -.");
        return (number, NotAlphanumeric().Replace(number, string.Empty));
    }

    /// <summary>Four eyes (IT14): only a recorded certificate, never by the person who recorded it.</summary>
    public void Verify(Guid actor, DateTime nowUtc)
    {
        EnsureDecidableBy(actor);
        (Status, DecidedBy, DecidedAtUtc, UpdatedAtUtc) = (TaxWithholdingStatus.Verified, actor, nowUtc, nowUtc);
    }

    /// <summary>A refusal by another person with a 5–500 character reason; it frees the certificate number for a corrected entry.</summary>
    public void Reject(Guid actor, string? reason, DateTime nowUtc)
    {
        EnsureDecidableBy(actor);
        var text = PlainText.Clean(reason)?.Trim();
        if (text is not { Length: >= 5 and <= 500 }) throw new ValidationException("reason", "Give a reason of 5 to 500 characters.");
        (Status, DecidedBy, DecidedAtUtc, DecisionReason, UpdatedAtUtc) = (TaxWithholdingStatus.Rejected, actor, nowUtc, text, nowUtc);
    }

    private void EnsureDecidableBy(Guid actor)
    {
        if (Status != TaxWithholdingStatus.Recorded) throw new ConflictException(NotRecorded, "This certificate is already verified or rejected.");
        if (actor == RecordedBy) throw new ConflictException(FourEyes, "Another person verifies or rejects a certificate you recorded.");
    }

    /// <summary>The one ledger line of a verified certificate: it lowers the order's receivable like a payment (key <c>withholding:{id}</c>).</summary>
    public CreditPosting SettlementPosting()
    {
        if (Status != TaxWithholdingStatus.Verified || DecidedAtUtc is not { } at)
            throw new InvalidOperationException("Only a verified certificate settles the receivable.");
        return new CreditPosting(CreditLedgerKind.WithholdingApplied, -AmountMinor, 0, CreditLedgerKeys.Withholding(Id), CreditCalendar.DhakaDate(at),
            OrderId: OrderId, ActorUserId: DecidedBy, WithholdingId: Id);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9 /-]{0,39}$")]
    private static partial Regex CertificatePattern();

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NotAlphanumeric();
}
