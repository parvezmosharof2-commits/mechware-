using FluentAssertions;
using Mechware.Application.Credit;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>B3.9 (ADR-0010 §10, threat model CS22-CS24): what a buyer is told about an order, the end of a reference, the visibility default and the exact fields.</summary>
public class BuyerCreditTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    [Theory]
    [InlineData(false, 10_000, 10_000, 0, "open")]            // due today is still open
    [InlineData(false, 10_000, 10_000, 5, "open")]            // not due yet
    [InlineData(false, 10_000, 4_000, 5, "partially_paid")]
    [InlineData(false, 10_000, 4_000, 0, "partially_paid")]
    [InlineData(false, 10_000, 10_000, -1, "overdue")]        // one day past due
    [InlineData(false, 10_000, 4_000, -1, "overdue")]         // overdue wins over partially paid
    [InlineData(false, 10_000, 0, -40, "paid")]               // paid wins over a long-gone due date
    [InlineData(false, 10_000, -1, 5, "paid")]
    [InlineData(true, 10_000, 0, -40, "cancelled")]
    [InlineData(true, 10_000, 10_000, 5, "cancelled")]
    public void AnOrderStatus_FollowsTheLedger(bool cancelled, long charge, long receivable, int dueInDays, string expected) =>
        CreditBuyerView.OrderStatus(cancelled, charge, receivable, Today.AddDays(dueInDays), Today).Should().Be(expected);

    [Theory]
    [InlineData("RTGS1234567890", "****7890")]
    [InlineData("ABCDEFGH", "****EFGH")]
    [InlineData("ABCDEFG", "****EFG")]                         // never more than half
    [InlineData("1234", "****34")]
    [InlineData("AB", "****B")]
    [InlineData("A", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void AReferenceShowsOnlyItsEnd(string? reference, string shown) => CreditBuyerView.ReferenceTail(reference).Should().Be(shown);

    [Fact]
    public void TheBalanceIsVisibleToAdminsAndBuyersOnly_ByDefault()
    {
        var visible = new CreditOptions().BalanceVisibleTo;
        (visible & CompanyRoles.Admin).Should().NotBe(CompanyRoles.None);
        (visible & CompanyRoles.Buyer).Should().NotBe(CompanyRoles.None);
        (visible & CompanyRoles.Approver).Should().Be(CompanyRoles.None);
        (visible & CompanyRoles.Requester).Should().Be(CompanyRoles.None);
    }

    /// <summary>CS23: the buyer DTOs carry exactly these fields. A new field has to be added here on purpose, after asking whether a buyer may see it.</summary>
    [Fact]
    public void BuyerStatement_HasOnlyTheAllowedFields()
    {
        Names<BuyerStatementLineDto>().Should().BeEquivalentTo("Seq", "OccurredOn", "Kind", "OrderNumber", "PaymentMethod", "ReferenceTail",
            "ReceivableDeltaMinor", "OnAccountDeltaMinor", "ReceivableAfterMinor", "OnAccountAfterMinor", "DueOn");
        Names<BuyerStatementDto>().Should().BeEquivalentTo("CompanyId", "From", "To", "CurrentReceivableMinor", "CurrentOnAccountMinor", "Lines");
        Names<BuyerOpenItemDto>().Should().BeEquivalentTo("OrderNumber", "ChargeMinor", "ReceivableMinor", "DueOn", "DaysPastDue", "Status");
        Names<BuyerPendingPaymentDto>().Should().BeEquivalentTo("ReceivedOn", "Method", "AmountMinor", "ReferenceTail");
        Names<BuyerCreditDto>().Should().BeEquivalentTo("CompanyId", "Currency", "AsOf", "Terms", "LimitMinor", "UsedMinor", "AvailableMinor", "OnAccountMinor",
            "OverdueMinor", "OldestDueOn", "OnHold", "Aging", "PendingPayments");
    }

    private static IEnumerable<string> Names<T>() => typeof(T).GetProperties().Select(p => p.Name);
}
