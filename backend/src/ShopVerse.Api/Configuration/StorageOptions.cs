using System.ComponentModel.DataAnnotations;

namespace ShopVerse.Api.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required(ErrorMessage = "Storage ServiceUrl / Endpoint is required.")]
    public string ServiceUrl { get; set; } = "http://localhost:9000";

    [Required(ErrorMessage = "Storage AccessKey is required.")]
    public string AccessKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "Storage SecretKey is required.")]
    public string SecretKey { get; set; } = string.Empty;

    public string PublicBucket { get; set; } = "mechware-public";
    public string PrivateKycBucket { get; set; } = "mechware-private-kyc";
}
