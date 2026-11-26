using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Common.Primitives;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;
using Xunit;

namespace Mechware.UnitTests.Audit;

/// <summary>
/// G5.2b redaction of audit values. The whitelist tests read the source: every audit action written anywhere must be in the reviewed
/// list below, and every key those actions or the change interceptor can write (anonymous objects at the audit call sites, every column
/// of every audited table and the members of their JSON snapshots) that looks sensitive must either be hidden or be reviewed as shown.
/// A new action or a new sensitive-looking key fails here until someone decides.
/// </summary>
public sealed partial class AuditRedactionTests
{
    /// <summary>Every explicit audit action in backend/src (reviewed for G5.2b). The change interceptor adds "&lt;table&gt;.created|updated|deleted".</summary>
    private static readonly HashSet<string> KnownActions =
    [
        "address.created", "address.deleted", "address.updated",
        "audit.searched", "audit.viewed",
        "catalog.import.committed", "catalog.product.edited", "catalog.product.{action}", "catalog.variant.price_changed", "catalog.variant.updated",
        "credit.adjustment_approved", "credit.adjustment_rejected", "credit.adjustment_requested", "credit.aging_exported", "credit.charge_reduced",
        "credit.hold_ended", "credit.hold_overridden", "credit.hold_started", "credit.on_account_applied", "credit.payment_bounced",
        "credit.payment_recorded", "credit.payment_rejected", "credit.payment_verified", "credit.reconciliation_mismatch", "credit.statement_exported",
        "inventory.reconciled", "inventory.restocked",
        "invoice.issue_refused", "invoice.issued", "invoice.reconciliation_finding",
        "margin-floor.changed", "margin-floor.deleted",
        "order.cancelled_by_staff", "order.delivered", "order.packed", "order.processing_started", "order.shipped",
        "pricelist.approved", "pricelist.deleted", "pricelist.rejected", "pricelist.revised", "pricelist.submitted", "pricelist.withdrawn",
        "quote.accepted", "quote.approval_rejected", "quote.approved", "quote.closed", "quote.created", "quote.draft_changed", "quote.pdf_integrity_failed", "quote.pdf_rendered",
        "quote.recalled", "quote.rejected", "quote.revised", "quote.sent", "quote.submitted_for_approval", "quote.terms_changed", "quote.withdrawn",
        "return.approved", "return.inspected", "return.received", "return.rejected", "return.requested", "return.withdrawn",
        "rfq.assigned", "rfq.closed", "rfq.submitted", "rfq.withdrawn",
        "security.refresh_token_family_revoked_on_reuse", "security.sessions_revoked_2fa_required", "security.sessions_revoked_step_up_failures",
        "shipping.zone.created", "shipping.zone.deleted", "shipping.zone.updated",
        "user.2fa_enabled", "user.2fa_enrollment_failed", "user.2fa_enrollment_required", "user.2fa_failed", "user.2fa_setup_code_failed",
        "user.2fa_setup_code_issued", "user.2fa_setup_code_redeemed", "user.2fa_setup_initiated", "user.bootstrap_admin_provisioned",
        "user.gdpr_anonymized", "user.gdpr_export", "user.google_oauth_login", "user.google_oauth_mismatch", "user.google_oauth_refused",
        "user.login_failed", "user.login_locked_out", "user.login_refused", "user.login_succeeded", "user.otp_dispatched", "user.otp_session_refused",
        "user.otp_verified", "user.password_reset_completed", "user.password_reset_requested", "user.registered",
        "user.registration_for_existing_account", "user.session_logged_out", "user.sessions_revoked", "user.step_up_failed", "user.step_up_passed"
    ];

    /// <summary>Dotted literals in those files that are not actions (a JSON schema id, a cache key).</summary>
    private static readonly HashSet<string> NotActions = ["mechware.rfq.v1", "rates.bands"];

    /// <summary>Must always be hidden: the interceptor's secrets, the encrypted business numbers and their fingerprint, bank references.</summary>
    private static readonly string[] MustHide =
    [
        "PasswordHash", "TwoFactorSecret", "TwoFactorRecoveryCodesJson", "TokenHash", "ReplacedByTokenHash", "CodeHash", "RecoveryCodes", "Bin",
        "Tin", "TradeLicenceNo", "BinIndex", "Reference", "PayoutReference", "ApiKey", "IdempotencyKey", "code", "otp", "totpCode", "stepUpCode",
        "secret", "token", "accessToken", "refreshToken", "ContentHash", "RequestHash", "card_number", "bankAccountNumber", "iban"
    ];

