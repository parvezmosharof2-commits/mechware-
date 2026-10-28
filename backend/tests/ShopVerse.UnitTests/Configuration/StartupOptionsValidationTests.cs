using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShopVerse.Api.Configuration;
using Xunit;

namespace ShopVerse.UnitTests.Configuration;

public class StartupOptionsValidationTests
{
    [Fact]
    public void MissingDatabaseConfig_ShouldFailStartupValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "", // Empty
                ["Redis:ConnectionString"] = "localhost:6379",
                ["Jwt:Secret"] = "12345678901234567890123456789012"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Database connection string is required.*");
    }

    [Fact]
    public void MissingRedisConfig_ShouldFailStartupValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=test;",
                ["Redis:ConnectionString"] = "", // Empty
                ["Jwt:Secret"] = "12345678901234567890123456789012"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<RedisOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Redis connection string is required.*");
    }

    [Fact]
    public void ShortJwtSecret_ShouldFailStartupValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "short_key", // < 32 chars
                ["Jwt:Issuer"] = "https://test.com",
                ["Jwt:Audience"] = "https://test.com"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<JwtOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*JWT Secret must be at least 32 characters*");
    }

    [Fact]
    public void ValidConfig_ShouldSucceed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=test;",
                ["Redis:ConnectionString"] = "localhost:6379",
                ["Jwt:Secret"] = "12345678901234567890123456789012",
                ["Jwt:Issuer"] = "https://api.mechware.com.bd",
                ["Jwt:Audience"] = "https://mechware.com.bd"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString.Should().Be("Host=localhost;Database=test;");
        provider.GetRequiredService<IOptions<RedisOptions>>().Value.ConnectionString.Should().Be("localhost:6379");
        provider.GetRequiredService<IOptions<JwtOptions>>().Value.Secret.Should().Be("12345678901234567890123456789012");
    }
}
