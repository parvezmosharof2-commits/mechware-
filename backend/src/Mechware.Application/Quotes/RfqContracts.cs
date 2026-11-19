namespace Mechware.Application.Quotes;

/// <summary>
/// What a buyer sends to ask for a quote (ADR-0011 §2). Only the source's own member is read: <c>text</c>/<c>csv</c> the pasted lines,
/// <c>product</c> a variant and a quantity, <c>cart</c> nothing (the server reads the caller's own cart). There is no price, line list
/// or company in it; the company is the one in the URL, whose membership was checked.
/// </summary>
public sealed record CreateRfqRequest(Guid UserId, Guid CompanyId, string IdempotencyKey, string? Source, string? Text, Guid? VariantId, int? Quantity,
    string? Message, DateOnly? NeededBy, Guid? ShipToSiteId);

public sealed record RfqVariantDto(Guid Id, string Sku, string Title, string TitleBn);

/// <param name="MatchStatus"><c>matched</c>, <c>ambiguous</c>, <c>not_found</c> or <c>obsolete</c>.</param>
public sealed record RfqLineDto(int LineNo, string RawText, string? PartKey, int Quantity, string MatchStatus, RfqVariantDto? Variant, RfqVariantDto? Successor, string? Note);

/// <summary>An RFQ as its company's member sees it: no staff ids, no assignee, no idempotency data. A closed one shows its reason.</summary>
public sealed record RfqSummaryDto(Guid Id, string Number, string Status, string Source, int LineCount, string? Message, DateOnly? NeededBy, Guid? ShipToSiteId,
    bool RequestedByMe, DateTime CreatedAt, DateTime? ClosedAt, string? CloseReason, DateTime? WithdrawnAt);

public sealed record RfqDetailDto(RfqSummaryDto Rfq, IReadOnlyList<RfqLineDto> Lines);

public sealed record RfqCreated(RfqDetailDto Rfq, bool Replayed);

public sealed record RfqPage(IReadOnlyList<RfqSummaryDto> Items, int Page, int PageSize, int Total);

/// <summary>
/// The buyer side of RFQs (ADR-0011 §2, §12, §13; D4.5). Every refusal about access, whatever its reason (unknown company, not a
/// member, an RFQ of another company or of another member, a company that is not Active or Suspended), is <c>KeyNotFoundException</c>
/// with one message, so nothing tells the cases apart.
/// </summary>
public interface IBuyerRfqService
{
    Task<RfqCreated> CreateAsync(CreateRfqRequest request, CancellationToken ct = default);
    Task<RfqPage> ListAsync(Guid actorUserId, Guid companyId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<RfqDetailDto> GetAsync(Guid actorUserId, Guid companyId, Guid rfqId, CancellationToken ct = default);
    Task<RfqDetailDto> WithdrawAsync(Guid actorUserId, Guid companyId, Guid rfqId, CancellationToken ct = default);
}
