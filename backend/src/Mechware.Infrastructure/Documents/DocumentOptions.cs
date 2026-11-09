namespace Mechware.Infrastructure.Documents;

/// <summary>Section <c>Documents</c>: size limits per kind and where files are kept.</summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    public long MaxDatasheetBytes { get; set; } = 25L * 1024 * 1024;
    public long MaxCadBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxCertificateBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>A directory on a persistent volume. Outside Development it must be set; there is no default there.</summary>
    public string? LocalPath { get; set; }

    public long LargestLimit => Math.Max(MaxDatasheetBytes, Math.Max(MaxCadBytes, MaxCertificateBytes));
}

/// <summary>Section <c>Antivirus</c>: the clamd daemon. Outside Development a host must be set.</summary>
public sealed class AntivirusOptions
{
    public const string SectionName = "Antivirus";

    public string? Host { get; set; }
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 60;
}
