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
    public static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("mechware-integration-tests-field-key"));
    public static readonly string KeyBase64 = Convert.ToBase64String(Key);

    [ModuleInitializer]
    public static void UseTestKey() => FieldEncryption.Configure(Key);
}
