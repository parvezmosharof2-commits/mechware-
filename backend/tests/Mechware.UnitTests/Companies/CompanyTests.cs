using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;

namespace Mechware.UnitTests.Companies;

public class CompanyTests
{
    private const string Bin = "0012345678901";

    private static Company NewCompany(bool activate = true)
    {
        var c = Company.Register(Guid.NewGuid(), "Square Textiles Ltd", "TL-2020-1234", Bin, "123456789012", "Textile");
        if (activate) c.Activate();
        return c;
    }

    private static Money Bdt(long taka) => new(taka, Currency.BDT);

    [Fact]
    public void Register_StartsPending_WithNoCredit()
    {
        var c = NewCompany(activate: false);
        c.Status.Should().Be(CompanyStatus.Pending);
        c.CreditTerms.Should().Be(CreditTerms.None);
        c.CreditLimit.IsZero.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "TL-1", Bin, null, "legalName")]
    [InlineData("A", "TL-1", Bin, null, "legalName")]
    [InlineData("Acme", "", Bin, null, "tradeLicenceNo")]
    [InlineData("Acme", "TL-1", "123", null, "bin")]
    [InlineData("Acme", "TL-1", "00123456789AB", null, "bin")]
    [InlineData("Acme", "TL-1", "00123456789012", null, "bin")]
    [InlineData("Acme", "TL-1", Bin, "12345", "tin")]
    public void Register_RejectsInvalidInput(string name, string licence, string bin, string? tin, string field)
    {
        var act = () => Company.Register(Guid.NewGuid(), name, licence, bin, tin, null);
        act.Should().Throw<ValidationException>().Which.Errors.Select(e => e.Field).Should().Contain(field);
    }

    // B3.3: ReserveCredit became ChargeCredit (ADR-0010 §3): same checks, and it returns the ledger line.
    private static Mechware.Domain.Credit.CreditLedgerEntry Charge(Company c, long taka) =>
        c.ChargeCredit(Guid.NewGuid(), taka * 100, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void ChargeCredit_RequiresActiveCompanyAndTerms()
    {
        var pending = NewCompany(activate: false);
        pending.Invoking(c => Charge(c, 1)).Should().Throw<ConflictException>().Which.Code.Should().Be("COMPANY_NOT_ACTIVE");

        var noTerms = NewCompany();
        noTerms.Invoking(c => Charge(c, 1)).Should().Throw<ConflictException>().Which.Code.Should().Be("NO_CREDIT_TERMS");

        var suspended = NewCompany();
        suspended.SetCredit(CreditTerms.Net30, Bdt(1000));
        suspended.Suspend();
        suspended.Invoking(c => Charge(c, 1)).Should().Throw<ConflictException>().Which.Code.Should().Be("COMPANY_NOT_ACTIVE");
    }

    [Fact]
    public void ChargeCredit_AllowsExactlyUpToTheLimit()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net15, Bdt(1000));
        Charge(c, 600);
        Charge(c, 400);
        c.CreditAvailable.IsZero.Should().BeTrue();
        c.Invoking(x => x.ChargeCredit(Guid.NewGuid(), 1, DateTime.UtcNow, DateTime.UtcNow)).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_LIMIT_EXCEEDED");
    }

    [Fact]
    public void ChargeCredit_RejectedOrderChangesNothing()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net30, Bdt(1000));
        Charge(c, 900);
        c.Invoking(x => Charge(x, 101)).Should().Throw<ConflictException>();
        c.CreditUsed.Should().Be(Bdt(900));
        c.CreditLedgerSeq.Should().Be(1, "a refused charge writes no line");
    }

    [Fact]
    public void SetCredit_CannotDropBelowUsed_OrContradictTerms()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net30, Bdt(1000));
        Charge(c, 700);

        c.Invoking(x => x.SetCredit(CreditTerms.Net30, Bdt(699))).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_LIMIT_BELOW_USED");
        c.Invoking(x => x.SetCredit(CreditTerms.None, Bdt(0))).Should().Throw<ConflictException>();
        c.Invoking(x => x.SetCredit(CreditTerms.Net15, Bdt(0))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit(CreditTerms.None, Bdt(5))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit(CreditTerms.Net15, Bdt(-5))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit((CreditTerms)7, Bdt(5))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit(CreditTerms.Net30, new Money(10, Currency.USD))).Should().Throw<CurrencyMismatchException>();
    }

    // B3.3: credit is released only by a ledger line now (ReleaseCredit is gone); the rule is the same.
    [Fact]
    public void ReleaseCredit_CannotGoBelowZero()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net30, Bdt(1000));
        var ledger = new Mechware.Domain.Credit.CreditLedger(c, []);
        var order = Guid.NewGuid();
        ledger.Charge(order, 30_000, DateTime.UtcNow, DateTime.UtcNow);
        ledger.Post(Paid(order, 30_000), DateTime.UtcNow);
        c.CreditUsed.IsZero.Should().BeTrue();
        ledger.Invoking(l => l.Post(Paid(order, 1), DateTime.UtcNow)).Should().Throw<ConflictException>();
    }

    private static Mechware.Domain.Credit.CreditPosting Paid(Guid order, long minor) =>
        new(Mechware.Domain.Credit.CreditLedgerKind.PaymentApplied, -minor, 0, $"verify:{Guid.NewGuid():N}:{order:N}",
            DateOnly.FromDateTime(DateTime.UtcNow), OrderId: order, PaymentId: Guid.NewGuid());

    [Fact]
    public void RandomOperations_NeverBreakCreditInvariants()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var c = NewCompany();
            var ledger = new Mechware.Domain.Credit.CreditLedger(c, []);
            var orders = new List<Guid>();
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    switch (rng.Next(5))
                    {
                        case 0: c.SetCredit(rng.Next(3) == 0 ? CreditTerms.None : CreditTerms.Net30, Bdt(rng.Next(0, 5000))); break;
                        case 1 or 2:
                            var id = Guid.NewGuid();
                            ledger.Charge(id, rng.Next(-1000, 300_000), DateTime.UtcNow, DateTime.UtcNow);
                            orders.Add(id);
                            break;
                        case 3 when orders.Count > 0: ledger.Post(Paid(orders[rng.Next(orders.Count)], rng.Next(1, 300_000)), DateTime.UtcNow); break;
                        default: if (rng.Next(2) == 0) c.Suspend(); else c.Activate(); break;
                    }
                }
                catch (DomainException) { /* rejected operations are fine; they must not corrupt state */ }

                c.CreditUsedMinor.Should().BeInRange(0, c.CreditLimitMinor, $"seed {seed} iteration {i}");
                (c.CreditTerms != CreditTerms.None || c.CreditLimitMinor == 0).Should().BeTrue($"seed {seed} iteration {i}");
            }
        }
    }

    [Theory]
    [InlineData(CompanyRoles.None, false)]
    [InlineData(CompanyRoles.Requester, true)]
    [InlineData(CompanyRoles.Buyer | CompanyRoles.Approver, true)]
    [InlineData(CompanyRoles.Admin, true)]
    [InlineData((CompanyRoles)16, false)]
    [InlineData((CompanyRoles)17, false)]
    public void Roles_AreValidatedAgainstKnownFlags(CompanyRoles roles, bool valid) => roles.IsValid().Should().Be(valid);

    [Fact]
    public void InactiveMember_HasNoRoles()
    {
        var m = new CompanyMember(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CompanyRoles.Admin, null, null, accepted: true);
        m.Has(CompanyRoles.Admin).Should().BeTrue();
        m.Update(CompanyRoles.Admin, null, null, isActive: false);
        m.Has(CompanyRoles.Admin).Should().BeFalse();
    }

    [Fact]
    public void AnInvitation_GrantsNothing_UntilAccepted_AndIsAcceptedOnce()
    {
        var m = new CompanyMember(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CompanyRoles.Admin | CompanyRoles.Approver, null, null);
        m.IsAccepted.Should().BeFalse();
        m.Has(CompanyRoles.Admin).Should().BeFalse("an invitation the person has not accepted is not a membership");
        m.Has(CompanyRoles.Approver).Should().BeFalse();

        m.Accept(DateTime.UtcNow);
        m.Has(CompanyRoles.Admin).Should().BeTrue();

        var again = () => m.Accept(DateTime.UtcNow);
        again.Should().Throw<ConflictException>().Which.Code.Should().Be("INVITATION_ALREADY_ACCEPTED");
    }

    [Fact]
    public void Member_RejectsNegativeSpendLimit_AndEmptyRoles()
    {
        var act1 = () => new CompanyMember(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CompanyRoles.Buyer, -1, null);
        var act2 = () => new CompanyMember(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CompanyRoles.None, null, null);
        act1.Should().Throw<ValidationException>();
        act2.Should().Throw<ValidationException>();
    }
}
