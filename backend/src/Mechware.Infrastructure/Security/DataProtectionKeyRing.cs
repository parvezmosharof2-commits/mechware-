using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Security;

/// <summary>
/// ASP.NET Core Data Protection keys shared by every API instance and kept across restarts: they live in PostgreSQL
/// (table <c>data_protection_keys</c>), not in the container's file system, which is read-only and thrown away.
/// Today nothing in the API protects a payload with Data Protection (sign-in uses our own JWTs, business identifiers use
/// <see cref="FieldEncryption"/>, TOTP secrets are columns of the users table), so losing the ring costs nothing yet;
/// persisting it keeps any future cookie, antiforgery token or protected link valid across instances and deploys.
/// </summary>
public static class DataProtectionKeyRing
{
    /// <summary>Fixed so that every instance, whatever its content root, reads the same keys.</summary>
    public const string ApplicationName = "Mechware";

    /// <summary>
    /// Registers Data Protection over the database. When <paramref name="certificatePath"/> names a PKCS#12 file with a
    /// private key, every key is encrypted with that certificate before it is stored; returns whether that is the case.
    /// Without a certificate the keys are stored as plain XML, readable by anyone who can read the database.
    /// </summary>
    public static bool AddPersistedDataProtection(this IServiceCollection services, string? certificatePath, string? certificatePassword)
    {
        IDataProtectionBuilder builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<MechwareDbContext>();

        if (string.IsNullOrWhiteSpace(certificatePath)) return false;

        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("DataProtection:CertificatePath must point at a PKCS#12 file that includes the private key.");
        }

        // UnprotectKeysWithAnyCertificate: on Linux the decryptor cannot find the certificate in a machine store.
        builder.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
        return true;
    }
}
