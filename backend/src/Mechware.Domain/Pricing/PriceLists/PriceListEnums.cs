namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>Where a unit price came from (ADR-0008 §5). Stored on the order line (§8); values are persisted, never renumber.</summary>
public enum PriceSource { List = 1, Sale = 2, QuantityBreak = 3, GroupList = 4, Contract = 5, Quote = 6 }

/// <summary>Who a price list is for (ADR-0008 §1). Cannot change after creation.</summary>
public enum PriceListScope
{
    /// <summary>Public quantity breaks for every buyer. Entries are on the catalogue basis (VAT-inclusive).</summary>
    General = 1,

    /// <summary>Shared list assigned through <c>Company.DefaultPriceListId</c> (a series id). Entries are net of VAT.</summary>
    Group = 2,

    /// <summary>Contract of one company. Entries are net of VAT.</summary>
    Company = 3,
}

/// <summary>Stored status (ADR-0008 §1). Scheduled / Active / Expired / Superseded are computed from the clock, never stored.</summary>
public enum PriceListStatus { Draft = 1, PendingApproval = 2, Approved = 3, Withdrawn = 4 }

/// <summary>The VAT basis of a unit price in minor units.</summary>
public enum PriceBasis
{
    /// <summary>The price includes VAT (catalogue prices, sale prices, <see cref="PriceListScope.General"/> entries; guest orders).</summary>
    GrossInclVat = 1,

    /// <summary>The price excludes VAT; VAT is added per line by <c>OrderPricing</c> (negotiated entries; company orders).</summary>
    NetOfVat = 2,
}
