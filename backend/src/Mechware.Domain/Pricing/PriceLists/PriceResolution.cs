using Mechware.Domain.Common.Enums;

namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>One entry of a price list version as the resolver sees it. <see cref="UnitPriceMinor"/> is on the list's basis.</summary>
public sealed record PriceListEntryInput(Guid Id, Guid VariantId, int MinQty, long UnitPriceMinor);

/// <summary>
/// One version of a price list, as committed data (ADR-0008 §1). The caller passes <b>every</b> Approved or Withdrawn
/// version of each series it loads, not only the one it thinks applies: a later version that has started (even if it
/// was withdrawn or has expired since) must be seen, or an older cheaper version would come back (§3, §5).
/// </summary>
/// <param name="ContractRef">Contract number of a <see cref="PriceListScope.Company"/> list; returned with a contract price.</param>
/// <param name="ApprovedAtUtc">Null until approved; a list without it never starts.</param>
/// <param name="WithdrawnAtUtc">For a Withdrawn list; when missing, the version is assumed to have started (fail closed).</param>
public sealed record PriceListInput(
    Guid Id,
    Guid SeriesId,
    int Version,
    PriceListScope Scope,
    Guid? CompanyId,
    string? ContractRef,
    Currency Currency,
    PriceListStatus Status,
    DateTime ValidFromUtc,
    DateTime ValidToUtc,
    DateTime? ApprovedAtUtc,
    DateTime? WithdrawnAtUtc,
    IReadOnlyList<PriceListEntryInput> Entries);

/// <summary>Everything needed to price one variant for one order, with no I/O and no clock.</summary>
/// <param name="Quantity">All units of this variant in the order (lines of one variant are added up first), 1 … 10,000.</param>
/// <param name="Catalogue">The variant's catalogue facts. Its prices are VAT-inclusive for everyone (owner decision 2).</param>
/// <param name="Mode">Inclusive: compare and return gross prices. Exclusive: compare and return net prices. A company order must be Exclusive.</param>
/// <param name="VerifiedCompanyId">From the membership check of the same request (B1), never from the request body; null for guest/personal orders.</param>
/// <param name="GroupSeriesId">The company's <c>DefaultPriceListId</c> (a series id); ignored without a company.</param>
/// <param name="AsOfUtc">The order's single server instant.</param>
public sealed record PriceResolutionRequest(
    Guid VariantId,
    int Quantity,
    PricingInput Catalogue,
    TaxMode Mode,
    Guid? VerifiedCompanyId,
    Guid? GroupSeriesId,
    DateTime AsOfUtc,
    IReadOnlyCollection<PriceListInput> Lists);

/// <summary>The resolved unit price and what the order line snapshots (ADR-0008 §8).</summary>
/// <param name="UnitPriceMinor">Whole poisha, 1 … <see cref="PriceListLimits.MaxUnitPriceMinor"/>, on <see cref="Basis"/>.</param>
/// <param name="PublicUnitPriceMinor">The lowest public candidate (list, sale, General break) for this quantity at this instant, on <see cref="Basis"/>.</param>
public sealed record PriceResolution(
    Guid VariantId,
    long UnitPriceMinor,
    PriceSource Source,
    PriceBasis Basis,
    Guid? PriceListId,
    int? PriceListVersion,
    Guid? EntryId,
    string? ContractRef,
    long PublicUnitPriceMinor);