    /// <summary>Sensitive-looking keys reviewed as safe to show to audit.view staff (G5.2b). Personal data is shown (item 1 masks none).</summary>
    private static readonly HashSet<string> ReviewedShown =
    [
        // Who and from where (the list shows them too; owner decision (a) on staff privacy is open).
        "ActorIp", "BreachIp", "clientIp", "CreatedByIp", "AccountManagerId",
        // Contact data of buyers and staff: personal, not secret; audit.view (Finance, Admin) needs it to investigate.
        "ContactPhone", "email", "Email", "EmailRedacted", "EmailVerifiedAtUtc", "Phone", "PhoneVerifiedAtUtc", "recipient", "RecipientName",
        "GoogleSubjectId", "subjectId", "SubjectId", "SubjectType",
        // Money on account, cheque dates and the drawer's bank name (a name, not an account number).
        "CreditMovedOnAccountMinor", "CreditOnAccountMinor", "MovedOnAccountMinor", "OnAccountAfterMinor", "OnAccountDeltaMinor", "OnAccountMinor",
        "OnAccountReversedMinor", "ChequeDate", "DrawerBank",
        // Postal codes; storage keys and SHA-256 of rendered PDFs (where a document is and its public fingerprint, not secrets).
        "PostalCode", "PostalCodePrefix", "PdfBnKey", "PdfEnKey", "PdfBnSha256", "PdfEnSha256", "Sha256", "StorageKey",
        // Links to other rows (an OTP's purpose row, a price's source), not payment references.
        "ReferenceId", "ReferenceSource", "ReferenceType", "ReferenceUnitPriceMinor"
    ];

    /// <summary>Words (camelCase or snake_case parts) that make a key look sensitive.</summary>
    private static readonly HashSet<string> SensitiveWords =
    [
        "password", "secret", "token", "otp", "totp", "hash", "salt", "key", "code", "codes", "bin", "tin", "licence", "license", "nid", "account",
        "card", "iban", "cvv", "reference", "cheque", "bank", "stamp", "signature", "cipher", "recovery", "sha256", "pin", "email", "phone",
        "recipient", "subject", "ip"
    ];

    private static bool LooksSensitive(string key) =>
        Regex.Split(key, @"(?<=[a-z0-9])(?=[A-Z])|_|-").Any(w => SensitiveWords.Contains(w.ToLowerInvariant()));

