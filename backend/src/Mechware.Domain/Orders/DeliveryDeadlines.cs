namespace Mechware.Domain.Orders;

/// <summary>
/// The two legal delivery deadlines of an order (E8.3; brief + Guideline 2021, the lawyer confirms the numbers, so they come
/// from configuration). Computed once, at confirmation, and stored: a later change of status or of the configuration never moves them.
/// </summary>
/// <param name="LegalDeliveryDeadlineUtc">Delivery to the buyer, counted from confirmation.</param>
/// <param name="CourierHandoverDeadlineUtc">Handover to the courier, counted from confirmation (the advance payment).</param>
/// <param name="SameCity">True: dispatch location and destination share a district. False: they do not. Null: unknown, treated as same city.</param>
public sealed record DeliveryDeadlines(DateTime LegalDeliveryDeadlineUtc, DateTime CourierHandoverDeadlineUtc, bool? SameCity)
{
    /// <summary>
    /// Unknown counts as same city: the shorter period is the strict one for the seller, so a missing district can never
    /// give a buyer a later date than the law allows.
    /// </summary>
    public static DeliveryDeadlines Compute(DateTime confirmedAtUtc, bool? sameCity, int sameCityDays, int otherCityDays, int handoverHours)
    {
        if (sameCityDays <= 0 || otherCityDays <= 0 || handoverHours <= 0)
            throw new ArgumentOutOfRangeException(nameof(sameCityDays), "Delivery periods must be positive.");
        var days = sameCity == false ? otherCityDays : sameCityDays;
        return new DeliveryDeadlines(confirmedAtUtc.AddDays(days), confirmedAtUtc.AddHours(handoverHours), sameCity);
    }
}
