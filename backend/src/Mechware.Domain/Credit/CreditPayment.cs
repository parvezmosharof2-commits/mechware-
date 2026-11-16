using System.Globalization;
using System.Text;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Credit;

/// <summary>How a customer paid on credit (ADR-0010 §4; manual entry, gateway payments stay E5). Stored snake_case.</summary>
public enum CreditPaymentMethod { Cheque = 1, PayOrder = 2, Beftn = 3, Rtgs = 4, Npsb = 5, BankDeposit = 6, Cash = 7, Bkash = 8, Nagad = 9 }

/// <summary>Recorded → Verified | Rejected; Verified → Bounced (never cash). Only a verification releases credit (§5).</summary>
public enum CreditPaymentStatus { Recorded = 1, Verified = 2, Rejected = 3, Bounced = 4 }

/// <summary>The owner's payment settings (owner decision 4), built by <c>CreditOptions.PaymentRules()</c>.</summary>
public sealed record CreditPaymentRules(IReadOnlySet<CreditPaymentMethod> Methods, long MaxPaymentMinor, long MaxCashPaymentMinor, bool AcceptPostDatedCheques);

/// <summary>What finance enters for one payment (ADR-0010 §4); <see cref="CreditPayment.Record"/> normalises the texts.</summary>
public sealed record CreditPaymentInput(Guid CompanyId, CreditPaymentMethod Method, long AmountMinor, string? Reference, string? DrawerBank,
    DateOnly ReceivedOn, DateOnly? ChequeDate = null, string? LateEntryReason = null, IReadOnlyList<CreditAllocationRequest>? Allocations = null,
    string? Note = null);

/// <summary>
/// A customer's payment against its credit (ADR-0010 §4, §5, §8.1; threat model CS2, CS4, CS10, CS11, CS31). Recording does
/// nothing to credit; B3.4 releases credit only after <see cref="Verify"/> by someone other than the recorder. Reference and
/// bank are normalised so differently written copies meet in the duplicate indexes; dates are Dhaka dates. The database
/// repeats the four-eyes rule (CHECK) and refuses every change but the next step (<c>CreditLedgerSql.InstallSettlement</c>).
/// </summary>
public sealed class CreditPayment : Entity<Guid>
{
    public const int LateAfterDays = 30, ChequeWindowDays = 180, MaxTextLength = 500;
    public const int MinReferenceLength = 4, MaxReferenceLength = 40, MaxBankLength = 60, MaxIdempotencyKeyLength = 100;

