using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Security;

/// <summary>
/// H3a.2: Data Protection keys live in PostgreSQL, so a second API instance (or the same one after a restart) reads what
/// the first protected; with a certificate they are encrypted at rest; and they never reach the audit log.
/// </summary>
public sealed class DataProtectionKeyRingTests : IClassFixture<PostgresDatabaseFixture>
{
    private readonly PostgresDatabaseFixture _db;

    public DataProtectionKeyRingTests(PostgresDatabaseFixture db) => _db = db;

    /// <summary>One "API instance": its own container, the same database, the API's audit interceptor on the DbContext.</summary>
    private ServiceProvider Instance(string? certificatePath = null, string? certificatePassword = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserService, NoUser>();
        services.AddScoped<AuditLogSaveChangesInterceptor>();
        services.AddDbContext<MechwareDbContext>((sp, o) => o.UseNpgsql(_db.ConnectionString)
            .AddInterceptors(sp.GetRequiredService<AuditLogSaveChangesInterceptor>()));
        services.AddPersistedDataProtection(certificatePath, certificatePassword);
        return services.BuildServiceProvider();
    }

    private async Task ClearKeysAsync()
    {
        await using var db = _db.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM data_protection_keys");
    }

    private static IDataProtector Protector(IServiceProvider sp) =>
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Mechware.Tests.KeyRing");

    [PostgresFact]
    public async Task KeysAreStoredInTheDatabase_AndASecondInstanceUnprotectsWhatTheFirstProtected()
    {
        await ClearKeysAsync();

        string token;
        await using (var first = Instance())
        {
            token = Protector(first).Protect("order-42");
        }

        await using (var second = Instance())
        {
            Protector(second).Unprotect(token).Should().Be("order-42");
        }

        await using var db = _db.CreateContext();
        var keys = await db.DataProtectionKeys.AsNoTracking().ToListAsync();
        keys.Should().ContainSingle().Which.Xml.Should().Contain("<key ");
        (await db.AuditLogs.AnyAsync(a => a.EntityName == "data_protection_keys"))
            .Should().BeFalse("key material must never be copied into the audit log");
    }

    [PostgresFact]
    public async Task WithACertificate_KeysAreEncryptedAtRest_AndStillShared()
    {
        await ClearKeysAsync();
        string pfx = Path.Combine(Path.GetTempPath(), $"mw-dp-{Guid.NewGuid():N}.pfx");
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=mechware-data-protection-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            await File.WriteAllBytesAsync(pfx, cert.Export(X509ContentType.Pfx, "test-pfx-password"));
        }

        try
        {
            string token;
            await using (var first = Instance(pfx, "test-pfx-password"))
            {
                token = Protector(first).Protect("quote-7");
            }

            await using (var second = Instance(pfx, "test-pfx-password"))
            {
                Protector(second).Unprotect(token).Should().Be("quote-7");
            }

            await using var db = _db.CreateContext();
            var xml = (await db.DataProtectionKeys.AsNoTracking().SingleAsync()).Xml!;
            xml.Should().Contain("EncryptedData").And.NotContain("<masterKey", "the master key must not be stored in clear");
        }
        finally
        {
            File.Delete(pfx);
        }
    }

    [Fact]
    public void ACertificateWithoutItsPrivateKey_IsRefused()
    {
        string cer = Path.Combine(Path.GetTempPath(), $"mw-dp-{Guid.NewGuid():N}.pfx");
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=public-only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            using var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
            File.WriteAllBytes(cer, publicOnly.Export(X509ContentType.Pfx, "pw"));
        }

        try
        {
            FluentActions.Invoking(() => new ServiceCollection().AddPersistedDataProtection(cer, "pw"))
                .Should().Throw<InvalidOperationException>().WithMessage("*private key*");
        }
        finally
        {
            File.Delete(cer);
        }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => null;
        public string? UserAgent => null;
        public string? CorrelationId => null;
        public bool HasPermission(string permission) => false;
    }
}
