using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Approvals;

/// <summary>
/// A company's approval rule (brief §8.3). An order that matches waits for the approver chain, in order.
/// Amounts are minor units of the company currency.
/// </summary>
public sealed class ApprovalRule : Entity<Guid>
{
    public const int MaxChainLength = 5;

    public Guid CompanyId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>The rule matches orders whose total is at least this amount.</summary>
    public long MinAmountMinor { get; private set; }
    public Guid? CostCentreId { get; private set; }
    /// <summary>Empty means any category.</summary>
    public List<Guid> CategoryIds { get; private set; } = [];
    /// <summary>Approvers in the order they must decide.</summary>
    public List<Guid> ApproverUserIds { get; private set; } = [];
    public bool IsActive { get; private set; } = true;

    private ApprovalRule() { }

    public ApprovalRule(Guid id, Guid companyId, string name, long minAmountMinor, Guid? costCentreId,
        IEnumerable<Guid>? categoryIds, IEnumerable<Guid> approverUserIds, bool isActive = true) : base(id)
    {
        CompanyId = companyId;
        Update(name, minAmountMinor, costCentreId, categoryIds, approverUserIds, isActive);
    }

    public void Update(string name, long minAmountMinor, Guid? costCentreId, IEnumerable<Guid>? categoryIds,
        IEnumerable<Guid> approverUserIds, bool isActive)
    {
        var errors = new List<ValidationError>();
        name = (name ?? string.Empty).Trim();
        var approvers = (approverUserIds ?? []).ToList();
        if (name.Length is < 1 or > 100) errors.Add(new("name", "Name must be 1-100 characters."));
        if (minAmountMinor < 0) errors.Add(new("minAmount", "The amount threshold cannot be negative."));
        if (approvers.Count is < 1 or > MaxChainLength) errors.Add(new("approverUserIds", $"The approver chain needs 1 to {MaxChainLength} approvers."));
        if (approvers.Distinct().Count() != approvers.Count) errors.Add(new("approverUserIds", "An approver can appear only once in the chain."));
        if (approvers.Contains(Guid.Empty)) errors.Add(new("approverUserIds", "Approver ids must be valid."));
        if (errors.Count > 0) throw new ValidationException(errors);

        Name = name;
        MinAmountMinor = minAmountMinor;
        CostCentreId = costCentreId;
        CategoryIds = categoryIds?.Distinct().ToList() ?? [];
        ApproverUserIds = approvers;
        IsActive = isActive;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public bool Matches(long amountMinor, Guid? costCentreId, IReadOnlyCollection<Guid> categoryIds) =>
        IsActive
        && amountMinor >= MinAmountMinor
        && (CostCentreId is null || CostCentreId == costCentreId)
        && (CategoryIds.Count == 0 || CategoryIds.Intersect(categoryIds).Any());

    /// <summary>
    /// Picks the one rule that governs an order: among the matching rules, the highest amount threshold wins,
    /// then the one with more conditions (cost centre, categories), then the oldest, then the lowest id, so the
    /// choice is deterministic. Returns null when no rule matches (no approval needed).
    /// </summary>
    public static ApprovalRule? Select(IEnumerable<ApprovalRule> rules, long amountMinor, Guid? costCentreId, IReadOnlyCollection<Guid> categoryIds) =>
        rules.Where(r => r.Matches(amountMinor, costCentreId, categoryIds))
             .OrderByDescending(r => r.MinAmountMinor)
             .ThenByDescending(r => (r.CostCentreId is null ? 0 : 1) + (r.CategoryIds.Count == 0 ? 0 : 1))
             .ThenBy(r => r.CreatedAtUtc)
             .ThenBy(r => r.Id)
             .FirstOrDefault();
}
