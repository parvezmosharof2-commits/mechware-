namespace Mechware.Domain.Invoicing;

/// <summary>
/// ADR-0012 §3. <c>Off</c>: no invoice, ship works as before. <c>Specimen</c>: <c>SPEC-…</c> series, watermarked, never in Production.
/// <c>Live</c>: real Mushak-6.3s; the host refuses to start without the issuer facts.
/// </summary>
public enum InvoicingMode
{
    Off,
    Specimen,
    Live
}

/// <summary>BIN and TIN format checks. ASCII digits only: Bangla digits or full-width ones never pass (they would print as another number).</summary>
public static class TaxIdFormat
{
    /// <summary>A BIN is 13 digits as best known (ADR-0012 §3, §19 Q8: consultant to confirm).</summary>
    public const int BinLength = 13;

    public const int TinLength = 12;

    public static bool IsBin(string? value) => value is { Length: BinLength } && value.All(char.IsAsciiDigit);

    public static bool IsTin(string? value) => value is { Length: TinLength } && value.All(char.IsAsciiDigit);
}

/// <summary>
/// Configuration section <c>Invoicing</c> (ADR-0012 §3, §7, §9). The company facts come from here, not from the database, because the
/// owner has not supplied them yet (docs/COMPANY_FACTS.md): Live mode does not start until they are set. Production has no default
/// <see cref="Mode"/>: the deploy config must name it, so nobody gets Off or Live by accident.
/// </summary>
public sealed record InvoicingOptions
{
    public const string Section = "Invoicing";

    /// <summary>Null = not written in the config: refused in Production, <see cref="InvoicingMode.Off"/> elsewhere.</summary>
    public InvoicingMode? Mode { get; init; }

    /// <summary>The seller row of MECHWARE's own store; the only seller invoiced for at launch (marketplace sellers are H5).</summary>
    public Guid? IssuerSellerId { get; init; }

    public string? LegalNameEn { get; init; }
    public string? LegalNameBn { get; init; }
    public string? Bin { get; init; }
    public string? Tin { get; init; }
    public string? TradeLicence { get; init; }
    public string? RegisteredAddressEn { get; init; }
    public string? RegisteredAddressBn { get; init; }

    /// <summary>Blank = the registered address.</summary>
    public string? IssuingPremisesAddressEn { get; init; }

    public string? IssuingPremisesAddressBn { get; init; }
    public string? ResponsiblePersonName { get; init; }
    public string? ResponsiblePersonDesignation { get; init; }
    public string NumberPrefix { get; init; } = "MW";
    public int CreditNoteMaxAgeDays { get; init; } = 180;

    /// <summary>VAT inside the delivery charge (§7): default 0, "consultant check"; the owner action "VAT on shipping" is open.</summary>
    public decimal ShippingVatRate { get; init; }

    /// <summary>False until the VAT consultant signs off (E6.0); every PDF then says "consultant check before relying on it".</summary>
    public bool ConsultantChecked { get; init; }

    /// <summary>The mode in force: the configured one, or Off outside Production when none is written.</summary>
    public InvoicingMode EffectiveMode => Mode ?? InvoicingMode.Off;

    /// <summary>The issuer facts a Live invoice cannot be issued without (TIN and trade licence are optional on the form).</summary>
    public IReadOnlyList<string> MissingIssuerFacts()
    {
        var missing = new List<string>();
        void Need(bool ok, string name) { if (!ok) missing.Add(name); }
        Need(IssuerSellerId is { } s && s != Guid.Empty, nameof(IssuerSellerId));
        Need(!string.IsNullOrWhiteSpace(LegalNameEn), nameof(LegalNameEn));
        Need(!string.IsNullOrWhiteSpace(LegalNameBn), nameof(LegalNameBn));
        Need(TaxIdFormat.IsBin(Bin?.Trim()), nameof(Bin));
        Need(!string.IsNullOrWhiteSpace(RegisteredAddressEn), nameof(RegisteredAddressEn));
        Need(!string.IsNullOrWhiteSpace(RegisteredAddressBn), nameof(RegisteredAddressBn));
        Need(!string.IsNullOrWhiteSpace(ResponsiblePersonName), nameof(ResponsiblePersonName));
        Need(!string.IsNullOrWhiteSpace(ResponsiblePersonDesignation), nameof(ResponsiblePersonDesignation));
        return missing;
    }

    /// <param name="isProduction">The host environment is Production (ASPNETCORE_ENVIRONMENT).</param>
    public IReadOnlyList<string> Problems(bool isProduction)
    {
        var p = new List<string>();
        void Check(bool ok, string name, string rule) { if (!ok) p.Add($"{Section}:{name} {rule}."); }
        Check(Mode is null || Enum.IsDefined(Mode.Value), nameof(Mode), "must be Off, Specimen or Live");
        Check(!isProduction || Mode is not null, nameof(Mode), "must be written in the Production config (Off, Specimen or Live); there is no default");
        Check(!isProduction || Mode != InvoicingMode.Specimen, nameof(Mode), "Specimen is not allowed in Production");
        if (Mode == InvoicingMode.Live)
            foreach (var fact in MissingIssuerFacts()) Check(false, fact, "is required in Live mode (set it, or the host does not start)");
        Check(string.IsNullOrWhiteSpace(Bin) || TaxIdFormat.IsBin(Bin.Trim()), nameof(Bin), $"must be {TaxIdFormat.BinLength} digits");
        Check(string.IsNullOrWhiteSpace(Tin) || TaxIdFormat.IsTin(Tin.Trim()), nameof(Tin), $"must be {TaxIdFormat.TinLength} digits");
        Check(NumberPrefix is { Length: >= 2 and <= 6 } && NumberPrefix.All(char.IsAsciiLetterUpper) && NumberPrefix != InvoiceNumber.SpecimenPrefix,
            nameof(NumberPrefix), "must be 2 to 6 capital letters and not SPEC");
        Check(CreditNoteMaxAgeDays is >= 1 and <= 3650, nameof(CreditNoteMaxAgeDays), "must be 1 to 3650");
        Check(ShippingVatRate is >= 0m and <= 0.5m && decimal.Round(ShippingVatRate, 4) == ShippingVatRate, nameof(ShippingVatRate),
            "must be 0 to 0.5 with at most 4 decimals");
        return p;
    }

    /// <summary>This instance, or <see cref="InvalidOperationException"/> naming every bad value (the host does not start).</summary>
    public InvoicingOptions Validated(bool isProduction) =>
        Problems(isProduction) is { Count: > 0 } p ? throw new InvalidOperationException("Invalid invoicing options: " + string.Join(" ", p)) : this;

    internal InvoiceSellerSnapshot SellerSnapshot() => new(
        Clean(LegalNameEn), Clean(LegalNameBn), Clean(Bin), Clean(Tin), Clean(TradeLicence), Clean(RegisteredAddressEn), Clean(RegisteredAddressBn),
        Clean(IssuingPremisesAddressEn) ?? Clean(RegisteredAddressEn), Clean(IssuingPremisesAddressBn) ?? Clean(RegisteredAddressBn),
        Clean(ResponsiblePersonName), Clean(ResponsiblePersonDesignation));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
