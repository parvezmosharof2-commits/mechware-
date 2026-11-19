namespace Mechware.Domain.Quotes;

/// <summary>
/// The working-hours clock of the "first quote within 4 working hours" KPI (ADR-0011 §15 decision 12): Sunday to Thursday,
/// 09:00 to 18:00 in Dhaka (a fixed +06:00), minus the owner's public holidays (owner item c; empty until given). Pure: the
/// inbox shows the due time and the working time an RFQ has waited; nothing is stored and nothing is refused by it.
/// </summary>
public static class QuoteSla
{
    private static readonly TimeSpan DayStart = TimeSpan.FromHours(9), DayEnd = TimeSpan.FromHours(18);

    public static bool IsWorkingDay(DateOnly dhakaDate, IReadOnlyCollection<DateOnly> holidays) =>
        dhakaDate.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) && !holidays.Contains(dhakaDate);

    /// <summary>The instant <paramref name="workingMinutes"/> of working time after <paramref name="startUtc"/>; time outside working hours does not count.</summary>
    public static DateTime DueAtUtc(DateTime startUtc, int workingMinutes, IReadOnlyCollection<DateOnly> holidays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workingMinutes);
        var left = TimeSpan.FromMinutes(workingMinutes);
        var local = ToDhaka(startUtc);
        for (var guard = 0; guard < 3_700; guard++)   // ten years of days: holidays cannot make it loop for ever
        {
            var day = DateOnly.FromDateTime(local);
            var (open, close) = (day.ToDateTime(TimeOnly.MinValue) + DayStart, day.ToDateTime(TimeOnly.MinValue) + DayEnd);
            if (IsWorkingDay(day, holidays) && local < close)
            {
                var from = local < open ? open : local;
                if (from + left <= close) return ToUtc(from + left);
                left -= close - from;
            }

            local = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        }

        throw new InvalidOperationException("The SLA clock found no working day in ten years; check Quotes:SlaHolidays.");
    }

    /// <summary>Working minutes from <paramref name="fromUtc"/> to <paramref name="toUtc"/> (0 when the end is not later).</summary>
    public static int WorkingMinutes(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<DateOnly> holidays)
    {
        var (from, to) = (ToDhaka(fromUtc), ToDhaka(toUtc));
        var total = TimeSpan.Zero;
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to); day = day.AddDays(1))
        {
            if (!IsWorkingDay(day, holidays)) continue;
            var (open, close) = (day.ToDateTime(TimeOnly.MinValue) + DayStart, day.ToDateTime(TimeOnly.MinValue) + DayEnd);
            var (start, end) = (from > open ? from : open, to < close ? to : close);
            if (end > start) total += end - start;
        }

        return (int)total.TotalMinutes;
    }

    private static DateTime ToDhaka(DateTime utc) => DateTime.SpecifyKind(DateTime.SpecifyKind(utc, DateTimeKind.Utc) + QuoteValidity.DhakaOffset, DateTimeKind.Unspecified);

    private static DateTime ToUtc(DateTime dhaka) => DateTime.SpecifyKind(dhaka - QuoteValidity.DhakaOffset, DateTimeKind.Utc);
}
