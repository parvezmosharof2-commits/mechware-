namespace Mechware.Application.Invoicing;

/// <summary>A finished invoice PDF to send (its SHA-256 is the recorded one, checked against the stored bytes).</summary>
public sealed record InvoicePdfFile(byte[] Bytes, string FileName, string Sha256);

/// <summary>Staff answer to "is this PDF ours and unmodified?": the invoice and language whose stored file has this SHA-256.</summary>
public sealed record InvoiceDocumentCheckDto(Guid InvoiceId, string Number, string Kind, string Locale);

/// <summary>
/// The invoice PDF (ADR-0012 §12, E6.4b; D4.11 pattern). Rendered from the stored snapshot after COMMIT by the outbox handler, or lazily on the
/// first download; stored under a key built from the invoice id and locale (never from the request); key and SHA-256 recorded null to value once.
/// Every download recomputes the SHA-256 of the stored bytes; a mismatch is never sent (503 <c>INVOICE_PDF_UNAVAILABLE</c>, audit
/// <c>invoice.pdf_integrity_failed</c>). Whatever the caller may not see is one and the same <c>KeyNotFoundException</c>.
/// </summary>
public interface IInvoicePdfService
{
    /// <summary>The buyer's PDF: only for an order the caller may read (<c>IOrderService.GetAsync</c>) and an invoice of that order.</summary>
    Task<InvoicePdfFile> BuyerPdfAsync(Guid actorUserId, Guid orderId, Guid invoiceId, string? locale, CancellationToken ct = default);

    /// <summary>The company's PDF (E6.4d): the caller must be an effective Admin/Buyer member (<c>Credit:BalanceVisibleTo</c>) of the company and the invoice must be the company's.</summary>
    Task<InvoicePdfFile> CompanyPdfAsync(Guid actorUserId, Guid companyId, Guid invoiceId, string? locale, CancellationToken ct = default);

    /// <summary>Staff PDF of any invoice (the same stored, verified file).</summary>
    Task<InvoicePdfFile> StaffPdfAsync(Guid invoiceId, string? locale, CancellationToken ct = default);

    /// <summary>Which stored PDF has this SHA-256; anything else is a not-found.</summary>
    Task<InvoiceDocumentCheckDto> CheckAsync(string? sha256, CancellationToken ct = default);

    /// <summary>Outbox handler: renders and records both languages if missing. Idempotent; a missing invoice is skipped.</summary>
    Task RenderBothAsync(Guid invoiceId, CancellationToken ct = default);
}
