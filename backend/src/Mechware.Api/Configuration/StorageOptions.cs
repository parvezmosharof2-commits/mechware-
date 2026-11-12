namespace Mechware.Api.Configuration;

/// <summary>
/// S3-compatible object storage (MinIO). Optional: no running feature stores files there today (product documents live on
/// the <c>Documents:LocalPath</c> volume; C3 "S3 vs volume" is an open owner decision). Setting <see cref="ServiceUrl"/>
/// turns it on: then both keys are required and <c>/health/ready</c> probes the store.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string ServiceUrl { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public string PublicBucket { get; set; } = "mechware-public";
    public string PrivateKycBucket { get; set; } = "mechware-private-kyc";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceUrl);

    public const string InvalidMessage =
        "Storage:ServiceUrl is set, so it must be an absolute http(s) URL and Storage:AccessKey and Storage:SecretKey are required. Leave Storage:ServiceUrl empty when no object store is used.";

    /// <summary>Not configured is valid; configured means a usable URL and both keys.</summary>
    public bool IsValid() =>
        !IsConfigured
        || (Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey));
}