    private static string Src([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src"));

    private static List<string> AuditWritingFiles() =>
    [
        .. Directory.EnumerateFiles(Src(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => AuditCall().IsMatch(File.ReadAllText(f)))
    ];

    [GeneratedRegex(@"AuditLogs\.Add|new AuditLog\(|RecordAudit\(|\bAudit\(""|\bAudit\(\w+|SearchedAction|ViewedAction")]
    private static partial Regex AuditCall();

    [GeneratedRegex(@"\$?""([a-z][a-z0-9_-]*(?:\.[a-z0-9_{}-]+)+)""")]
    private static partial Regex ActionLiteral();

    [GeneratedRegex(@"new\s*\{([^{}]*)\}")]
    private static partial Regex AnonymousObject();

    [GeneratedRegex(@"\([^()]*\)|\[[^\[\]]*\]")]
    private static partial Regex Bracketed();

    [Fact]
    public void Every_audit_action_in_the_source_is_reviewed_and_the_list_has_no_stale_entry()
    {
        var found = AuditWritingFiles().SelectMany(f => ActionLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(a => !NotActions.Contains(a)).ToHashSet();
        found.Except(KnownActions).Should().BeEmpty("a new audit action must be reviewed for redaction (add it to KnownActions)");
        KnownActions.Except(found).Should().BeEmpty("an action in the reviewed list is no longer written");
    }

    private static IEnumerable<string> AnonymousMembers(string source)
    {
        foreach (Match m in AnonymousObject().Matches(source))
        {
            var body = m.Groups[1].Value;
            while (Bracketed().IsMatch(body)) body = Bracketed().Replace(body, "");
            foreach (var part in body.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                var name = p.Contains('=') ? p[..p.IndexOf('=')].Trim() : p.Split('.', '?', '!').Last(s => s.Trim().Length > 0).Trim();
                if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) yield return name;
            }
        }
    }

    private static IEnumerable<string> ModelKeys()
    {
        using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql("Host=unused").Options);
        foreach (var e in db.Model.GetEntityTypes().Where(e => !Attribute.IsDefined(e.ClrType, typeof(NotAuditedAttribute))))
        {
            foreach (var p in e.GetProperties())
            {
                yield return p.Name;
                foreach (var member in Members(p.ClrType, 0)) yield return member;
            }
        }
    }

    /// <summary>The members a value-converted property (a JSON snapshot) serialises with, two levels deep.</summary>
    private static IEnumerable<string> Members(Type t, int depth)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsArray) t = t.GetElementType()!;
        else if (t.IsGenericType && t.GetGenericArguments() is [var element]) t = element;
        if (depth > 2 || t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) || t == typeof(DateTime)
            || t == typeof(DateOnly) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t.Namespace?.StartsWith("Mechware", StringComparison.Ordinal) != true)
            yield break;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return p.Name;
            foreach (var m in Members(p.PropertyType, depth + 1)) yield return m;
        }
    }

    [Fact]
    public void Every_sensitive_looking_key_an_audit_row_can_hold_is_hidden_or_reviewed()
    {
        var keys = AuditWritingFiles().SelectMany(f => AnonymousMembers(File.ReadAllText(f))).Concat(ModelKeys()).ToHashSet(StringComparer.Ordinal);
        keys.Should().Contain(["PasswordHash", "Bin", "TwoFactorSecret", "Reference"], "the scan must see the model and the call sites");

        var unreviewed = keys.Where(LooksSensitive).Where(k => !AuditRedaction.IsHiddenKey(k) && !ReviewedShown.Contains(k)).Order().ToList();
        unreviewed.Should().BeEmpty("each sensitive-looking key must be hidden by AuditRedaction or reviewed into ReviewedShown: "
            + string.Join(", ", unreviewed));
    }

    [Fact]
    public void The_must_hide_keys_are_hidden_in_any_spelling()
    {
        foreach (var key in MustHide)
        {
            AuditRedaction.IsHiddenKey(key).Should().BeTrue(key);
            AuditRedaction.IsHiddenKey(key.ToUpperInvariant()).Should().BeTrue(key);
        }
    }

    [Fact]
    public void Redact_hides_secrets_at_any_depth_and_ciphertext_under_any_key_and_keeps_the_rest()
    {
        var cipher = new FieldProtector(new byte[32]).Protect("1234567890123");
        var json = $$$"""
            {"Email":"a@b.test","PasswordHash":"AQAAAAIAAYagAAAAE","Status":"Active","Amount":12345,"Ok":true,"Gone":null,
             "CompanySnapshot":{"Name":"Acme","Bin":"1234567890123","BillingAddress":{"Line1":"Plot 1","PostalCode":"1710"}},
             "Lines":[{"Sku":"X-1","Note":"{{{cipher}}}"},{"Sku":"X-2","Reference":"TT-998877"}],
             "Note":"{{{cipher}}}","Wrapped":"prefix {{{cipher}}}","Nested":"{\"TwoFactorSecret\":\"JBSWY3DP\",\"Name\":\"n\"}",
             "TwoFactorRecoveryCodesJson":["a","b"],"Items":["{{{cipher}}}","plain"]}
            """;

        var r = AuditRedaction.Redact(json)!.AsObject();
        var text = r.ToJsonString();
        text.Should().NotContain("1234567890123").And.NotContain(cipher).And.NotContain("AQAAAAIAAYagAAAAE").And.NotContain("TT-998877")
            .And.NotContain("JBSWY3DP").And.NotContain("enc:");
        ((string?)r["Email"]).Should().Be("a@b.test");
        ((string?)r["PasswordHash"]).Should().Be(AuditRedaction.Hidden);
        ((int?)r["Amount"]).Should().Be(12345);
        ((bool?)r["Ok"]).Should().BeTrue();
        r["Gone"].Should().BeNull();
        ((string?)r["CompanySnapshot"]!["Name"]).Should().Be("Acme");
        ((string?)r["CompanySnapshot"]!["Bin"]).Should().Be(AuditRedaction.Hidden);
        ((string?)r["CompanySnapshot"]!["BillingAddress"]!["PostalCode"]).Should().Be("1710");
        ((string?)r["Lines"]![0]!["Note"]).Should().Be(AuditRedaction.Hidden);
        ((string?)r["Lines"]![1]!["Reference"]).Should().Be(AuditRedaction.Hidden);
        ((string?)r["Lines"]![1]!["Sku"]).Should().Be("X-2");
        ((string?)r["Note"]).Should().Be(AuditRedaction.Hidden);
        ((string?)r["Wrapped"]).Should().Be(AuditRedaction.Hidden);
        JsonNode.Parse((string)r["Nested"]!)!["Name"]!.GetValue<string>().Should().Be("n");
        ((string?)r["TwoFactorRecoveryCodesJson"]).Should().Be(AuditRedaction.Hidden);
        ((string?)r["Items"]![0]).Should().Be(AuditRedaction.Hidden);
        ((string?)r["Items"]![1]).Should().Be("plain");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not json", "\"[hidden]\"")]
    [InlineData("\"plain\"", "\"plain\"")]
    [InlineData("[{\"otp\":\"123456\"}]", "[{\"otp\":\"[hidden]\"}]")]
    [InlineData("{\"PostalCode\":\"1710\",\"ReasonCode\":\"x\",\"SetupCode\":\"9\"}", "{\"PostalCode\":\"1710\",\"ReasonCode\":\"x\",\"SetupCode\":\"[hidden]\"}")]
    public void Redact_handles_every_shape(string? stored, string? expected) =>
        AuditRedaction.Redact(stored)?.ToJsonString().Should().Be(expected);
}
