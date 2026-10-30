using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Approvals;

public enum ApprovalStatus { Pending = 1, Approved = 2, Rejected = 3, Cancelled = 4 }

public enum StepDecision { Pending = 0, Approved = 1, Rejected = 2 }

/// <summary>One approver's turn in the chain. Once decided it never changes.</summary>
public sealed class ApprovalStep : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public int Position { get; private set; }
    public Guid ApproverUserId { get; private set; }
    public StepDecision Decision { get; private set; } = StepDecision.Pending;
    public string? Comment { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }

    private ApprovalStep() { }

    internal ApprovalStep(Guid id, Guid requestId, int position, Guid approverUserId) : base(id)
    {
        RequestId = requestId;
        Position = position;
        ApproverUserId = approverUserId;
    }

    internal void Record(StepDecision decision, string? comment, DateTime nowUtc)
    {
        Decision = decision;
        Comment = comment;
        DecidedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }
}

/// <summary>
/// A request for approval of one thing (an order, later). Created only when a rule matches. Approvers decide
/// in chain order; any rejection ends it; the requester can never decide on their own request (brief §8.3).
/// </summary>
public sealed class ApprovalRequest : AggregateRoot<Guid>
{
    public const int MaxCommentLength = 500;

    private readonly List<ApprovalStep> _steps = [];

    public Guid CompanyId { get; private set; }
    public Guid RequesterUserId { get; private set; }
    public string SubjectType { get; private set; } = string.Empty;
    public Guid SubjectId { get; private set; }
    public long AmountMinor { get; private set; }
    public Guid? CostCentreId { get; private set; }
    public List<Guid> CategoryIds { get; private set; } = [];
    public Guid RuleId { get; private set; }
    /// <summary>Snapshot, so renaming or deleting the rule later does not change history.</summary>
    public string RuleName { get; private set; } = string.Empty;
    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;
    public IReadOnlyList<ApprovalStep> Steps => _steps;

    private ApprovalRequest() { }

    private ApprovalRequest(Guid id) : base(id) { }

    public ApprovalStep? CurrentStep =>
        Status == ApprovalStatus.Pending ? _steps.OrderBy(s => s.Position).FirstOrDefault(s => s.Decision == StepDecision.Pending) : null;

    /// <param name="eligibleApprovers">
    /// When given, only these users can be steps (approvers who left the company or lost their role are skipped).
    /// </param>
    public static ApprovalRequest Create(Guid id, Guid requesterUserId, string subjectType, Guid subjectId, long amountMinor,
        Guid? costCentreId, IEnumerable<Guid>? categoryIds, ApprovalRule rule, IReadOnlySet<Guid>? eligibleApprovers = null)
    {
        var errors = new List<ValidationError>();
        subjectType = (subjectType ?? string.Empty).Trim().ToLowerInvariant();
        if (subjectType.Length is < 1 or > 40 || !subjectType.All(c => c is >= 'a' and <= 'z' or '_' or >= '0' and <= '9'))
            errors.Add(new("subjectType", "Subject type must be 1-40 lowercase letters, digits or underscores."));
        if (subjectId == Guid.Empty) errors.Add(new("subjectId", "Subject id is required."));
        if (amountMinor <= 0) errors.Add(new("amount", "Amount must be positive."));
        if (errors.Count > 0) throw new ValidationException(errors);

        // The requester never approves their own request, so they are skipped in the chain.
        var approvers = rule.ApproverUserIds.Where(a => a != requesterUserId && (eligibleApprovers is null || eligibleApprovers.Contains(a))).ToList();
        if (approvers.Count == 0)
            throw new ConflictException("NO_ELIGIBLE_APPROVER", "The matching rule has no approver who can decide on this request (the requester cannot approve their own, and approvers who left the company are skipped). Ask a company admin to update the rule.");

        var request = new ApprovalRequest(id)
        {
            CompanyId = rule.CompanyId,
            RequesterUserId = requesterUserId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            AmountMinor = amountMinor,
            CostCentreId = costCentreId,
            CategoryIds = categoryIds?.Distinct().ToList() ?? [],
            RuleId = rule.Id,
            RuleName = rule.Name
        };
        for (int i = 0; i < approvers.Count; i++) request._steps.Add(new ApprovalStep(Guid.NewGuid(), id, i, approvers[i]));
        return request;
    }

    public void Decide(Guid actorUserId, bool approve, string? comment, DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending)
            throw new ConflictException("APPROVAL_NOT_PENDING", "This request has already been decided or cancelled.");
        if (actorUserId == RequesterUserId)
            throw new ForbiddenException("SELF_APPROVAL", "You cannot decide on your own request.");

        var step = CurrentStep ?? throw new ConflictException("APPROVAL_NOT_PENDING", "This request has no pending step.");
        if (step.ApproverUserId != actorUserId)
            throw new ForbiddenException("NOT_CURRENT_APPROVER", "It is not your turn to decide on this request.");

        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (!approve && comment is null) throw new ValidationException("comment", "A comment is required when rejecting.");
        if (comment is { Length: > MaxCommentLength }) throw new ValidationException("comment", $"Comment must be at most {MaxCommentLength} characters.");

        step.Record(approve ? StepDecision.Approved : StepDecision.Rejected, comment, nowUtc);
        if (!approve) Status = ApprovalStatus.Rejected;
        else if (_steps.All(s => s.Decision == StepDecision.Approved)) Status = ApprovalStatus.Approved;
        UpdatedAtUtc = nowUtc;
    }

    public void Cancel(Guid actorUserId, DateTime nowUtc)
    {
        if (actorUserId != RequesterUserId)
            throw new ForbiddenException("NOT_REQUESTER", "Only the requester can cancel this request.");
        if (Status != ApprovalStatus.Pending)
            throw new ConflictException("APPROVAL_NOT_PENDING", "This request has already been decided or cancelled.");
        Status = ApprovalStatus.Cancelled;
        UpdatedAtUtc = nowUtc;
    }
}
