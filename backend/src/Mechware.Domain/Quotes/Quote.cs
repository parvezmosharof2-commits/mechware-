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
/// revised and superseded. Permissions and step-up are the API's (D4.8); four-eyes, the conflict-of-interest refusal, the
/// flags (<see cref="QuoteApprovalPolicy"/>), their acknowledgement and the content hash are checked here.
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

    /// <summary>
    /// A new quote whose version 1 is an empty Draft with the default validity; the author is its first contributor. A staff member
    /// of the company may not create its quote (conflict of interest, §5, QT6).
    /// </summary>
    public static Quote Create(Guid id, Guid firstVersionId, string number, Guid companyId, Guid? rfqId, Guid contactUserId, QuoteActor creator,
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
            LatestVersionNo = 1,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        quote.EnsureNoConflictOfInterest(creator);
        quote.CreatedBy = creator.UserId;
        quote._versions.Add(new QuoteVersion(firstVersionId, id, 1, options.DefaultValidityDays, creator.UserId, nowUtc));
        return quote;
    }

    public QuoteVersion Version(int versionNo) =>
        _versions.FirstOrDefault(v => v.VersionNo == versionNo) ?? throw new ValidationException("versionNo", "No such version of this quote.");

    public void Assign(Guid? staffId, DateTime nowUtc)
    {
        EnsureOpen();
        (AssignedTo, UpdatedAtUtc) = (staffId, nowUtc);
    }

    public void EditDraft(int versionNo, QuoteDraft draft, QuoteActor editor, QuoteOptions options, DateTime nowUtc)
    {
        EnsureOpen();
        EnsureNoConflictOfInterest(editor);
        Version(versionNo).Edit(draft, editor.UserId, options, nowUtc);
    }

    /// <summary>
    /// Freezes the Draft with the policy run of this moment (<see cref="QuoteIssue.Check"/>) and computes its content hash. No flag:
    /// Sent now (validity starts, the live version is superseded). Flags: PendingApproval. A line below cost is refused.
    /// </summary>
    public S Send(int versionNo, QuoteIssue issue, QuoteActor sender, bool acceptedOrderLapsed, QuoteOptions options, DateTime nowUtc)
    {
        EnsureOpen();
        EnsureNoConflictOfInterest(sender);
        var v = Version(versionNo);
        if (v.Status != S.Draft) throw v.Refused(S.Sent);
        EnsureNoLiveOrder(acceptedOrderLapsed);
        v.Freeze(this, issue, sender.UserId, options, nowUtc);
        if (v.Flags!.NeedsApproval)
        {
            v.MoveToApproval(nowUtc);
            AddDomainEvent(new QuoteApprovalRequestedEvent(Id, v.Id, v.VersionNo) { OccurredOnUtc = nowUtc });
            return S.PendingApproval;
        }

        Issue(v, sender.UserId, nowUtc);
        return S.Sent;
    }

    /// <summary>
    /// The approval sends the version (§5, §15 decision 13). Four-eyes: the approver did not create, change or submit this
    /// version. The approver returns the content hash they were shown, and acknowledges exactly the flagged lines of
    /// <paramref name="check"/>, the policy run inside this approval (cost, floors and buyer prices of now, not of the send).
    /// </summary>
    public void Approve(int versionNo, QuoteActor approver, QuoteApproval approval, QuoteApprovalCheck check, bool acceptedOrderLapsed, DateTime nowUtc)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(approval);
        var v = Version(versionNo);
        if (v.Status != S.PendingApproval) throw v.Refused(S.Sent);
        EnsureNoConflictOfInterest(approver);
        if (v.IsAuthoredBy(approver.UserId))
            throw new ConflictException("QUOTE_FOUR_EYES", "Whoever created, changed or submitted a quote version cannot approve it. Another approver must.");
        EnsureHash(v, approval.ContentHash);
        var note = Text.Optional(approval.Comment, QuoteLimits.MaxReasonLength, "comment");
        EnsureNoLiveOrder(acceptedOrderLapsed);
        var flags = v.Matching(check, atApproval: true).Flags;
        if (flags.BelowFloorLineIds.Count > 0 && !approval.MayApproveBelowFloor)
            throw new ForbiddenException("BELOW_FLOOR_PERMISSION_REQUIRED", "Approving prices below the margin floor needs its own permission.");
        if (!flags.BelowFloorLineIds.ToHashSet().SetEquals(approval.BelowFloorLineIds ?? [])
            || !flags.OverAuthorityLineIds.ToHashSet().SetEquals(approval.OverAuthorityLineIds ?? []))
            throw new ConflictException("QUOTE_FLAGS_NOT_ACKNOWLEDGED", "Acknowledge exactly the lines that are below the floor or over the discount authority now.");
        v.RecordApproval(approver.UserId, note, check, nowUtc);
        Issue(v, approver.UserId, nowUtc);
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

    /// <summary>
    /// Copies the latest version (the highest number, whatever order the versions were loaded in) into Draft n + 1 (§3) while no
    /// version is open and no order of the quote is live or confirmed. A staff member of the company may not revise its quote.
    /// </summary>
    public QuoteVersion Revise(Guid newVersionId, QuoteActor reviser, bool acceptedOrderLapsed, DateTime nowUtc)
    {
        EnsureOpen();
        EnsureNoConflictOfInterest(reviser);
        if (OpenVersion is not null)
            throw new ConflictException("QUOTE_OPEN_VERSION_EXISTS", "This quote already has an open version. Finish or recall it first.");
        EnsureNoLiveOrder(acceptedOrderLapsed);
        if (newVersionId == Guid.Empty || _versions.Any(v => v.Id == newVersionId)) throw new ValidationException("id", "A new version needs a new id.");
        var next = _versions.MaxBy(v => v.VersionNo)!.CopyAsDraft(newVersionId, LatestVersionNo + 1, reviser.UserId, nowUtc);
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

    /// <summary>A staff member of the quote's company may not edit, send or approve its quotes (§5, QT6). Unknown membership fails closed.</summary>
    private void EnsureNoConflictOfInterest(QuoteActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserId == Guid.Empty || actor.CompanyIds is null) throw new ValidationException("actor", "The staff user and their memberships are required.");
        if (actor.CompanyIds.Contains(CompanyId))
            throw new ForbiddenException("QUOTE_CONFLICT_OF_INTEREST", "You are a member of this company, so you may not edit, send or approve its quotes.");
    }

    /// <summary>The hash the caller was shown must be the stored one, and the stored one must still be the content's own (recomputed).</summary>
    private void EnsureHash(QuoteVersion v, string expected)
    {
        if (!string.Equals(expected, v.ContentHash, StringComparison.Ordinal)
            || !string.Equals(v.ContentHash, QuoteContentHash.Compute(this, v), StringComparison.Ordinal))
            throw new ConflictException("QUOTE_CHANGED", "The quote is not the one you reviewed. Reload it and review again.");
    }
}
