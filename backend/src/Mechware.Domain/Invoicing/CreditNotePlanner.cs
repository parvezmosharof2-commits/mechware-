using System.Text;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;

namespace Mechware.Domain.Invoicing;

/// <summary>Why a Mushak-6.7 is issued (ADR-0012 §9.1). Stored as text; the DB CHECK repeats this list.</summary>
public enum CreditNoteReason
{
    GoodsReturned,
    PriceReduction,
    Error,
    Other
}

/// <summary>What the credit notes issued so far carry against one original line (Σ over its credit lines).</summary>
public sealed record CreditedSoFar(int Quantity, long NetMinor, long VatMinor)
{
    public static readonly CreditedSoFar None = new(0, 0, 0);
}

/// <summary>One asked credit line: quantity 0…left; value excl. VAT 1…left, or null = the value of that quantity (remainder rule, §10).</summary>
public sealed record CreditLineRequest(Guid OriginalLineId, int Quantity, long? NetMinor = null);

/// <summary>A credit line as it will be issued. <see cref="FullyCredited"/>: after it nothing of the original line's value is left.</summary>
public sealed record PlannedCreditLine(Guid OriginalLineId, int LineNo, InvoiceLineKind Kind, int Quantity, long NetMinor, long VatMinor, bool FullyCredited)
{
    public long TotalMinor => NetMinor + VatMinor;
}

/// <summary>A planned credit note of one tax invoice: cleaned reason text, lines in the original's line order, plain sums.</summary>
public sealed record CreditNotePlan(Guid OriginalInvoiceId, CreditNoteReason Reason, string ReasonText, IReadOnlyList<PlannedCreditLine> Lines)
{
    public long NetMinor => Lines.Sum(l => l.NetMinor);
    public long VatMinor => Lines.Sum(l => l.VatMinor);
    public long TotalMinor => Lines.Sum(l => l.TotalMinor);
}

/// <summary>
/// The pure half of "never more than the original" (ADR-0012 §9.1–9.2, threat model §১৪ IT3, IT17). Per original line the credited
/// quantity, value excl. VAT and VAT never exceed what is left after the earlier credit notes; per invoice the credit-note totals never
/// exceed the invoice total. VAT of a credit line is <c>round_even(net × rate)</c> of the original line, capped at the VAT left; a line
/// whose value becomes fully credited takes exactly the net and VAT left, so it sums to zero to the poisha. Only a tax invoice can be
/// credited, and only up to <c>CreditNoteMaxAgeDays</c> Dhaka calendar days after its issue. The service (E6.5b) runs this under the
/// original invoice's row lock; the deferred trigger of <c>CreditNoteSql</c> repeats the per-line sums at COMMIT.
/// </summary>
public static class CreditNotePlanner
{
    public const string NotATaxInvoice = "CREDIT_NOTE_OF_CREDIT_NOTE";
    public const string TooLate = "CREDIT_NOTE_TOO_LATE";
    public const string ExceedsOriginal = "CREDIT_NOTE_EXCEEDS_ORIGINAL";
    public const int ReasonTextMinLength = 5, ReasonTextMaxLength = 500, MaxLines = 200;

    public static CreditNotePlan Plan(Invoice original, IReadOnlyDictionary<Guid, CreditedSoFar> credited, IReadOnlyList<CreditLineRequest>? lines,
        CreditNoteReason reason, string? reasonText, DateTime nowUtc, int maxAgeDays)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(credited);
        if (original.Kind != InvoiceKind.TaxInvoice)
            throw new ConflictException(NotATaxInvoice, $"{original.Number} is a credit note; only a tax invoice can be credited.");
        if (IsTooLate(original.IssuedAtUtc, nowUtc, maxAgeDays))
            throw new ConflictException(TooLate, $"{original.Number} was issued more than {maxAgeDays} days ago; settle the money without a credit note.");
        if (!Enum.IsDefined(reason)) throw new ValidationException("reason", "Choose GoodsReturned, PriceReduction, Error or Other.");
        var text = CleanReason(reasonText);
        if (lines is not { Count: >= 1 and <= MaxLines }) throw new ValidationException("lines", $"Credit 1 to {MaxLines} lines.");
        if (lines.Select(l => l.OriginalLineId).Distinct().Count() != lines.Count) throw new ValidationException("lines", "Name each invoice line once.");

