using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Mechware.Domain.Companies;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;
using Xunit;

namespace Mechware.UnitTests.Audit;

/// <summary>
/// A2.x (threat model v1, H4): the change interceptor never writes a field-encrypted value in clear, at the top level or inside a
/// JSON snapshot. The whitelist is proven twice: every property of the EF model is probed through its value converter (a column
/// whose stored form holds field ciphertext is an encrypted property), and every place in backend/src that encrypts is counted.
/// A new encrypted property or a new encryption site fails here until it is reviewed, listed below and given an integration case
/// in <c>Mechware.IntegrationTests/Security/AuditEncryptedFieldsTests.cs</c>.
/// </summary>
public sealed partial class AuditEncryptedFieldsTests
{
    /// <summary>Properties whose column the EF value converter encrypts (whole value, or members of a JSON snapshot).</summary>
    public static readonly string[] ConverterEncrypted =
        ["Company.Bin", "Company.Tin", "Company.TradeLicenceNo", "Invoice.Buyer", "QuoteVersion.CompanySnapshot"];

    /// <summary>Properties the entity already holds as ciphertext (encrypted by <c>TwoFactorSecrets</c> before they reach EF).</summary>
    public static readonly string[] EntityEncrypted = ["User.TwoFactorSecret", "User.TwoFactorRecoveryCodesJson"];

