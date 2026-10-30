using FluentAssertions;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Companies;

namespace ShopVerse.UnitTests.Companies;

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

    [Fact]
    public void ReserveCredit_RequiresActiveCompanyAndTerms()
    {
        var pending = NewCompany(activate: false);
        pending.Invoking(c => c.ReserveCredit(Bdt(1))).Should().Throw<ConflictException>().Which.Code.Should().Be("COMPANY_NOT_ACTIVE");

        var noTerms = NewCompany();
        noTerms.Invoking(c => c.ReserveCredit(Bdt(1))).Should().Throw<ConflictException>().Which.Code.Should().Be("NO_CREDIT_TERMS");

        var suspended = NewCompany();
        suspended.SetCredit(CreditTerms.Net30, Bdt(1000));
        suspended.Suspend();
        suspended.Invoking(c => c.ReserveCredit(Bdt(1))).Should().Throw<ConflictException>().Which.Code.Should().Be("COMPANY_NOT_ACTIVE");
    }

    [Fact]
    public void ReserveCredit_AllowsExactlyUpToTheLimit()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net15, Bdt(1000));
        c.ReserveCredit(Bdt(600));
        c.ReserveCredit(Bdt(400));
        c.CreditAvailable.IsZero.Should().BeTrue();
        c.Invoking(x => x.ReserveCredit(new Money(0.01m, Currency.BDT))).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_LIMIT_EXCEEDED");
    }

    [Fact]
    public void ReserveCredit_RejectedOrderChangesNothing()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net30, Bdt(1000));
        c.ReserveCredit(Bdt(900));
        c.Invoking(x => x.ReserveCredit(Bdt(101))).Should().Throw<ConflictException>();
        c.CreditUsed.Should().Be(Bdt(900));
    }

    [Fact]
    public void SetCredit_CannotDropBelowUsed_OrContradictTerms()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net30, Bdt(1000));
        c.ReserveCredit(Bdt(700));

        c.Invoking(x => x.SetCredit(CreditTerms.Net30, Bdt(699))).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_LIMIT_BELOW_USED");
        c.Invoking(x => x.SetCredit(CreditTerms.None, Bdt(0))).Should().Throw<ConflictException>();
        c.Invoking(x => x.SetCredit(CreditTerms.Net15, Bdt(0))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit(CreditTerms.None, Bdt(5))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit(CreditTerms.Net15, Bdt(-5))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit((CreditTerms)7, Bdt(5))).Should().Throw<ValidationException>();
        c.Invoking(x => x.SetCredit(CreditTerms.Net30, new Money(10, Currency.USD))).Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void ReleaseCredit_CannotGoBelowZero()
    {
        var c = NewCompany();
        c.SetCredit(CreditTerms.Net30, Bdt(1000));
        c.ReserveCredit(Bdt(300));
        c.ReleaseCredit(Bdt(300));
        c.CreditUsed.IsZero.Should().BeTrue();
        c.Invoking(x => x.ReleaseCredit(Bdt(1))).Should().Throw<ConflictException>();
    }

    [Fact]
    public void RandomOperations_NeverBreakCreditInvariants()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var c = NewCompany();
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    switch (rng.Next(5))
                    {
                        case 0: c.SetCredit(rng.Next(3) == 0 ? CreditTerms.None : CreditTerms.Net30, Bdt(rng.Next(0, 5000))); break;
                        case 1 or 2: c.ReserveCredit(new Money(rng.Next(-10, 3000) + rng.Next(100) / 100m, Currency.BDT)); break;
                        case 3: c.ReleaseCredit(new Money(rng.Next(-10, 3000), Currency.BDT)); break;
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
        var m = new CompanyMember(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CompanyRoles.Admin, null, null);
        m.Has(CompanyRoles.Admin).Should().BeTrue();
        m.Update(CompanyRoles.Admin, null, null, isActive: false);
        m.Has(CompanyRoles.Admin).Should().BeFalse();
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
