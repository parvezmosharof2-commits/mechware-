using ShopVerse.Domain.Common.Enums;

namespace ShopVerse.Domain.Common.Exceptions;

/// <summary>
/// Thrown when an arithmetic operation is attempted on mismatched currencies.
/// </summary>
public sealed class CurrencyMismatchException : DomainException
{
    public Currency BaseCurrency { get; }
    public Currency TargetCurrency { get; }

    public CurrencyMismatchException(Currency baseCurrency, Currency targetCurrency)
        : base("CURRENCY_MISMATCH", $"Cannot perform monetary operations between {baseCurrency.ToIsoCode()} and {targetCurrency.ToIsoCode()}.")
    {
        BaseCurrency = baseCurrency;
        TargetCurrency = targetCurrency;
    }
}
