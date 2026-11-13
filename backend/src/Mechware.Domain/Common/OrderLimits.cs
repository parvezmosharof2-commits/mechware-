namespace Mechware.Domain.Common;

/// <summary>
/// Limits shared by the catalog (minimum order quantity) and the cart (lines, quantity per line). They live in the
/// shared kernel so the catalog never has to know about the cart.
/// </summary>
public static class OrderLimits
{
    public const int MaxQuantityPerLine = 10_000;
    public const int MaxCartLines = 100;
}