    /// <summary>Every call that encrypts with the field key, per file (reviewed for A2.x).</summary>
    private static readonly Dictionary<string, int> ReviewedEncryptionSites = new(StringComparer.Ordinal)
    {
        ["CompanyConfigurations.cs"] = 1, // bin, tin, trade_licence_no (one converter, three columns)
        ["InvoicingConfigurations.cs"] = 1, // buyer_snapshot.bin
        ["QuoteConfigurations.cs"] = 1, // company_snapshot.bin
        ["TwoFactorSecrets.cs"] = 1, // users.two_factor_secret, two_factor_recovery_codes_json
    };

    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static IModel Model()
    {
        using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql("Host=unused").Options);
        return db.Model;
    }

    private static IProperty Property<T>(string name) => Model().FindEntityType(typeof(T))!.FindProperty(name)!;

    [GeneratedRegex(@"\bProtect\(")]
    private static partial Regex ProtectCall();

    // ---- whitelist ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryEncryptionSiteInTheSource_IsReviewed()
    {
        string src = Path.Combine(Root(), "src");
        var sep = Path.DirectorySeparatorChar;
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}Migrations{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                        && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && Path.GetFileName(f) != "FieldEncryption.cs")
            .Select(f => (File: Path.GetFileName(f), Count: ProtectCall().Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.File, x => x.Count, StringComparer.Ordinal);

        found.Should().BeEquivalentTo(ReviewedEncryptionSites,
            "a new place that encrypts with the field key needs a review: the audit interceptor must not copy the value in clear " +
            "(add the property to the lists above and a case to the integration test)");
    }

    [Fact]
    public void ProbingTheModel_FindsExactlyTheReviewedEncryptedProperties_AndTheAuditValueHoldsNoneOfTheirPlaintext()
    {
        var encrypted = new List<string>();
        var unprobed = new List<string>();
        foreach (var entity in Model().GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var converter = property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;
                if (converter is null || converter.ProviderClrType != typeof(string) || converter.ModelClrType.IsEnum) continue;
                string name = $"{entity.ClrType.Name}.{property.Name}";
                var probe = new Probe();
                object? value;
                string stored;
                try
                {
                    value = probe.Build(converter.ModelClrType, 0);
                    stored = (string)converter.ConvertToProvider(value)!;
                }
                catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException
                                               or NullReferenceException or NotSupportedException or InvalidCastException)
                {
                    unprobed.Add($"{name}: {ex.GetBaseException().Message}");
                    continue;
                }

                var secrets = Ciphertexts(stored).Select(c => FieldEncryption.Current.Unprotect(c)).ToList();
                if (secrets.Count == 0) continue;
                encrypted.Add(name);

                string audit = JsonSerializer.Serialize(AuditLogSaveChangesInterceptor.AuditValue(property, value));
                foreach (string plain in secrets) audit.Should().NotContain(plain, $"{name} is encrypted at rest");
                audit.Should().NotContain(FieldProtector.Prefix, $"{name}: no ciphertext copy either (a key rotation could not re-encrypt it)");
                // The members stored in clear keep their values (the snapshot shape survives; only ciphertext is masked).
                foreach (string clear in probe.Issued.Except(secrets).Where(stored.Contains)) audit.Should().Contain(clear, name);
            }
        }

        unprobed.Should().BeEmpty("every converted column must be probed; teach the probe the new shape");
        encrypted.Should().BeEquivalentTo(ConverterEncrypted,
            "a new encrypted property needs a review and an integration case (Mechware.IntegrationTests/Security/AuditEncryptedFieldsTests.cs)");
    }

    [Fact]
    public void EveryReviewedProperty_HasAnIntegrationCase()
    {
        string source = File.ReadAllText(Path.Combine(Root(), "tests", "Mechware.IntegrationTests", "Security", "AuditEncryptedFieldsTests.cs"));
        foreach (string name in ConverterEncrypted.Concat(EntityEncrypted))
            source.Should().Contain($"[InlineData(\"{name}\")]", $"{name} is saved through the real interceptor and the raw audit_logs searched");
    }

    // ---- behaviour ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANestedSnapshot_IsWrittenInItsStoredShape_WithTheBinReplacedByTheMarker()
    {
        var snapshot = new QuoteCompanySnapshot("Acme Ltd", "000123456-0101", "12 Tejgaon I/A, Dhaka 1208");

        var written = AuditLogSaveChangesInterceptor.AuditValue(Property<QuoteVersion>(nameof(QuoteVersion.CompanySnapshot)), snapshot);

        JsonSerializer.Serialize(written).Should().Be(
            """{"name":"Acme Ltd","bin":"[encrypted]","billingAddress":"12 Tejgaon I/A, Dhaka 1208"}""");
    }

    [Fact]
    public void ASnapshotWithoutABin_AndAConvertedValueWithoutCiphertext_AreWrittenAsBefore()
    {
        var snapshot = new QuoteCompanySnapshot("Acme Ltd", null, null);
        AuditLogSaveChangesInterceptor.AuditValue(Property<QuoteVersion>(nameof(QuoteVersion.CompanySnapshot)), snapshot).Should().BeSameAs(snapshot);
        var flags = new QuoteFlags([], [], true);
        AuditLogSaveChangesInterceptor.AuditValue(Property<QuoteVersion>(nameof(QuoteVersion.Flags)), flags).Should().BeSameAs(flags);
        AuditLogSaveChangesInterceptor.AuditValue(Property<Company>(nameof(Company.LegalName)), "Acme Ltd").Should().Be("Acme Ltd");
    }

    [Fact]
    public void AnEncryptedColumn_OrCiphertextHeldByTheEntity_IsNeverCopied()
    {
        AuditLogSaveChangesInterceptor.AuditValue(Property<Company>(nameof(Company.Bin)), "1234567890123").Should().Be("[REDACTED]");
        // Any value that already is field ciphertext is masked, whatever the property is called.
        string cipher = FieldEncryption.Current.Protect("JBSWY3DPEHPK3PXP");
        AuditLogSaveChangesInterceptor.AuditValue(Property<Company>(nameof(Company.LegalName)), cipher)
            .Should().Be(AuditLogSaveChangesInterceptor.EncryptedMarker);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> Ciphertexts(string stored)
    {
        if (FieldProtector.IsProtected(stored)) return [stored];
        if (!stored.Contains(FieldProtector.Prefix, StringComparison.Ordinal)) return [];
        return Strings(JsonNode.Parse(stored)).Where(FieldProtector.IsProtected);
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue(out string? s) => [s],
        _ => [],
    };

    /// <summary>Builds a value of any snapshot shape with a distinct text in every string member (one item in every list).</summary>
    private sealed class Probe
    {
        public List<string> Issued { get; } = [];

        public object? Build(Type type, int depth)
        {
            if (depth > 5) return null;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string))
            {
                string text = $"probe-{Issued.Count:D3}-7c41";
                Issued.Add(text);
                return text;
            }

            if (underlying.IsEnum) return Enum.GetValues(underlying).GetValue(0);
            if (underlying == typeof(Guid)) return Guid.NewGuid();
            if (underlying == typeof(DateTime)) return new DateTime(2026, 11, 26, 4, 0, 0, DateTimeKind.Utc);
            if (underlying == typeof(DateOnly)) return new DateOnly(2026, 11, 26);
            if (underlying == typeof(decimal)) return 1.5m;
            if (underlying.IsPrimitive) return Convert.ChangeType(1, underlying, System.Globalization.CultureInfo.InvariantCulture);
            if (underlying.IsValueType) return Activator.CreateInstance(underlying);
            if (underlying.IsArray)
            {
                var array = Array.CreateInstance(underlying.GetElementType()!, 1);
                array.SetValue(Build(underlying.GetElementType()!, depth + 1), 0);
                return array;
            }

            if (underlying.IsGenericType && underlying.GetGenericArguments().Length == 2 && typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                var args = underlying.GetGenericArguments();
                var map = (IDictionary)Activator.CreateInstance(underlying.IsInterface ? typeof(Dictionary<,>).MakeGenericType(args) : underlying)!;
                map.Add(Build(args[0], depth + 1)!, Build(args[1], depth + 1));
                return map;
            }

            if (underlying.IsGenericType && typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                var item = underlying.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(underlying.IsInterface ? typeof(List<>).MakeGenericType(item) : underlying)!;
                list.Add(Build(item, depth + 1));
                return list;
            }

            var ctor = underlying.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            return ctor.Invoke([.. ctor.GetParameters().Select(p => Build(p.ParameterType, depth + 1))]);
        }
    }
}
