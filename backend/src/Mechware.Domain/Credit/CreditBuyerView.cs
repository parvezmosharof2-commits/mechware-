namespace Mechware.Domain.Credit;

/// <summary>What a buyer may be told about one credit order (ADR-0010 §10, B3.9). Derived from the ledger, never stored.</summary>
public static class CreditBuyerView
{
    public const string Open = "open", PartiallyPaid = "partially_paid", Paid = "paid", Overdue = "overdue", Cancelled = "cancelled";

    /// <summary>
    /// <c>cancelled</c> for a cancelled order; else <c>paid</c> when nothing is owed, <c>overdue</c> when the due date (Dhaka) is before today,
    /// <c>partially_paid</c> when only part of the charge is still owed, otherwise <c>open</c>. Overdue wins over partially paid.
    /// </summary>
    public static string OrderStatus(bool orderCancelled, long chargeMinor, long receivableMinor, DateOnly dueOn, DateOnly today)
    {
        if (orderCancelled) return Cancelled;
        if (receivableMinor <= 0) return Paid;
        if (CreditAging.DaysPastDue(dueOn, today) > 0) return Overdue;
        return receivableMinor < chargeMinor ? PartiallyPaid : Open;
    }

    /// <summary>
    /// The end of a payment reference: at most 4 characters and never more than half of it, so a short reference is not shown whole.
    /// The rest is not part of any buyer answer.
    /// </summary>
    public static string ReferenceTail(string? reference)
    {
        var text = reference?.Trim() ?? string.Empty;
        var shown = Math.Min(4, text.Length / 2);
        return shown == 0 ? string.Empty : "****" + text[^shown..];
    }
}
