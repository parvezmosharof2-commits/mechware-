using Mechware.Domain.Quotes;

namespace Mechware.Application.Quotes;

/// <summary>
/// Letterhead facts and terms printed on the quote PDF (ADR-0011 §10, docs/COMPANY_FACTS.md), configuration section <c>QuotePdf</c>.
/// The defaults are the facts from the owner's own quotation pad; the items the owner has not given (BIN, TIN, trade licence, full address)
/// are empty and the PDF leaves an empty field out. <c>{validUntil}</c> in a terms line becomes the version's real last valid date.
/// </summary>
public sealed record QuotePdfOptions
{
    public const string Section = "QuotePdf";

    public string CompanyName { get; init; } = "MECHWARE";
    public string Tagline { get; init; } = "YOUR GATEWAY TO INDUSTRY 4.0";
    public string FooterLine { get; init; } = "MECHWARE · TECHNOLOGY FOR SMARTER SYSTEMS";
    public string Office { get; init; } = "Sha-23/E (6th Floor)";
    public string Address { get; init; } = "";
    public string Phone { get; init; } = "+88 01748-305931";
    public string Email { get; init; } = "parvez.mechware@gmail.com";
    public string Bin { get; init; } = "";
    public string Tin { get; init; } = "";
    public string TradeLicence { get; init; } = "";
    public string SignatoryName { get; init; } = "Md. Parvez Mosharaf";
    public string SignatoryTitle { get; init; } = "Managing Director";
    public string Partners { get; init; } = "Siemens · Mitsubishi Electric · Delta · Omron";

    /// <summary>A PNG or JPEG file for the letterhead; empty or missing means no logo. The repository does not carry the owner's logo files.</summary>
    public string LogoPath { get; init; } = "";

    public IReadOnlyList<string> TermsEn { get; init; } =
    [
        "Offer validity: this quotation is valid until {validUntil}.",
        "VAT: prices include VAT as shown.",
        "Payment: 70% advance, 30% on completion or as agreed.",
        "Delivery: 7 working days after the advance is received.",
        "Warranty: 365 days on products, 1825 days on software.",
        "Food and accommodation for site work are the customer's scope.",
    ];

    public IReadOnlyList<string> TermsBn { get; init; } =
    [
        "প্রস্তাবের মেয়াদ: এই কোটেশন {validUntil} পর্যন্ত বৈধ।",
        "ভ্যাট: মূল্যে ভ্যাট অন্তর্ভুক্ত, যেমন দেখানো হয়েছে।",
        "পেমেন্ট: ৭০% অগ্রিম, ৩০% কাজ শেষে বা সম্মত অনুযায়ী।",
        "ডেলিভারি: অগ্রিম পাওয়ার পর ৭ কর্মদিবসের মধ্যে।",
        "ওয়ারেন্টি: পণ্যে ৩৬৫ দিন, সফটওয়্যারে ১৮২৫ দিন।",
        "সাইটের কাজের জন্য খাবার ও থাকার ব্যবস্থা গ্রাহকের দায়িত্ব।",
    ];
}

/// <summary>A finished PDF to send. <paramref name="Preview"/>: a draft preview, never stored and not an offer.</summary>
public sealed record QuotePdfFile(byte[] Bytes, string FileName, string Sha256, bool Preview = false);

/// <summary>Staff answer to "is this PDF ours and unmodified?": the quote, version and language whose stored file has this SHA-256.</summary>
public sealed record QuoteDocumentCheckDto(Guid QuoteId, string Number, int VersionNo, string Locale);

/// <summary>One line as the PDF prints it, from the frozen snapshot of the send.</summary>
public sealed record QuotePdfLine(int LineNo, string? PartNumber, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate, long LineTotalMinor);

/// <summary>
/// Everything the PDF prints about one version, taken from the version row and its lines only (threat model QT9): the buyer company is the
/// snapshot of the send, never the live company; titles, prices and VAT are the lines' own. A draft (<see cref="Draft"/>) has no snapshot yet and
/// prints the live company name as a preview.
/// </summary>
public sealed record QuotePdfModel(string Locale, bool Draft, string Number, int VersionNo, DateOnly? IssuedOn, DateOnly? ValidUntil, int ValidityDays,
    string CompanyName, string? CompanyBin, string? CompanyAddress, QuoteShipTo? ShipTo, string PaymentTerms, string ShippingMode,
    IReadOnlyList<QuotePdfLine> Lines, long GoodsNetMinor, long TaxMinor, long GoodsTotalMinor, long? ShippingTotalMinor, long? GrandTotalMinor,
    string? TermsText, string? BuyerNote, string? PreparerName, string? ContentHash, QuoteTermsSnapshot? Terms = null);

/// <summary>
/// The quote PDF (ADR-0011 §10, D4.11). Rendered lazily on the first request, stored under a key the server builds from ids, and verified against the
/// recorded SHA-256 on every download; a file that does not match is never sent (503 <c>QUOTE_PDF_UNAVAILABLE</c>, audit <c>quote.pdf_integrity_failed</c>).
/// Whatever the caller may not see is the same <c>KeyNotFoundException</c> as the buyer reads (D4.10).
/// </summary>
public interface IQuotePdfService
{
    /// <summary>The buyer's PDF of a version they may see (the rules of <see cref="IBuyerQuoteService.GetAsync"/>). <paramref name="locale"/>: <c>en</c> or <c>bn</c>, default <c>en</c>.</summary>
    Task<QuotePdfFile> BuyerPdfAsync(Guid actorUserId, Guid companyId, Guid quoteId, int versionNo, string? locale, CancellationToken ct = default);

    /// <summary>Staff PDF of any version: stored and verified once sent; a draft or a version waiting for approval is a preview that is never stored.</summary>
    Task<QuotePdfFile> StaffPdfAsync(Guid quoteId, int versionNo, string? locale, CancellationToken ct = default);

    /// <summary>Which stored PDF has this SHA-256; anything else is a not-found.</summary>
    Task<QuoteDocumentCheckDto> CheckAsync(string? sha256, CancellationToken ct = default);
}
