using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Mechware.Api.Configuration;
using Mechware.Infrastructure.Persistence;

namespace Mechware.UnitTests.Configuration;

/// <summary>H3a.2: one database connection for the whole host, and object storage that is optional until it is configured.</summary>
public class DeployConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void ConnectionString_PrefersDefaultConnection_OverTheOlderDatabaseSection()
    {
        var cs = MechwareDatabase.ConnectionString(Config(
            ("ConnectionStrings:DefaultConnection", "Host=db-a;Database=a;Username=u;Password=p"),
            ("Database:ConnectionString", "Host=db-b;Database=b;Username=u;Password=p")));

        new NpgsqlConnectionStringBuilder(cs).Host.Should().Be("db-a");
    }

    [Fact]
    public void ConnectionString_FallsBackToTheDatabaseSection_ThenToLocalDevelopment()
    {
        new NpgsqlConnectionStringBuilder(MechwareDatabase.ConnectionString(Config(("Database:ConnectionString", "Host=db-b;Database=b"))))
            .Host.Should().Be("db-b");
        new NpgsqlConnectionStringBuilder(MechwareDatabase.ConnectionString(Config(("ConnectionStrings:DefaultConnection", "  "))))
            .Database.Should().Be("mechware");
    }

    [Fact]
    public void ConnectionString_DisablesGssEncryption_KeepingEverythingElse()
    {
        var b = new NpgsqlConnectionStringBuilder(
            MechwareDatabase.WithDefaults("Host=pg;Port=6432;Database=shop;Username=app;Password=\"s3cr;et=x\";Maximum Pool Size=7"));

        b.GssEncryptionMode.Should().Be(GssEncryptionMode.Disable);
        (b.Host, b.Port, b.Database, b.Username, b.Password, b.MaxPoolSize).Should().Be(("pg", 6432, "shop", "app", "s3cr;et=x", 7));
    }

    [Theory]
    [InlineData("Host=pg;GSS Encryption Mode=Prefer", GssEncryptionMode.Prefer)]
    [InlineData("Host=pg;gss encryption mode=Require", GssEncryptionMode.Require)]
    public void ConnectionString_KeepsAnExplicitGssChoice(string connectionString, GssEncryptionMode expected)
    {
        MechwareDatabase.WithDefaults(connectionString).Should().Be(connectionString);
        new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("", "", "", true)]                                   // not configured: nothing required
    [InlineData("", "key", "secret", true)]                          // keys alone do not turn it on
    [InlineData("http://minio:9000", "key", "secret", true)]
    [InlineData("https://s3.example.com", "key", "secret", true)]
    [InlineData("http://minio:9000", "", "secret", false)]           // configured, a key missing
    [InlineData("http://minio:9000", "key", " ", false)]
    [InlineData("minio:9000", "key", "secret", false)]               // not an absolute http(s) URL
    [InlineData("ftp://minio", "key", "secret", false)]
    public void Storage_IsOptional_ButMustBeCompleteOnceItsUrlIsSet(string url, string accessKey, string secretKey, bool valid)
    {
        var services = new ServiceCollection();
        services.AddOptions<StorageOptions>()
            .Bind(Config(("Storage:ServiceUrl", url), ("Storage:AccessKey", accessKey), ("Storage:SecretKey", secretKey)).GetSection(StorageOptions.SectionName))
            .Validate(o => o.IsValid(), StorageOptions.InvalidMessage);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<StorageOptions>>().Value;

        if (valid) act.Should().NotThrow();
        else act.Should().Throw<OptionsValidationException>().WithMessage("*Storage:ServiceUrl is set*");
    }
}