    public Guid CompanyId { get; private set; }
    public CreditPaymentMethod Method { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = CreditLedgerEntry.BdtCurrency;
    /// <summary>Normalised: A-Z and 0-9 only. Cash: the receipt number <c>CR{yyMMdd}{6}</c> issued here. Wallets: the TrxID only.</summary>
    public string Reference { get; private set; } = string.Empty;
    /// <summary>Cheque, pay order and deposit only, normalised like the reference.</summary>
    public string? DrawerBank { get; private set; }
    public DateOnly ReceivedOn { get; private set; }
    /// <summary>Cheque and pay order only; a later date is a post-dated cheque, verified no earlier than this day.</summary>
    public DateOnly? ChequeDate { get; private set; }
    /// <summary>Set exactly when the payment was received more than 30 days before it was recorded (the queue flags it).</summary>
    public string? LateEntryReason { get; private set; }
    public IReadOnlyList<CreditAllocationRequest> Allocations { get; private set; } = [];
    /// <summary>Staff only; never shown to the buyer.</summary>
    public string? Note { get; private set; }
    public CreditPaymentStatus Status { get; private set; } = CreditPaymentStatus.Recorded;
    public Guid RecordedBy { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public DateTime? VerifiedAtUtc { get; private set; }
    /// <summary>The day the money was in MECHWARE's bank.</summary>
    public DateOnly? ClearedOn { get; private set; }
    public Guid? RejectedBy { get; private set; }
    public DateTime? RejectedAtUtc { get; private set; }
    public Guid? BouncedBy { get; private set; }
    public DateTime? BouncedAtUtc { get; private set; }
    /// <summary>Why it was rejected or bounced.</summary>
    public string? DecisionReason { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    /// <summary>SHA-256 (lower-case hex) of the recorded request: a replay with another body is refused (B3.4).</summary>
    public string RequestHash { get; private set; } = string.Empty;

    private CreditPayment() { }

    private CreditPayment(Guid id) : base(id) { }

    public static CreditPayment Record(Guid id, CreditPaymentInput input, Guid recordedBy, string idempotencyKey, string requestHash,
        CreditPaymentRules rules, DateTime nowUtc)
    {
        var (m, today) = (input.Method, CreditCalendar.DhakaDate(nowUtc));
        if (!rules.Methods.Contains(m)) throw new ValidationException("method", "This payment method is not accepted.");
        var max = m == CreditPaymentMethod.Cash ? Math.Min(rules.MaxCashPaymentMinor, rules.MaxPaymentMinor) : rules.MaxPaymentMinor;
        if (input.AmountMinor < 1 || input.AmountMinor > max) throw new ValidationException("amountMinor", $"The amount must be 1 to {max} poisha.");
        if (input.ReceivedOn > today) throw new ValidationException("receivedOn", "A payment cannot be received in the future.");
        var lateReason = Text(input.LateEntryReason, "lateEntryReason", 10);
        var late = input.ReceivedOn < today.AddDays(-LateAfterDays);
        if (late != (lateReason is not null))
            throw new ValidationException("lateEntryReason", $"A reason is needed exactly when the payment was received more than {LateAfterDays} days ago.");

        bool cheque = m is CreditPaymentMethod.Cheque or CreditPaymentMethod.PayOrder, bank = cheque || m == CreditPaymentMethod.BankDeposit;
        if (cheque != input.ChequeDate.HasValue) throw new ValidationException("chequeDate", "A cheque or pay order, and only those, has a cheque date.");
        if (input.ChequeDate is { } d && (d < input.ReceivedOn.AddDays(-ChequeWindowDays) || d > input.ReceivedOn.AddDays(ChequeWindowDays)))
            throw new ValidationException("chequeDate", $"The cheque date must be within {ChequeWindowDays} days of the day it was received.");
        if (input.ChequeDate > today && !rules.AcceptPostDatedCheques) throw new ValidationException("chequeDate", "Post-dated cheques are not accepted.");
        if (!bank && !string.IsNullOrWhiteSpace(input.DrawerBank)) throw new ValidationException("drawerBank", "Only a cheque, pay order or deposit names a bank.");
        if (m == CreditPaymentMethod.Cash && !string.IsNullOrWhiteSpace(input.Reference))
            throw new ValidationException("reference", "A cash payment gets a receipt number; it takes no reference.");
        var reference = m == CreditPaymentMethod.Cash
            ? "CR" + input.ReceivedOn.ToString("yyMMdd", CultureInfo.InvariantCulture) + id.ToString("N")[..6].ToUpperInvariant()
            : Normalise(input.Reference, "reference", MinReferenceLength, MaxReferenceLength);
        if (m is CreditPaymentMethod.Bkash or CreditPaymentMethod.Nagad && reference.All(char.IsAsciiDigit)
            && (reference is { Length: 11 } && reference.StartsWith("01", StringComparison.Ordinal) || reference is { Length: 13 } && reference.StartsWith("8801", StringComparison.Ordinal)))
            throw new ValidationException("reference", "Enter the transaction id (TrxID), never the wallet's phone number.");
        var allocations = (input.Allocations ?? []).ToList();
        CreditAllocationPlanner.CheckRequests(allocations, input.AmountMinor);
        if (idempotencyKey is not { Length: >= 8 and <= MaxIdempotencyKeyLength } || idempotencyKey.Any(c => c is <= ' ' or > '~'))
            throw new ValidationException("idempotencyKey", $"The Idempotency-Key is 8 to {MaxIdempotencyKeyLength} printable ASCII characters.");
        if (requestHash is not { Length: 64 } || !requestHash.All(char.IsAsciiHexDigitLower))
            throw new ValidationException("requestHash", "The request hash is a lower-case SHA-256 hex string.");

        return new CreditPayment(id)
        {
            CompanyId = input.CompanyId,
            Method = m,
            AmountMinor = input.AmountMinor,
            Reference = reference,
            DrawerBank = bank ? Normalise(input.DrawerBank, "drawerBank", 2, MaxBankLength) : null,
            ReceivedOn = input.ReceivedOn,
            ChequeDate = input.ChequeDate,
            LateEntryReason = lateReason,
            Allocations = allocations,
            Note = Text(input.Note, "note", 1),
            RecordedBy = recordedBy,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    /// <summary>Four-eyes (CS2): someone other than the recorder; a post-dated cheque not before its date (CS11).</summary>
    public void Verify(Guid verifierId, DateOnly clearedOn, DateTime nowUtc)
    {
        Expect(CreditPaymentStatus.Recorded, "CREDIT_PAYMENT_NOT_RECORDED");
        NotTheRecorder(verifierId);
        var today = CreditCalendar.DhakaDate(nowUtc);
        if (ChequeDate is { } chequeDate && chequeDate > today)
            throw new ConflictException("CREDIT_PAYMENT_CHEQUE_NOT_DUE", $"A post-dated cheque cannot be verified before its date, {chequeDate:O}.");
        var earliest = ChequeDate > ReceivedOn ? ChequeDate.Value : ReceivedOn;
        if (clearedOn < earliest || clearedOn > today)
            throw new ValidationException("clearedOn", $"The money cleared between {earliest:O} and today.");
        (Status, VerifiedBy, VerifiedAtUtc, ClearedOn, UpdatedAtUtc) = (CreditPaymentStatus.Verified, verifierId, nowUtc, clearedOn, nowUtc);
    }

    /// <summary>A mistaken entry is rejected by someone other than the recorder (§5); it frees its reference for a new entry.</summary>
    public void Reject(Guid userId, string reason, DateTime nowUtc)
    {
        Expect(CreditPaymentStatus.Recorded, "CREDIT_PAYMENT_NOT_RECORDED");
        NotTheRecorder(userId);
        (Status, RejectedBy, RejectedAtUtc, DecisionReason, UpdatedAtUtc) = (CreditPaymentStatus.Rejected, userId, nowUtc, Reason(reason), nowUtc);
    }

    /// <summary>The bank returned it (§8.1): any verifier, reason required; cash never bounces (an adjustment corrects it).</summary>
    public void Bounce(Guid userId, string reason, DateTime nowUtc)
    {
        Expect(CreditPaymentStatus.Verified, "CREDIT_PAYMENT_NOT_VERIFIED");
        if (Method == CreditPaymentMethod.Cash)
            throw new ConflictException("CREDIT_PAYMENT_CASH_CANNOT_BOUNCE", "Cash does not bounce; correct a mistaken cash entry with an adjustment.");
        (Status, BouncedBy, BouncedAtUtc, DecisionReason, UpdatedAtUtc) = (CreditPaymentStatus.Bounced, userId, nowUtc, Reason(reason), nowUtc);
    }

    /// <summary>
    /// The duplicate key of a reference or bank name (CS4): after NFKC (full-width forms), spaces, punctuation and symbols are
    /// dropped, letters upper-cased and digits of any script read as 0-9; anything else (a Cyrillic look-alike, a Bengali
    /// letter) is refused. So "ab-123", "AB 123" and "ＡＢ১২৩" are one key, and no look-alike makes a second one.
    /// </summary>
    public static string Normalise(string? text, string field, int minLength, int maxLength)
    {
        var key = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            if (char.IsDigit(c)) key.Append((char)('0' + (int)char.GetNumericValue(c)));
            else if (char.IsAsciiLetter(c)) key.Append(char.ToUpperInvariant(c));
            else throw new ValidationException(field, "Use the letters A-Z and digits only.");
            if (key.Length > maxLength) break;
        }
        if (key.Length < minLength || key.Length > maxLength) throw new ValidationException(field, $"Give {minLength} to {maxLength} letters or digits.");
        return key.ToString();
    }

    private static string? Text(string? value, string field, int minLength)
    {
        var t = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (t is not null && (t.Length < minLength || t.Length > MaxTextLength))
            throw new ValidationException(field, $"Give {minLength} to {MaxTextLength} characters.");
        return t;
    }

    private static string Reason(string reason) => Text(reason, "reason", 5) ?? throw new ValidationException("reason", "Give a reason of 5 to 500 characters.");

    private void Expect(CreditPaymentStatus status, string code)
    {
        if (Status != status) throw new ConflictException(code, $"A {Status} payment cannot do this.");
    }

    private void NotTheRecorder(Guid userId)
    {
        if (userId == RecordedBy)
            throw new ConflictException("CREDIT_PAYMENT_FOUR_EYES", "The person who recorded a payment cannot verify or reject it. Someone else must.");
    }
}
