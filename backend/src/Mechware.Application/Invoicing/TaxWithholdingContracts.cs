using Mechware.Application.Catalog;

namespace Mechware.Application.Invoicing;

/// <summary>Body of <c>POST admin/invoices/{id}/withholdings</c>: kind <c>vds | ait</c>, the certificate number as printed, its date (Dhaka) and the amount in poisha.</summary>
public sealed record RecordWithholdingRequest(string? Kind, string? CertificateNumber, DateOnly? CertificateDate, long AmountMinor);

/// <summary>Body of a rejection: a reason of 5–500 characters.</summary>
public sealed record RejectWithholdingRequest(string? Reason);

/// <summary>Body of <c>PUT admin/companies/{id}/withholding-entity</c>.</summary>
public sealed record SetWithholdingEntityRequest(bool? WithholdingEntity);

/// <summary>A VDS/AIT certificate. <paramref name="Kind"/>: vds, ait. <paramref name="Status"/>: recorded, verified, rejected.</summary>
public sealed record TaxWithholdingDto(Guid Id, string Kind, Guid InvoiceId, string InvoiceNumber, Guid OrderId, Guid CompanyId, string CompanyName,
    string CertificateNumber, DateOnly CertificateDate, long AmountMinor, string Status, Guid RecordedBy, DateTime RecordedAtUtc, Guid? DecidedBy,
    DateTime? DecidedAtUtc, string? DecisionReason);

public sealed record RecordedWithholding(TaxWithholdingDto Withholding, bool Replayed);

public sealed record CompanyWithholdingEntityDto(Guid CompanyId, bool WithholdingEntity);

/// <summary>
/// E6.6b, ADR-0012 §11, §14, §15 (threat model IT14). Finance records a certificate against a tax invoice of a credit order (Idempotency-Key); ANOTHER
/// Finance/Admin verifies it (step-up at the endpoint), which posts the one ledger line <c>withholding_applied</c> in the same transaction, or rejects it
/// with a reason. Record and verify lock <c>orders</c> → <c>invoices</c> (FOR NO KEY UPDATE) first, so the caps read the credit notes committed before.
/// 404 unknown id; 409 <c>WITHHOLDING_EXCEEDS_VAT | _NET</c>, <c>WITHHOLDING_NEEDS_COMPANY_INVOICE</c>, <c>WITHHOLDING_NEEDS_CREDIT_ORDER</c>,
/// <c>WITHHOLDING_CERTIFICATE_DUPLICATE</c>, <c>WITHHOLDING_FOUR_EYES</c>, <c>WITHHOLDING_NOT_RECORDED</c>, <c>IDEMPOTENCY_KEY_REUSED</c>, the ledger's codes.
/// </summary>
public interface ITaxWithholdingService
{
    public const int MaxPageSize = 50;

    /// <summary>Same person + same key + same facts = the first entry (Replayed); other facts under the key = 409.</summary>
    Task<RecordedWithholding> RecordAsync(Guid invoiceId, RecordWithholdingRequest request, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>Oldest first; <c>status</c> recorded (default), verified or rejected; optional <c>kind</c> and invoice.</summary>
    Task<PagedResult<TaxWithholdingDto>> ListAsync(string? status, string? kind, Guid? invoiceId, int page, int pageSize, CancellationToken ct = default);

    Task<TaxWithholdingDto> GetAsync(Guid withholdingId, CancellationToken ct = default);

    /// <summary>The same verifier again answers the verified entry without a second ledger line (the ledger key <c>withholding:{id}</c>).</summary>
    Task<TaxWithholdingDto> VerifyAsync(Guid withholdingId, CancellationToken ct = default);

    Task<TaxWithholdingDto> RejectAsync(Guid withholdingId, RejectWithholdingRequest request, CancellationToken ct = default);

    /// <summary>Admin, step-up at the endpoint, audited when it changes. Drives only the PDF/payment hint.</summary>
    Task<CompanyWithholdingEntityDto> SetWithholdingEntityAsync(Guid companyId, SetWithholdingEntityRequest request, CancellationToken ct = default);
}
