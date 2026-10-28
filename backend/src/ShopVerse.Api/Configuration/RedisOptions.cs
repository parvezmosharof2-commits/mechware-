using System.ComponentModel.DataAnnotations;

namespace ShopVerse.Api.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required(ErrorMessage = "Redis connection string is required.")]
    [MinLength(3, ErrorMessage = "Redis connection string must be valid.")]
    public string ConnectionString { get; set; } = string.Empty;

    public string InstanceName { get; set; } = "ShopVerse_";
    public int DefaultTtlMinutes { get; set; } = 60;
}
