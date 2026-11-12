namespace Mechware.Domain.Identity;

/// <summary>
/// Bangladeshi mobile numbers, the only destinations an OTP SMS may go to (F3a.2a: international SMS pumping costs the
/// sender per message). Every accepted spelling becomes one canonical form, <c>+8801[3-9]XXXXXXXX</c>, so "01711…",
/// "8801711…", "+880 1711-…" and Bengali digits all count against the same per-number limit.
/// </summary>
public static class BdMobileNumber
{
    private const int MaxInputLength = 32;

    /// <summary>The canonical <c>+8801[3-9]XXXXXXXX</c> form, or false when the input is not a Bangladeshi mobile number.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxInputLength)
        {
            return false;
        }

        var digits = new System.Text.StringBuilder(16);
        string trimmed = input.Trim();
        bool international = false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '+' && i == 0)
            {
                international = true;
            }
            else if (c is >= '0' and <= '9')
            {
                digits.Append(c);
            }
            else if (c is >= '০' and <= '৯')
            {
                // Bengali digits ০-৯, as a phone keyboard in Bangla types them.
                digits.Append((char)('0' + (c - '০')));
            }
            else if (c is not (' ' or '-' or '(' or ')' or '.'))
            {
                return false;
            }
        }

        string all = digits.ToString();
        if (!international && all.StartsWith("00", StringComparison.Ordinal))
        {
            international = true;
            all = all[2..];
        }

        string national;
        if (all.Length == 13 && all.StartsWith("880", StringComparison.Ordinal))
        {
            national = all[3..];
        }
        else if (!international && all.Length == 11 && all[0] == '0')
        {
            national = all[1..];
        }
        else
        {
            return false;
        }

        // 1 + operator digit 3-9 + eight subscriber digits.
        if (national.Length != 10 || national[0] != '1' || national[1] < '3')
        {
            return false;
        }

        normalized = "+880" + national;
        return true;
    }
}
