using System.Text.Json;
using System.Text.Json.Nodes;
using Mechware.Infrastructure.Security;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// What the audit viewer may show of a row's before/after values (G5.2b). Redaction goes by key name, at any depth, whatever the
/// action: a key that names a secret, a code, a hash, a token, an encrypted business number (BIN, TIN, trade licence, the BIN
/// fingerprint) or a bank/card number reads <see cref="Hidden"/>, and so does any text holding a field-encrypted value. Nothing is
/// ever decrypted. Values that are not valid JSON are hidden whole (fail closed).
/// </summary>
public static class AuditRedaction
{
    public const string Hidden = "[hidden]";

    /// <summary>A key containing one of these (lower case, without '_' or '-') is hidden.</summary>
    private static readonly string[] Fragments =
    [
        "password", "secret", "token", "totp", "otp", "hash", "salt", "cipher", "recovery", "securitystamp", "cvv", "cardnumber", "cardno",
        "iban", "accountnumber", "accountno", "bankaccount", "routingnumber", "swift"
    ];

    /// <summary>Keys hidden as a whole word: the field-encrypted business numbers (H4), their fingerprint, payment references.</summary>
    private static readonly HashSet<string> Exact =
        ["bin", "tin", "binindex", "tradelicence", "tradelicenceno", "nid", "reference", "payoutreference", "chequenumber", "chequeno"];

    /// <summary>"Code" and "key" mean a secret unless the name is one of these reviewed, harmless ones.</summary>
    private static readonly HashSet<string> SafeCodes =
        ["postalcode", "postalcodeprefix", "countrycode", "currencycode", "reasoncode", "errorcode", "hscode", "statuscode"];

    private static readonly HashSet<string> SafeKeys = ["storagekey", "pdfenkey", "pdfbnkey", "templatekey"];

    private const string CipherMark = FieldProtector.Prefix + "v";

    public static bool IsHiddenKey(string key)
    {
        var n = key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return Exact.Contains(n) || Fragments.Any(f => n.Contains(f, StringComparison.Ordinal))
            || (n.Contains("code", StringComparison.Ordinal) && !SafeCodes.Contains(n))
            || (n.EndsWith("key", StringComparison.Ordinal) && !SafeKeys.Contains(n));
    }

    /// <summary>The stored JSON (old or new values) as a redacted JSON node; null stays null.</summary>
    public static JsonNode? Redact(string? json)
    {
        if (json is null) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(Hidden); }
        return Scrub(root);
    }

    /// <summary>Returns the node to keep in place of <paramref name="node"/> (objects and arrays are scrubbed in place).</summary>
    private static JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (IsHiddenKey(key)) o[key] = Hidden;
                    else if (o[key] is JsonValue) o[key] = Scrub(o[key]);
                    else Scrub(o[key]);
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue) a[i] = Scrub(a[i]);
                    else Scrub(a[i]);
                }
                return a;
            case JsonValue v when v.TryGetValue(out string? text):
                return ScrubText(text);
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Text holding ciphertext is hidden; text that is itself a JSON object or array is scrubbed like the values.</summary>
    private static JsonNode ScrubText(string text)
    {
        if (text.Contains(CipherMark, StringComparison.Ordinal)) return Hidden;
        var t = text.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonNode inner and (JsonObject or JsonArray)) return Scrub(inner)!.ToJsonString();
            }
            catch (JsonException) { }
        }

        return text;
    }
}
