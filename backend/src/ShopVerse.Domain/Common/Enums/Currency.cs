namespace ShopVerse.Domain.Common.Enums;

/// <summary>
/// Supported ISO 4217 Currency Codes.
/// </summary>
public enum Currency
{
    /// <summary>
    /// Bangladeshi Taka (Default commercial currency)
    /// </summary>
    BDT = 1,

    /// <summary>
    /// United States Dollar (International export transactions)
    /// </summary>
    USD = 2,

    /// <summary>
    /// Euro (European engineering components)
    /// </summary>
    EUR = 3
}

public static class CurrencyExtensions
{
    public static string ToIsoCode(this Currency currency) => currency switch
    {
        Currency.BDT => "BDT",
        Currency.USD => "USD",
        Currency.EUR => "EUR",
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, "Unsupported currency code.")
    };

    public static string Symbol(this Currency currency) => currency switch
    {
        Currency.BDT => "৳",
        Currency.USD => "$",
        Currency.EUR => "€",
        _ => currency.ToString()
    };

    public static int DecimalPlaces(this Currency currency) => currency switch
    {
        Currency.BDT => 2,
        Currency.USD => 2,
        Currency.EUR => 2,
        _ => 2
    };

    public static Currency Parse(string code) => code.Trim().ToUpperInvariant() switch
    {
        "BDT" => Currency.BDT,
        "USD" => Currency.USD,
        "EUR" => Currency.EUR,
        _ => throw new ArgumentException($"Invalid currency code: '{code}'", nameof(code))
    };
}
