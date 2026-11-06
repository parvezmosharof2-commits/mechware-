using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ShopVerse.Infrastructure.Security;

namespace ShopVerse.IntegrationTests.Support;

/// <summary>
/// One field-encryption key for the whole test run. The key is process-wide, and test classes run in parallel: a host that
/// switched to its own key would leave other tests unable to read what they wrote. Production-like hosts are given this
/// same key, so configuring it changes nothing.
/// </summary>
public static class TestEncryption
{
    /// <summary>Production-like hosts refuse to start without a documents directory and a scanner host; tests point at these.</summary>
    public static readonly string DocumentsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mechware-test-documents");

    public static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("mechware-integration-tests-field-key"));
    public static readonly string KeyBase64 = Convert.ToBase64String(Key);

    /// <summary>A retired key still configured for reading, as during a key rotation.</summary>
    public static readonly byte[] PreviousKey = SHA256.HashData(Encoding.UTF8.GetBytes("mechware-integration-tests-previous-field-key"));
    public static readonly string PreviousKeyBase64 = Convert.ToBase64String(PreviousKey);

    [ModuleInitializer]
    public static void UseTestKey() => FieldEncryption.Configure(Key, [PreviousKey]);
}
