using System.ComponentModel.DataAnnotations;

namespace Mechware.Infrastructure.Notifications;

/// <summary>SMTP settings. Section <c>Email</c>; keys <c>Email__Host</c>, <c>Email__Port</c>, <c>Email__Password</c>, ...</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>SMTP host. Empty in Development/Testing means "log only"; elsewhere it must be set.</summary>
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public bool UseTls { get; set; } = true;

    public string Username { get; set; } = "";

    /// <summary>Never logged.</summary>
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "Mechware";

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}
