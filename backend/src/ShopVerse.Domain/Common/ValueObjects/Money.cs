using System.Globalization;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Common.ValueObjects;

/// <summary>
/// Domain Value Object representing a monetary amount with Banker's Rounding (MidpointRounding.ToEven),
/// 128-bit decimal precision, and minor-unit storage conversion.
/// </summary>
public readonly record struct Money : IComparable<Money>, IComparable
{
    public decimal Amount { get; }
    public Currency Currency { get; }

    /// <summary>
    /// Creates a Money instance with Banker's Rounding applied to 2 decimal places.
    /// </summary>
    public Money(decimal amount, Currency currency = Currency.BDT)
    {
        // Enforce Banker's rounding (Half-Even) to prevent statistical upward drift
        Amount = decimal.Round(amount, 2, MidpointRounding.ToEven);
        Currency = currency;
    }

    /// <summary>
    /// Factory for zero money in given currency.
    /// </summary>
    public static Money Zero(Currency currency = Currency.BDT) => new(0m, currency);

    private static readonly CultureInfo BdCulture = CultureInfo.ReadOnly(new CultureInfo("bn-BD"));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static decimal GetMultiplier(int decimals) => decimals switch
    {
        0 => 1m,
        2 => 100m,
        3 => 1000m,
        4 => 10000m,
        _ => (decimal)Math.Pow(10, decimals)
    };

    /// <summary>
    /// Factory from minor units (e.g. 125050 Poisha -> 1250.50 BDT).
    /// </summary>
    public static Money FromMinorUnits(long minorUnits, Currency currency = Currency.BDT)
    {
        int decimals = currency.DecimalPlaces();
        decimal divisor = GetMultiplier(decimals);
        decimal amount = (decimal)minorUnits / divisor;
        return new Money(amount, currency);
    }

    /// <summary>
    /// Converts to integer minor units (Poisha / Cents).
    /// </summary>
    public long ToMinorUnits()
    {
        int decimals = Currency.DecimalPlaces();
        decimal multiplier = GetMultiplier(decimals);
        return (long)decimal.Round(Amount * multiplier, 0, MidpointRounding.ToEven);
    }

    public bool IsZero => Amount == 0m;
    public bool IsPositive => Amount > 0m;
    public bool IsNegative => Amount < 0m;

    #region Arithmetic Operators

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money money)
    {
        return new Money(-money.Amount, money.Currency);
    }

    public static Money operator *(Money money, decimal multiplier)
    {
        return new Money(money.Amount * multiplier, money.Currency);
    }

    public static Money operator *(decimal multiplier, Money money)
    {
        return money * multiplier;
    }

    public static Money operator *(Money money, int multiplier)
    {
        return new Money(money.Amount * multiplier, money.Currency);
    }

    public static Money operator *(int multiplier, Money money)
    {
        return money * multiplier;
    }

    public static Money operator /(Money money, decimal divisor)
    {
        if (divisor == 0m)
            throw new DivideByZeroException("Cannot divide Money by zero.");

        return new Money(money.Amount / divisor, money.Currency);
    }

    public static Money operator /(Money money, int divisor)
    {
        if (divisor == 0)
            throw new DivideByZeroException("Cannot divide Money by zero.");

        return new Money(money.Amount / divisor, money.Currency);
    }

    #endregion

    #region Comparison Operators

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount > right.Amount;
    }

    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount < right.Amount;
    }

    public static bool operator >=(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount >= right.Amount;
    }

    public static bool operator <=(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount <= right.Amount;
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is not Money other)
            throw new ArgumentException($"Object must be of type {nameof(Money)}");

        return CompareTo(other);
    }

    #endregion

    #region Allocation / Splitting

    /// <summary>
    /// Distributes monetary amount proportionally across N ratios without loss of minor units.
    /// E.g. allocating 100 across 3 shares yields [33.34, 33.33, 33.33].
    /// </summary>
    public Money[] Distribute(int parts)
    {
        if (parts <= 0)
            throw new ArgumentOutOfRangeException(nameof(parts), "Parts must be greater than zero.");

        long totalMinor = ToMinorUnits();
        long baseShare = totalMinor / parts;
        long remainder = totalMinor % parts;

        var result = new Money[parts];
        for (int i = 0; i < parts; i++)
        {
            long share = baseShare + (i < remainder ? 1 : 0);
            result[i] = FromMinorUnits(share, Currency);
        }

        return result;
    }

    #endregion

    #region Formatting & Validation

    public override string ToString()
    {
        var culture = Currency == Currency.BDT
            ? BdCulture
            : CultureInfo.InvariantCulture;

        return $"{Currency.Symbol()}{Amount.ToString("N2", culture)}";
    }

    public string ToIsoFormattedString()
    {
        return $"{Amount.ToString("F2", CultureInfo.InvariantCulture)} {Currency.ToIsoCode()}";
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new CurrencyMismatchException(left.Currency, right.Currency);
    }

    #endregion
}
