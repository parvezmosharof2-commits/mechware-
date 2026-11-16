using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.Application.Credit;

/// <summary>When the 15/30 credit days start (ADR-0010 §13, owner decision 1).</summary>
public enum CreditDueDateStart { Confirmation = 1, Delivery = 2 }

/// <summary>
/// Credit settlement settings (section <c>Credit</c>, ADR-0010 §13). Every value is a DEFAULT PENDING OWNER CONFIRMATION:
/// the owner changes it here, not in code. A value outside its range stops the host at startup (<see cref="Validated"/>).
/// B3.3 uses <see cref="DueDateStart"/> only; the others are read by the B3 units named on each.
/// </summary>
public sealed class CreditOptions
{
    public const string SectionName = "Credit";
    public const string AllPaymentMethods = "cheque,pay_order,beftn,rtgs,npsb,bank_deposit,cash,bkash,nagad";

    /// <summary>Owner 1 (B3.3): Confirmation. Delivery needs courier delivery dates (F1) and is refused until it is built.</summary>
    public CreditDueDateStart DueDateStart { get; set; } = CreditDueDateStart.Confirmation;
    /// <summary>Owner 2 (B3.7): new credit orders are refused once a charge is this many days past due; 0 = flag only, no hold.</summary>
    public int HoldAfterDaysPastDue { get; set; } = 15;
    /// <summary>Owner 2 (B3.7): the longest an Admin may lift the hold, 1-7 days.</summary>
    public int HoldOverrideMaxDays { get; set; } = 7;
    /// <summary>Owner 3 (B3.4): an authenticator code on every verification; when false, only at or above <see cref="VerifyStepUpFromMinor"/>.</summary>
    public bool StepUpOnEveryVerification { get; set; } = true;
    public long VerifyStepUpFromMinor { get; set; } = 10_000_000;              // BDT 1,00,000
    /// <summary>Owner 4 (B3.4): accepted methods (comma-separated, from <see cref="AllPaymentMethods"/>), post-dated cheques, limits.</summary>
    public string PaymentMethods { get; set; } = AllPaymentMethods;
    public bool AcceptPostDatedCheques { get; set; } = true;
    public long MaxPaymentMinor { get; set; } = 10_000_000_000;               // BDT 10 crore
    public long MaxCashPaymentMinor { get; set; } = 20_000_000;               // BDT 2,00,000 (the accountant to confirm)
    /// <summary>Owner 5 (B3.6): over-payments stay on account for the next orders (true); paying back at once is not built.</summary>
    public bool KeepOverPaymentsOnAccount { get; set; } = true;
    /// <summary>Owner 6 (B3.9): the company roles that see the balance and the statement.</summary>
    public CompanyRoles BalanceVisibleTo { get; set; } = CompanyRoles.Admin | CompanyRoles.Buyer;

    /// <summary>Owner 4 as the domain reads it (B3.4 passes it to <see cref="CreditPayment.Record"/>); call on <see cref="Validated"/> options.</summary>
    public CreditPaymentRules PaymentRules() => new(
        PaymentMethods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(m => Enum.Parse<CreditPaymentMethod>(m.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true)).ToHashSet(),
        MaxPaymentMinor, MaxCashPaymentMinor, AcceptPostDatedCheques);

    /// <summary>When the credit days of an order confirmed at <paramref name="confirmedAtUtc"/> start.</summary>
    public DateTime TermsStartUtc(DateTime confirmedAtUtc) => DueDateStart == CreditDueDateStart.Confirmation
        ? confirmedAtUtc
        : throw new InvalidOperationException("Credit:DueDateStart=Delivery is not built yet.");

    /// <summary>The options as read from configuration, or the startup error naming the setting.</summary>
    public static CreditOptions Validated(CreditOptions? o)
    {
        o ??= new CreditOptions();
        static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException($"{SectionName}:{message}");
        }
        Require(o.DueDateStart == CreditDueDateStart.Confirmation,
            "DueDateStart must be Confirmation; Delivery needs courier delivery dates (F1) and a due-date line, not built yet.");
        Require(o.HoldAfterDaysPastDue is >= 0 and <= 365, "HoldAfterDaysPastDue must be 0 (no hold) to 365.");
        Require(o.HoldOverrideMaxDays is >= 1 and <= 7, "HoldOverrideMaxDays must be 1 to 7.");
        Require(o.VerifyStepUpFromMinor >= 1, "VerifyStepUpFromMinor must be at least 1 poisha.");
        var methods = o.PaymentMethods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Require(methods.Length > 0 && methods.All(m => AllPaymentMethods.Split(',').Contains(m)),
            $"PaymentMethods must be a comma-separated list from {AllPaymentMethods}.");
        Require(o.MaxPaymentMinor is >= 1 and <= 1_000_000_000_000, "MaxPaymentMinor must be 1 to 1,000,000,000,000 poisha.");
        Require(o.MaxCashPaymentMinor >= 0 && o.MaxCashPaymentMinor <= o.MaxPaymentMinor, "MaxCashPaymentMinor must be 0 to MaxPaymentMinor.");
        Require(o.KeepOverPaymentsOnAccount, "KeepOverPaymentsOnAccount=false (pay back at once) is not built yet.");
        Require(o.BalanceVisibleTo.IsValid(), "BalanceVisibleTo must name company roles (Admin, Buyer, Approver, Requester).");
        return o;
    }
}