        var planned = new List<PlannedCreditLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = original.Lines.SingleOrDefault(l => l.Id == lines[i].OriginalLineId)
                ?? throw new ValidationException($"lines[{i}].originalLineId", "This is not a line of the invoice.");
            planned.Add(PlanLine(line, credited.GetValueOrDefault(line.Id) ?? CreditedSoFar.None, lines[i], $"lines[{i}]"));
        }

        var before = original.Lines.Sum(l => credited.TryGetValue(l.Id, out var c) ? c.NetMinor + c.VatMinor : 0);
        if (before + planned.Sum(l => l.TotalMinor) > original.TotalMinor)
            throw new ConflictException(ExceedsOriginal, $"Credit notes of {original.Number} would exceed its total.");
        return new CreditNotePlan(original.Id, reason, text, [.. planned.OrderBy(l => l.LineNo)]);
    }

    /// <summary>§10 remainder rule: the value of <paramref name="quantity"/> more units is net × quantity / invoiced quantity (banker's), and the
    /// units that complete the line's quantity take the whole value left.</summary>
    public static long NetForQuantity(InvoiceLine line, CreditedSoFar soFar, int quantity)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(soFar);
        var netLeft = line.NetMinor - soFar.NetMinor;
        if (quantity <= 0) return 0;
        if (soFar.Quantity + quantity == line.Quantity) return netLeft;
        return Math.Min(netLeft, (long)decimal.Round((decimal)line.NetMinor * quantity / line.Quantity, 0, MidpointRounding.ToEven));
    }

    /// <summary>Too late once the Dhaka calendar day of <paramref name="nowUtc"/> is after the issue day + <paramref name="maxAgeDays"/>.</summary>
    public static bool IsTooLate(DateTime issuedAtUtc, DateTime nowUtc, int maxAgeDays) =>
        FiscalYear.DhakaDate(nowUtc) > FiscalYear.DhakaDate(issuedAtUtc).AddDays(maxAgeDays);

    /// <summary>
    /// IT17: the reason text as printed on the 6.7 and in the 9.1 export: plain text (<see cref="PlainText"/>: control and invisible format
    /// characters removed), every run of white space (new lines and tabs too) one space, trimmed, 5–500 characters.
    /// </summary>
    public static string CleanReason(string? value)
    {
        var plain = PlainText.Clean(value) ?? string.Empty;
        var sb = new StringBuilder(plain.Length);
        foreach (var ch in plain)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        var text = sb.ToString().Trim();
        if (text.Length is < ReasonTextMinLength or > ReasonTextMaxLength)
            throw new ValidationException("reasonText", $"Give a reason of {ReasonTextMinLength} to {ReasonTextMaxLength} characters.");
        return text;
    }

    private static PlannedCreditLine PlanLine(InvoiceLine line, CreditedSoFar soFar, CreditLineRequest asked, string field)
    {
        if (line.SdMinor != 0) throw new InvalidOperationException("Crediting supplementary duty is not supported (SD is 0 at launch, ADR-0012 §7).");
        var (quantityLeft, netLeft, vatLeft) = (line.Quantity - soFar.Quantity, line.NetMinor - soFar.NetMinor, line.VatMinor - soFar.VatMinor);
        if (quantityLeft < 0 || netLeft < 0 || vatLeft < 0) throw new InvalidOperationException($"Line {line.LineNo} is already credited above the original.");
        if (asked.Quantity < 0) throw new ValidationException($"{field}.quantity", "The quantity cannot be negative.");
        if (asked.NetMinor is < 1) throw new ValidationException($"{field}.netMinor", "Credit at least one poisha.");
        if (asked.Quantity == 0 && asked.NetMinor is null) throw new ValidationException(field, "Give a quantity or a value to credit.");
        if (asked.Quantity > quantityLeft)
            throw new ConflictException(ExceedsOriginal, $"Line {line.LineNo}: at most {quantityLeft} more can be credited.");
        var net = asked.NetMinor ?? NetForQuantity(line, soFar, asked.Quantity);
        if (net > netLeft) throw new ConflictException(ExceedsOriginal, $"Line {line.LineNo}: at most {netLeft} poisha more can be credited.");
        if (net < 1) throw new ConflictException(ExceedsOriginal, $"Line {line.LineNo}: nothing of its value is left to credit.");
        var full = net == netLeft;
        var vat = full ? vatLeft : Math.Min(vatLeft, (long)decimal.Round(net * line.VatRate, 0, MidpointRounding.ToEven));
        return new PlannedCreditLine(line.Id, line.LineNo, line.Kind, asked.Quantity, net, vat, full);
    }
}
