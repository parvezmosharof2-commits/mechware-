using System.Globalization;
using System.Text.Json.Serialization;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Application.Pricing;

/// <summary>One variant a buyer asks the price of, for this many units.</summary>
public sealed record CompanyPriceItem(Guid VariantId, int Quantity);

/// <summary>A contract tier that applies now: from <see cref="MinQty"/> units the company pays this net unit price.</summary>
public sealed record CompanyPriceBreakDto(int MinQty, long UnitPriceMinorNet);

/// <summary>
/// What the company would be charged for one variant at this instant (D2.10, ADR-0008 §11). Every member except
/// <see cref="VariantId"/> and <see cref="Status"/> is left out of the JSON when null, so an unavailable variant is
/// <c>{ variantId, status: "unavailable" }</c> only. There is no cost, floor, margin, list version or entry id (PL12).
/// </summary>
public sealed record CompanyPriceDto
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";

    public Guid VariantId { get; init; }

    /// <summary><c>available</c>, or <c>unavailable</c> for a variant that is unknown, unpublished or not sellable.</summary>
    public string Status { get; init; } = Unavailable;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Quantity { get; init; }

    /// <summary>The unit price the order would carry: VAT-exclusive (a company order is priced net).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? UnitPriceMinorNet { get; init; }

    /// <summary>The lowest public price (list, sale, public break) for this quantity, on the same net basis: for "you save".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? PublicUnitPriceMinorNet { get; init; }

    /// <summary>What a guest pays per unit for this quantity right now, VAT-inclusive (the storefront's price).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? GuestUnitPriceMinorInclVat { get; init; }

    /// <summary><c>list</c>, <c>sale</c>, <c>quantity_break</c> or <c>contract</c> (the order line's <c>priceSource</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; init; }

    /// <summary>The company's own contract list, for a contract price only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? PriceListId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContractRef { get; init; }

    /// <summary>End of the contract's window (exclusive), for a contract price only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? ValidToUtc { get; init; }

    /// <summary>Always <c>exclusive</c>: the line adds VAT on the net price.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TaxMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? VatRate { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? LineNetMinor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? LineTaxMinor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? LineTotalMinor { get; init; }

    /// <summary>The company's contract tiers that apply now, by quantity; empty unless one of them starts above 1 unit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<CompanyPriceBreakDto>? Breaks { get; init; }

    /// <summary>Why an order would refuse this quantity (minimum or pack size); null when it is fine.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? QuantityProblem { get; init; }
}

/// <param name="AsOfUtc">The one server instant every item was priced at.</param>
public sealed record CompanyPricesDto(Guid CompanyId, DateTime AsOfUtc, IReadOnlyList<CompanyPriceDto> Items);

/// <summary>
/// The company prices of a signed-in buyer (D2.10). Only a member holding the Buyer role of an active company, the same people
/// who may place orders for it, gets an answer; for anyone else the company does not exist (404, one body for every case).
/// Prices come from <see cref="IPriceResolver"/> exactly as an order prices them.
/// </summary>
public interface ICompanyPriceService
{
    /// <param name="items">The raw query value, parsed by <see cref="CompanyPriceQuery.Parse"/> after the membership check.</param>
    Task<CompanyPricesDto> GetAsync(Guid actorUserId, Guid companyId, string? items, CancellationToken ct = default);
}

/// <summary>
/// Parses <c>items=variantId[:qty],variantId[:qty],…</c>: at most <see cref="MaxItems"/> items, each variant once, quantity
/// 1 … <see cref="PlaceOrderRequest.MaxQuantityPerLine"/> (default 1). More items → 400 <c>TOO_MANY_VARIANTS</c>; anything
/// else malformed → 400 <c>VALIDATION_ERROR</c>. Messages name positions, never echo the input.
/// </summary>
public static class CompanyPriceQuery
{
    public const int MaxItems = 100;

    public static IReadOnlyList<CompanyPriceItem> Parse(string? items)
    {
        if (string.IsNullOrWhiteSpace(items))
            throw new ValidationException("items", "Name at least one variant: items=variantId:quantity,…");
        var parts = items.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > MaxItems)
            throw new BadRequestException("TOO_MANY_VARIANTS", $"Ask for at most {MaxItems} variants per request.");

        var result = new List<CompanyPriceItem>(parts.Length);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < parts.Length; i++)
        {
            var colon = parts[i].IndexOf(':');
            var idText = colon < 0 ? parts[i] : parts[i][..colon];
            if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty)
                throw new ValidationException("items", $"Item {i + 1} is not a variant id.");
            var quantity = 1;
            if (colon >= 0 && (!int.TryParse(parts[i][(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out quantity)
                               || quantity is < 1 or > PlaceOrderRequest.MaxQuantityPerLine))
                throw new ValidationException("items", $"Item {i + 1}: the quantity must be a whole number from 1 to {PlaceOrderRequest.MaxQuantityPerLine}.");
            if (!seen.Add(id)) throw new ValidationException("items", $"Item {i + 1} names a variant a second time; send each variant once.");
            result.Add(new CompanyPriceItem(id, quantity));
        }

        return result;
    }
}
