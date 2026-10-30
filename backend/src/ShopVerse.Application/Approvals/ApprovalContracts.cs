namespace ShopVerse.Application.Approvals;

public sealed record ApprovalRuleRequest(
    string Name, long MinAmountMinor, Guid? CostCentreId, IReadOnlyList<Guid>? CategoryIds,
    IReadOnlyList<Guid> ApproverUserIds, bool IsActive = true);

public sealed record ApprovalRuleDto(
    Guid Id, string Name, long MinAmountMinor, Guid? CostCentreId, IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<Guid> ApproverUserIds, bool IsActive);

/// <summary>What needs approval. <c>SubjectType</c> + <c>SubjectId</c> point at the thing itself (an order, later).</summary>
public sealed record SubmitApprovalRequest(
    string SubjectType, Guid SubjectId, long AmountMinor, Guid? CostCentreId, IReadOnlyList<Guid>? CategoryIds);

public sealed record ApprovalStepDto(int Position, Guid ApproverUserId, string Decision, string? Comment, DateTime? DecidedAtUtc);

public sealed record ApprovalRequestDto(
    Guid Id, Guid RequesterUserId, string SubjectType, Guid SubjectId, long AmountMinor, Guid? CostCentreId,
    IReadOnlyList<Guid> CategoryIds, Guid RuleId, string RuleName, string Status, Guid? CurrentApproverUserId,
    IReadOnlyList<ApprovalStepDto> Steps, DateTime CreatedAtUtc);

/// <summary>When no rule matches, nothing is created and <c>ApprovalRequired</c> is false: the order can go ahead.</summary>
public sealed record SubmitApprovalResult(bool ApprovalRequired, ApprovalRequestDto? Request);

public sealed record DecisionRequest(bool Approve, string? Comment);

/// <summary>
/// Approval rules and requests (brief §8.3). Every method takes the acting user and resolves access from the
/// user's company membership in the database: not a member → "not found", wrong role → forbidden.
/// </summary>
public interface IApprovalService
{
    Task<IReadOnlyList<ApprovalRuleDto>> ListRulesAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);
    Task<ApprovalRuleDto> AddRuleAsync(Guid actorUserId, Guid companyId, ApprovalRuleRequest request, CancellationToken ct = default);
    Task<ApprovalRuleDto> UpdateRuleAsync(Guid actorUserId, Guid companyId, Guid ruleId, ApprovalRuleRequest request, CancellationToken ct = default);
    Task RemoveRuleAsync(Guid actorUserId, Guid companyId, Guid ruleId, CancellationToken ct = default);

    Task<SubmitApprovalResult> SubmitAsync(Guid actorUserId, Guid companyId, SubmitApprovalRequest request, CancellationToken ct = default);
    Task<ApprovalRequestDto> GetAsync(Guid actorUserId, Guid companyId, Guid requestId, CancellationToken ct = default);
    /// <summary>Requests waiting for the caller's decision right now.</summary>
    Task<IReadOnlyList<ApprovalRequestDto>> ListInboxAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);
    /// <summary>Requests the caller submitted.</summary>
    Task<IReadOnlyList<ApprovalRequestDto>> ListMineAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);
    Task<ApprovalRequestDto> DecideAsync(Guid actorUserId, Guid companyId, Guid requestId, DecisionRequest request, CancellationToken ct = default);
    Task<ApprovalRequestDto> CancelAsync(Guid actorUserId, Guid companyId, Guid requestId, CancellationToken ct = default);
}
