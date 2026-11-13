using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mechware.Domain.Common;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>
/// One version of a price list (ADR-0008 §1, §3). Stored status Draft → PendingApproval → Approved → Withdrawn; a rejection
/// returns it to Draft. Only a Draft can change, and every change records its author as a contributor. Approval is
/// four-eyes: the approver is not the creator, the submitter or any contributor, approves the content hash they were
/// shown, and below-floor entries need a reason, the extra permission and an exact acknowledgement. Scope and company
/// never change; a change to an approved list is <see cref="Revise"/>, a new Draft version of the series.
/// </summary>
public sealed class PriceList : AggregateRoot<Guid>
{
    public const int MaxNameLength = 200;
    public const int MaxContractRefLength = 64;
    public const int MinReasonLength = 5;
    public const int MaxReasonLength = 500;

    private readonly List<PriceListEntry> _entries = [];
    private readonly List<PriceListContributor> _contributors = [];

    public Guid SeriesId { get; private set; }
    public int Version { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public PriceListScope Scope { get; private set; }
    public Guid? CompanyId { get; private set; }
    public string? ContractRef { get; private set; }
    public Currency Currency { get; private set; } = Currency.BDT;
    public DateTime ValidFromUtc { get; private set; }
    public DateTime ValidToUtc { get; private set; }
    public PriceListStatus Status { get; private set; } = PriceListStatus.Draft;
    public Guid CreatedBy { get; private set; }
    public Guid? SubmittedBy { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }

    /// <summary>SHA-256 of the content, stored at submit; the approver must send back the one they were shown.</summary>
    public string? ContentHash { get; private set; }

    public Guid? ApprovedBy { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }

    /// <summary><see cref="PriceResolver.EffectiveFromUtc"/> of the approval, so the stored and computed start never differ.</summary>
    public DateTime? EffectiveFromUtc { get; private set; }

    public Guid? RejectedBy { get; private set; }
    public DateTime? RejectedAtUtc { get; private set; }
    public string? RejectionReason { get; private set; }
    public Guid? WithdrawnBy { get; private set; }
    public DateTime? WithdrawnAtUtc { get; private set; }
    public string? WithdrawReason { get; private set; }

    public IReadOnlyCollection<PriceListEntry> Entries => _entries.AsReadOnly();
    public IReadOnlyCollection<PriceListContributor> Contributors => _contributors.AsReadOnly();

    private PriceList() { }

    private PriceList(Guid id, Guid seriesId, int version, string name, PriceListScope scope, Guid? companyId, string? contractRef,
        DateTime validFromUtc, DateTime validToUtc, Guid createdBy, DateTime nowUtc) : base(id)
    {
        if (id == Guid.Empty || seriesId == Guid.Empty) throw new ValidationException("id", "A price list needs an id and a series id.");
        if (!Enum.IsDefined(scope)) throw new ValidationException("scope", "Unknown price list scope.");
        if (scope == PriceListScope.Company ? companyId is null || companyId == Guid.Empty : companyId is not null)
            throw new ValidationException("companyId", "A company list needs a company; other lists must not have one.");
        if (createdBy == Guid.Empty) throw new ValidationException("createdBy", "The author is required.");
        SeriesId = seriesId;
        Version = version;
        Scope = scope;
        CompanyId = companyId;
        Name = ValidName(name);
        ContractRef = ValidContractRef(scope, contractRef);
        (ValidFromUtc, ValidToUtc) = ValidWindow(validFromUtc, validToUtc);
        CreatedBy = createdBy;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        _contributors.Add(new PriceListContributor(id, createdBy, nowUtc));
    }

    /// <summary>Version 1 of a new series, as a Draft. The author is its first contributor.</summary>
    public static PriceList Create(Guid id, Guid seriesId, string name, PriceListScope scope, Guid? companyId, string? contractRef,
        DateTime validFromUtc, DateTime validToUtc, Guid createdBy, DateTime nowUtc) =>
        new(id, seriesId, 1, name, scope, companyId, contractRef, validFromUtc, validToUtc, createdBy, nowUtc);

    // ---- Draft edits (each records the editor as a contributor) ------------------------------------------------------

    public void ChangeHeader(string name, string? contractRef, DateTime validFromUtc, DateTime validToUtc, Guid editorId, DateTime nowUtc)
    {
        EnsureDraft();
        var validName = ValidName(name);
        var validRef = ValidContractRef(Scope, contractRef);
        var window = ValidWindow(validFromUtc, validToUtc);
        Contribute(editorId, nowUtc);
        (Name, ContractRef, (ValidFromUtc, ValidToUtc)) = (validName, validRef, window);
    }

    public PriceListEntry AddEntry(Guid entryId, Guid variantId, int minQty, long unitPriceMinor, string? belowFloorReason, Guid editorId, DateTime nowUtc)
    {
        EnsureDraft();
        if (entryId == Guid.Empty || _entries.Any(e => e.Id == entryId)) throw new ValidationException("entryId", "Each entry needs a new id.");
        if (variantId == Guid.Empty) throw new ValidationException("variantId", "The variant is required.");
        ValidateMinQty(minQty);
        ValidatePrice(unitPriceMinor);
        var reason = OptionalReason(belowFloorReason);
        if (_entries.Count >= PriceListLimits.MaxEntriesPerList)
            throw new ValidationException("entries", $"A price list has at most {PriceListLimits.MaxEntriesPerList} entries.");
        if (_entries.Any(e => e.VariantId == variantId && e.MinQty == minQty))
            throw new ValidationException("minQty", "This variant already has a price from this quantity.");
        Contribute(editorId, nowUtc);
        var entry = new PriceListEntry(entryId, Id, variantId, minQty, unitPriceMinor, reason);
        _entries.Add(entry);
        return entry;
    }

    public void ChangeEntry(Guid entryId, long unitPriceMinor, string? belowFloorReason, Guid editorId, DateTime nowUtc)
    {
        EnsureDraft();
        var entry = FindEntry(entryId);
        ValidatePrice(unitPriceMinor);
        var reason = OptionalReason(belowFloorReason);
        Contribute(editorId, nowUtc);
        entry.Change(unitPriceMinor, reason);
    }

    public void RemoveEntry(Guid entryId, Guid editorId, DateTime nowUtc)
    {
        EnsureDraft();
        var entry = FindEntry(entryId);
        Contribute(editorId, nowUtc);
        _entries.Remove(entry);
    }

    // ---- Transitions -----------------------------------------------------------------------------------------------

    /// <summary>Validates everything, checks the floors (below floor needs a reason; below cost is refused unless allowed) and freezes the list.</summary>
    public void Submit(Guid submitterId, MarginFloorEvaluation floors, bool allowBelowCost, DateTime nowUtc)
    {
        EnsureDraft();
        if (submitterId == Guid.Empty) throw new ValidationException("submittedBy", "The submitter is required.");
        if (_entries.Count == 0) throw new ValidationException("entries", "A price list needs at least one entry.");
        if (ValidToUtc <= nowUtc) throw new ValidationException("validToUtc", "The validity window has already ended.");
        ValidateBreaks();
        var hash = ComputeContentHash();
        var evaluation = Match(floors, hash);
        foreach (var entry in _entries)
        {
            var e = evaluation[entry.Id];
            if (e.BelowCost && !allowBelowCost)
                throw new ValidationException("unitPriceMinor", "A price below cost is not allowed.");
            if (e.BelowFloor && entry.BelowFloorReason is null)
                throw new ValidationException("belowFloorReason", "An entry below the margin floor needs a written reason.");
        }

        (Status, SubmittedBy, SubmittedAtUtc, ContentHash, UpdatedAtUtc) = (PriceListStatus.PendingApproval, submitterId, nowUtc, hash, nowUtc);
    }

    /// <param name="floors">Computed by <see cref="MarginFloorPolicy.Evaluate"/> with the cost and floors of this instant.</param>
    /// <param name="mayApproveBelowFloor">The approver holds <c>pricelists.approve_below_floor</c>.</param>
    /// <param name="acknowledgedBelowFloorEntryIds">Must equal the below-floor entries exactly (empty when there are none).</param>
    public void Approve(Guid approverId, bool mayApproveBelowFloor, string expectedContentHash,
        IReadOnlyCollection<Guid>? acknowledgedBelowFloorEntryIds, MarginFloorEvaluation floors, bool allowBelowCost, DateTime nowUtc)
    {
        if (Status != PriceListStatus.PendingApproval)
            throw new ConflictException("PRICE_LIST_NOT_PENDING", $"A {Status} price list cannot be approved.");
        if (approverId == Guid.Empty || approverId == CreatedBy || approverId == SubmittedBy || _contributors.Any(c => c.UserId == approverId))
            throw new ConflictException("PRICE_LIST_FOUR_EYES", "Whoever created, changed or submitted a price list cannot approve it. Another approver must.");
        var hash = ComputeContentHash();
        if (!string.Equals(expectedContentHash, ContentHash, StringComparison.Ordinal) || !string.Equals(hash, ContentHash, StringComparison.Ordinal))
            throw new ConflictException("PRICE_LIST_CHANGED", "The price list is not the one you reviewed. Reload it and review again.");
        if (PriceResolver.EffectiveFromUtc(ValidFromUtc, nowUtc) is not { } start || start >= ValidToUtc)
            throw new ConflictException("PRICE_LIST_WINDOW_PASSED", "The validity window ends before the list could start. Reject it and change the dates.");

        var evaluation = Match(floors, hash);
        var belowFloor = new HashSet<Guid>();
        foreach (var entry in _entries)
        {
            var e = evaluation[entry.Id];
            if (e.BelowCost && !allowBelowCost) throw new ConflictException("PRICE_BELOW_COST", "An entry is below cost at this moment.");
            if (!e.BelowFloor) continue;
            if (entry.BelowFloorReason is null)
                throw new ConflictException("BELOW_FLOOR_REASON_REQUIRED", "An entry is now below the margin floor and has no reason. Reject the list.");
            belowFloor.Add(entry.Id);
        }

        if (belowFloor.Count > 0 && !mayApproveBelowFloor)
            throw new ForbiddenException("BELOW_FLOOR_PERMISSION_REQUIRED", "Approving prices below the margin floor needs its own permission.");
        if (!belowFloor.SetEquals(acknowledgedBelowFloorEntryIds ?? []))
            throw new ConflictException("BELOW_FLOOR_NOT_ACKNOWLEDGED", "Acknowledge exactly the entries that are below the margin floor.");

        foreach (var entry in _entries) entry.RecordApproval(evaluation[entry.Id]);
        (Status, ApprovedBy, ApprovedAtUtc, EffectiveFromUtc, UpdatedAtUtc) = (PriceListStatus.Approved, approverId, nowUtc, start, nowUtc);
    }

    /// <summary>Back to Draft with a comment; a new submit (and hash) is needed.</summary>
    public void Reject(Guid rejectedBy, string reason, DateTime nowUtc)
    {
        if (Status != PriceListStatus.PendingApproval)
            throw new ConflictException("PRICE_LIST_NOT_PENDING", $"A {Status} price list cannot be rejected.");
        RejectionReason = RequiredReason(reason);
        (RejectedBy, RejectedAtUtc, Status, SubmittedBy, SubmittedAtUtc, ContentHash, UpdatedAtUtc) =
            (rejectedBy, nowUtc, PriceListStatus.Draft, null, null, null, nowUtc);
    }

    /// <summary>Emergency stop of an approved version (§3). One person is enough: a withdrawal never lowers a price.</summary>
    public void Withdraw(Guid withdrawnBy, string reason, DateTime nowUtc)
    {
        if (Status != PriceListStatus.Approved)
            throw new ConflictException("PRICE_LIST_NOT_APPROVED", $"A {Status} price list cannot be withdrawn.");
        WithdrawReason = RequiredReason(reason);
        (WithdrawnBy, WithdrawnAtUtc, Status, UpdatedAtUtc) = (withdrawnBy, nowUtc, PriceListStatus.Withdrawn, nowUtc);
    }

    /// <summary>
    /// A new Draft version (version + 1) of the series with this version's header and entries (new entry ids). The reviser
    /// is its creator and only contributor; no submitter, hash or approval is carried over. "One open version per series"
    /// and "revise the latest version" are checked by the caller against the database.
    /// </summary>
    public PriceList Revise(Guid newId, Guid reviserId, DateTime nowUtc)
    {
        if (Status is not (PriceListStatus.Approved or PriceListStatus.Withdrawn))
            throw new ConflictException("PRICE_LIST_NOT_REVISABLE", $"A {Status} price list is changed directly, not revised.");
        if (newId == Id) throw new ValidationException("id", "A new version needs a new id.");
        var next = new PriceList(newId, SeriesId, checked(Version + 1), Name, Scope, CompanyId, ContractRef, ValidFromUtc, ValidToUtc, reviserId, nowUtc);
        foreach (var e in _entries)
            next._entries.Add(new PriceListEntry(Guid.NewGuid(), newId, e.VariantId, e.MinQty, e.UnitPriceMinor, e.BelowFloorReason));
        return next;
    }

    /// <summary>
    /// Lower-case hex SHA-256 of a canonical, length-prefixed text of the header (id, series, version, name, scope, company,
    /// contract ref, currency, window) and every entry (id, variant, minQty, price, reason) sorted by variant and minQty, so
    /// the entry order never matters and any change of content does.
    /// </summary>
    public string ComputeContentHash()
    {
        var sb = new StringBuilder("mechware.pricelist.v1\n");
        Field(sb, Id.ToString("N"));
        Field(sb, SeriesId.ToString("N"));
        Field(sb, Version.ToString(CultureInfo.InvariantCulture));
        Field(sb, Name);
        Field(sb, ((int)Scope).ToString(CultureInfo.InvariantCulture));
        Field(sb, CompanyId?.ToString("N") ?? "-");
        Field(sb, ContractRef ?? "-");
        Field(sb, Currency.ToIsoCode());
        Field(sb, ValidFromUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        Field(sb, ValidToUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var e in _entries.OrderBy(e => e.VariantId.ToString("N"), StringComparer.Ordinal).ThenBy(e => e.MinQty))
        {
            Field(sb, e.Id.ToString("N"));
            Field(sb, e.VariantId.ToString("N"));
            Field(sb, e.MinQty.ToString(CultureInfo.InvariantCulture));
            Field(sb, e.UnitPriceMinor.ToString(CultureInfo.InvariantCulture));
            Field(sb, e.BelowFloorReason ?? "-");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ---- Rules -----------------------------------------------------------------------------------------------------

    private void EnsureDraft()
    {
        if (Status != PriceListStatus.Draft)
            throw new ConflictException("PRICE_LIST_NOT_EDITABLE", $"A {Status} price list cannot be changed. Revise it to make a new version.");
    }

    private void Contribute(Guid editorId, DateTime nowUtc)
    {
        if (editorId == Guid.Empty) throw new ValidationException("editor", "The editor is required.");
        if (!_contributors.Any(c => c.UserId == editorId)) _contributors.Add(new PriceListContributor(Id, editorId, nowUtc));
        UpdatedAtUtc = nowUtc;
    }

    private PriceListEntry FindEntry(Guid entryId) =>
        _entries.FirstOrDefault(e => e.Id == entryId) ?? throw new ValidationException("entryId", "No such entry in this price list.");

    private Dictionary<Guid, EntryFloorEvaluation> Match(MarginFloorEvaluation floors, string hash)
    {
        ArgumentNullException.ThrowIfNull(floors);
        var byId = floors.Entries.ToDictionary(e => e.EntryId);
        if (floors.PriceListId != Id || !string.Equals(floors.ContentHash, hash, StringComparison.Ordinal)
            || byId.Count != _entries.Count || _entries.Any(e => !byId.ContainsKey(e.Id)))
            throw new ConflictException("PRICE_LIST_FLOOR_STALE", "The margin floors were checked for other content. Check them again.");
        return byId;
    }

    /// <summary>Per variant, a larger minQty never has a higher price (§1).</summary>
    private void ValidateBreaks()
    {
        foreach (var variant in _entries.GroupBy(e => e.VariantId))
        {
            var prices = variant.OrderBy(e => e.MinQty).Select(e => e.UnitPriceMinor).ToList();
            for (var i = 1; i < prices.Count; i++)
                if (prices[i] > prices[i - 1])
                    throw new ValidationException("entries", "A quantity break must not cost more per unit than a smaller quantity.");
        }
    }

    private void ValidateMinQty(int minQty)
    {
        var min = Scope == PriceListScope.General ? 2 : 1; // one unit of a public price is the catalogue's job (§1)
        if (minQty < min || minQty > OrderLimits.MaxQuantityPerLine)
            throw new ValidationException("minQty", $"Minimum quantity must be between {min} and {OrderLimits.MaxQuantityPerLine}.");
    }

    private static void ValidatePrice(long unitPriceMinor)
    {
        if (unitPriceMinor is < 1 or > PriceListLimits.MaxUnitPriceMinor)
            throw new ValidationException("unitPriceMinor", "The price must be positive and not above the maximum unit price.");
    }

    private static string ValidName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxNameLength) throw new ValidationException("name", $"Give a name of 1 to {MaxNameLength} characters.");
        return trimmed;
    }

    private static string? ValidContractRef(PriceListScope scope, string? contractRef)
    {
        var trimmed = contractRef?.Trim();
        if (scope != PriceListScope.Company)
            return string.IsNullOrEmpty(trimmed) ? null : throw new ValidationException("contractRef", "Only a company list has a contract reference.");
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxContractRefLength)
            throw new ValidationException("contractRef", $"A company list needs a contract reference of 1 to {MaxContractRefLength} characters.");
        return trimmed;
    }

    /// <summary><c>[from, to)</c> in UTC, at most <see cref="PriceListLimits.MaxValidityDays"/>; whole seconds, so the hash survives the database.</summary>
    private static (DateTime From, DateTime To) ValidWindow(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Kind == DateTimeKind.Local || toUtc.Kind == DateTimeKind.Local)
            throw new ValidationException("validFromUtc", "Validity is given in UTC.");
        var from = WholeSecondUtc(fromUtc);
        var to = WholeSecondUtc(toUtc);
        if (to <= from) throw new ValidationException("validToUtc", "The validity must end after it starts.");
        if (to - from > TimeSpan.FromDays(PriceListLimits.MaxValidityDays))
            throw new ValidationException("validToUtc", $"A price list is valid for at most {PriceListLimits.MaxValidityDays} days.");
        return (from, to);
    }

    private static DateTime WholeSecondUtc(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static string? OptionalReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : RequiredReason(trimmed);
    }

    private static string RequiredReason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < MinReasonLength or > MaxReasonLength)
            throw new ValidationException("reason", $"Give a reason of {MinReasonLength} to {MaxReasonLength} characters.");
        return trimmed;
    }

    private static void Field(StringBuilder sb, string value) =>
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
}
