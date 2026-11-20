using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Quotes;

/// <summary>
/// The default terms lines printed on a quote PDF, as they were when a version left Draft (D4.20). Frozen with the version, so a sent
/// quote keeps printing what it was sent with and a re-rendered file has the same SHA-256. <c>{validUntil}</c> stays a placeholder.
/// </summary>
public sealed record QuoteTermsSnapshot(string[] En, string[] Bn);

/// <summary>The staff-editable default terms (D4.20): one row. EN and BN lists, at most 12 lines each, 1 to 300 characters per line, plain text.</summary>
public sealed class QuoteTermsSettings : Entity<Guid>
{
    public const int MaxLines = 12;
    public const int MaxLineLength = 300;

    /// <summary>The one row's id (the table has a CHECK on it).</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000d4200");

    public string[] TermsEn { get; private set; } = [];
    public string[] TermsBn { get; private set; } = [];
    public Guid? UpdatedBy { get; private set; }

    private QuoteTermsSettings() { }

    /// <summary>Control characters are stripped (<see cref="PlainText"/>); an empty line, too many lines or a long line is a 400.</summary>
    public static string[] Normalize(IReadOnlyList<string?>? lines, string field)
    {
        if (lines is null) throw new ValidationException(field, "Send the list of terms lines (it may be empty).");
        if (lines.Count > MaxLines) throw new ValidationException(field, $"At most {MaxLines} lines.");
        var result = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = PlainText.Clean(lines[i])?.Trim() ?? "";
            if (line.Length is 0 or > MaxLineLength) throw new ValidationException($"{field}[{i}]", $"Each line needs 1 to {MaxLineLength} characters.");
            result[i] = line;
        }

        return result;
    }

    public void Change(string[] en, string[] bn, Guid updatedBy, DateTime nowUtc)
    {
        if (updatedBy == Guid.Empty) throw new ValidationException("updatedBy", "The author is required.");
        (TermsEn, TermsBn, UpdatedBy, UpdatedAtUtc) = (en, bn, updatedBy, nowUtc);
    }

    public QuoteTermsSnapshot ToSnapshot() => new([.. TermsEn], [.. TermsBn]);
}
