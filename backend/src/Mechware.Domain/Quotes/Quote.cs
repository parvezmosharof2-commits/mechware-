using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Domain.Quotes;

/// <summary>
/// A quote to one company (ADR-0011 §3), optionally answering one RFQ; company, RFQ and number never change. It holds versions
/// 1, 2, 3 … with at most one open (Draft | PendingApproval) and at most one live (Sent | Accepted) at a time: sending a newer
/// version supersedes the live one in the same step. A closed quote takes no further step.
/// <para>
/// The order side is not part of this aggregate. Where a rule depends on it, the caller passes <c>acceptedOrderLapsed</c>: true
/// when the order of the Accepted version is Cancelled and was never confirmed (§8.6), so that version may be accepted again,
/// revised and superseded. Four-eyes, the flags and the acknowledgement lists of an approval are D4.3 / D4.8.
/// </para>
/// </summary>
public sealed class Quote : AggregateRoot<Guid>
{
    private readonly List<QuoteVersion> _versions = [];

    public string Number { get; private set; } = string.Empty;
    public Guid CompanyId { get; private set; }
    public Guid? RfqId { get; private set; }
    public Guid ContactUserId { get; private set; }
    public Guid? AssignedTo { get; private set; }
    public Guid CreatedBy { get; private set; }
    public int LatestVersionNo { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public string? CloseReason { get; private set; }
    public bool IsClosed => ClosedAtUtc is not null;
    public IReadOnlyList<QuoteVersion> Versions => _versions.AsReadOnly();
    public QuoteVersion? OpenVersion => _versions.SingleOrDefault(v => v.Status is S.Draft or S.PendingApproval);
    public QuoteVersion? LiveVersion => _versions.SingleOrDefault(v => v.Status is S.Sent or S.Accepted);

    private Quote() { }

    /// <summary>A new quote whose version 1 is an empty Draft with the default validity; the author is its first contributor.</summary>
    public static Quote Create(Guid id, Guid firstVersionId, string number, Guid companyId, Guid? rfqId, Guid contactUserId, Guid createdBy,
        QuoteOptions options, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (id == Guid.Empty || companyId == Guid.Empty || contactUserId == Guid.Empty || rfqId == Guid.Empty)
            throw new ValidationException("id", "A quote needs an id, a company and a contact.");
        if (!QuoteNumber.IsQuoteNumber(number)) throw new ValidationException("number", "Not a quote number.");
        var quote = new Quote
        {
            Id = id,
            Number = number,
            CompanyId = companyId,
            RfqId = rfqId,
            ContactUserId = contactUserId,
            CreatedBy = createdBy,
            LatestVersionNo = 1,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        quote._versions.Add(new QuoteVersion(firstVersionId, id, 1, options.DefaultValidityDays, createdBy, nowUtc));
        return quote;
    }

    public QuoteVersion Version(int versionNo) =>
        _versions.FirstOrDefault(v => v.VersionNo == versionNo) ?? throw new ValidationException("versionNo", "No such version of this quote.");

    public void Assign(Guid? staffId, DateTime nowUtc)
    {
        EnsureOpen();
        (AssignedTo, UpdatedAtUtc) = (staffId, nowUtc);
    }

    public void EditDraft(int versionNo, QuoteDraft draft, Guid editorId, QuoteOptions options, DateTime nowUtc)
    {
        EnsureOpen();
        Version(versionNo).Edit(draft, editorId, options, nowUtc);
    }

    /// <summary>Freezes the Draft. No flag: Sent now (validity starts, the live version is superseded). Flags: PendingApproval.</summary>
    public S Send(int versionNo, QuoteIssue issue, Guid senderId, bool acceptedOrderLapsed, QuoteOptions options, DateTime nowUtc)
    {
        EnsureOpen();
        var v = Version(versionNo);
        if (v.Status != S.Draft) throw v.Refused(S.Sent);
        EnsureNoLiveOrder(acceptedOrderLapsed);
        v.Freeze(issue, senderId, options, nowUtc);
        if (v.Flags!.NeedsApproval)
        {
            v.MoveToApproval(nowUtc);
            AddDomainEvent(new QuoteApprovalRequestedEvent(Id, v.Id, v.VersionNo) { OccurredOnUtc = nowUtc });
            return S.PendingApproval;
        }

        Issue(v, senderId, nowUtc);
        return S.Sent;
    }

    /// <summary>The approval sends the version (§5, §15 decision 13). The approver returns the content hash they were shown.</summary>
    public void Approve(int versionNo, Guid approverId, string expectedContentHash, string? comment, bool acceptedOrderLapsed, DateTime nowUtc)
    {
        EnsureOpen();
        var v = Version(versionNo);
        if (v.Status != S.PendingApproval) throw v.Refused(S.Sent);
        if (approverId == Guid.Empty) throw new ValidationException("approvedBy", "The approver is required.");
        EnsureHash(v, expectedContentHash);
        var note = Text.Optional(comment, QuoteLimits.MaxReasonLength, "comment");
        EnsureNoLiveOrder(acceptedOrderLapsed);
        v.RecordApproval(approverId, note, nowUtc);
        Issue(v, approverId, nowUtc);
    }

    public void RejectApproval(int versionNo, Guid rejectedBy, string comment, DateTime nowUtc)
    {
        EnsureOpen();
        var why = Text.Reason(comment);
        if (rejectedBy == Guid.Empty) throw new ValidationException("rejectedBy", "The approver is required.");
        Version(versionNo).BackToDraft(rejectedBy, why, nowUtc);
    }

    public void Recall(int versionNo, Guid recalledBy, DateTime nowUtc)
    {
        EnsureOpen();
        if (recalledBy == Guid.Empty) throw new ValidationException("recalledBy", "The editor is required.");
        Version(versionNo).BackToDraft(null, null, nowUtc);
    }

    public void Withdraw(int versionNo, Guid withdrawnBy, string reason, DateTime nowUtc)
    {
        EnsureOpen();
        var v = Version(versionNo);
        v.Withdraw(withdrawnBy, reason, nowUtc);
        AddDomainEvent(new QuoteWithdrawnEvent(Id, v.Id, v.VersionNo) { OccurredOnUtc = nowUtc });
    }

    /// <summary>The buyer's "no" with an optional "what should change"; the quote stays open for a revision (§8.8).</summary>
    public void Reject(int versionNo, Guid rejectedBy, string? reason, DateTime nowUtc)
    {
        EnsureOpen();
        var v = Version(versionNo);
        v.Reject(rejectedBy, reason, nowUtc);
        AddDomainEvent(new QuoteRejectedEvent(Id, v.Id, v.VersionNo) { OccurredOnUtc = nowUtc });
    }

    /// <summary>Whether the version may be accepted at <paramref name="nowUtc"/> (§8.6, §7): Sent, or Accepted with a lapsed order, and valid.</summary>
    public bool IsAcceptable(int versionNo, bool acceptedOrderLapsed, DateTime nowUtc)
    {
        var v = Version(versionNo);
        return !IsClosed && (v.Status == S.Sent || (v.Status == S.Accepted && acceptedOrderLapsed))
            && v.ValidUntilUtc is { } until && QuoteValidity.IsValidAt(until, nowUtc);
    }

    /// <summary>Records the order made from the version (one quote, one order, all lines); a lapsed order's version moves to the new order.</summary>
    public void Accept(int versionNo, string expectedContentHash, Guid orderId, Guid acceptedBy, bool acceptedOrderLapsed, DateTime nowUtc)
    {
        var v = Version(versionNo);
        if (v.Status == S.Accepted && !acceptedOrderLapsed && !IsClosed)
            throw new ConflictException("QUOTE_ALREADY_ACCEPTED", "This quote has already been accepted.");
        if (!IsAcceptable(versionNo, acceptedOrderLapsed, nowUtc))
            throw new ConflictException("QUOTE_NOT_ACCEPTABLE", v.IsExpired(nowUtc) ? "This quote has expired. Ask for a new version."
                : "This quote can no longer be accepted (withdrawn, rejected, superseded or closed).");
        EnsureHash(v, expectedContentHash);
        if (orderId == Guid.Empty || acceptedBy == Guid.Empty) throw new ValidationException("orderId", "The order and the buyer are required.");
        v.RecordAcceptance(orderId, acceptedBy, nowUtc);
        AddDomainEvent(new QuoteAcceptedEvent(Id, v.Id, v.VersionNo, orderId) { OccurredOnUtc = nowUtc });
    }

    /// <summary>Copies the latest version into Draft n + 1 (§3) while no version is open and no order of the quote is live or confirmed.</summary>
    public QuoteVersion Revise(Guid newVersionId, Guid reviserId, bool acceptedOrderLapsed, DateTime nowUtc)
    {
        EnsureOpen();
        if (OpenVersion is not null)
            throw new ConflictException("QUOTE_OPEN_VERSION_EXISTS", "This quote already has an open version. Finish or recall it first.");
        EnsureNoLiveOrder(acceptedOrderLapsed);
        if (newVersionId == Guid.Empty || _versions.Any(v => v.Id == newVersionId)) throw new ValidationException("id", "A new version needs a new id.");
        var next = _versions[^1].CopyAsDraft(newVersionId, LatestVersionNo + 1, reviserId, nowUtc);
        _versions.Add(next);
        (LatestVersionNo, UpdatedAtUtc) = (next.VersionNo, nowUtc);
        return next;
    }

    /// <summary>Ends the quote (§3): the Sent version is withdrawn and a pending one recalled in the same step.</summary>
    public void Close(Guid closedBy, string reason, DateTime nowUtc)
    {
        EnsureOpen();
        var why = Text.Reason(reason);
        if (closedBy == Guid.Empty) throw new ValidationException("closedBy", "The closer is required.");
        foreach (var v in _versions)
            if (v.Status == S.Sent) Withdraw(v.VersionNo, closedBy, why, nowUtc);
            else if (v.Status == S.PendingApproval) v.BackToDraft(null, null, nowUtc);
        (ClosedBy, ClosedAtUtc, CloseReason, UpdatedAtUtc) = (closedBy, nowUtc, why, nowUtc);
    }

    private void Issue(QuoteVersion v, Guid senderId, DateTime nowUtc)
    {
        foreach (var live in _versions.Where(o => o != v && o.Status is S.Sent or S.Accepted).ToList()) live.Supersede(nowUtc);
        v.MarkSent(senderId, nowUtc);
        UpdatedAtUtc = nowUtc;
        AddDomainEvent(new QuoteSentEvent(Id, v.Id, v.VersionNo) { OccurredOnUtc = nowUtc });
    }

    private void EnsureOpen()
    {
        if (IsClosed) throw new ConflictException("QUOTE_CLOSED", "This quote is closed.");
    }

    private void EnsureNoLiveOrder(bool acceptedOrderLapsed)
    {
        if (!acceptedOrderLapsed && _versions.Any(v => v.Status == S.Accepted))
            throw new ConflictException("QUOTE_ALREADY_ACCEPTED", "This quote has a live or confirmed order.");
    }

    private static void EnsureHash(QuoteVersion v, string expected)
    {
        if (!string.Equals(expected, v.ContentHash, StringComparison.Ordinal))
            throw new ConflictException("QUOTE_CHANGED", "The quote is not the one you reviewed. Reload it and review again.");
    }
}
