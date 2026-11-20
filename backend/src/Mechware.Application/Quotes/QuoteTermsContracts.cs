using Mechware.Domain.Quotes;

namespace Mechware.Application.Quotes;

/// <summary>The default terms lines printed on every quote PDF (D4.20). <c>{validUntil}</c> becomes the version's real last valid date.</summary>
public sealed record QuoteTermsDto(IReadOnlyList<string> TermsEn, IReadOnlyList<string> TermsBn, uint RowVersion, DateTime? UpdatedAtUtc, Guid? UpdatedBy, string? UpdatedByName);

/// <summary>Replace both lists. <c>RowVersion</c> is from the last read; a stale one is 409 <c>QUOTE_TERMS_CHANGED</c>. At most 12 lines of 1 to 300 characters each.</summary>
public sealed record UpdateQuoteTermsRequest(IReadOnlyList<string?>? TermsEn, IReadOnlyList<string?>? TermsBn, uint? RowVersion);

/// <summary>Staff-editable default quote terms (D4.20). A version freezes a copy when it leaves Draft, so a change never reaches a quote already sent.</summary>
public interface IQuoteTermsService
{
    Task<QuoteTermsDto> GetAsync(CancellationToken ct = default);

    /// <summary>Audited as <c>quote.terms_changed</c> with the old and the new lines.</summary>
    Task<QuoteTermsDto> UpdateAsync(UpdateQuoteTermsRequest request, CancellationToken ct = default);

    /// <summary>The lines as they are now (a draft preview prints these).</summary>
    Task<QuoteTermsSnapshot> CurrentAsync(CancellationToken ct = default);
}
