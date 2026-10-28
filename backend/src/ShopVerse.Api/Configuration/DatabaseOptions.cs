using System.ComponentModel.DataAnnotations;

namespace ShopVerse.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required(ErrorMessage = "Database connection string is required.")]
    [MinLength(10, ErrorMessage = "Database connection string must be valid.")]
    public string ConnectionString { get; set; } = string.Empty;

    public int MaxRetryCount { get; set; } = 3;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public bool EnableSensitiveDataLogging { get; set; } = false;
}
