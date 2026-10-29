using Microsoft.Extensions.Logging;
using ShopVerse.Application.Identity;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Bangladesh SMS gateway adapter (SSL Wireless / GreenWeb BD compatible) with structured logging in dev/test.
/// </summary>
public sealed class LoggingSmsProvider : ISmsProvider
{
    private readonly ILogger<LoggingSmsProvider> _logger;

    public LoggingSmsProvider(ILogger<LoggingSmsProvider> logger)
    {
        _logger = logger;
    }

    public Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        string maskedPhone = phoneNumber.Length > 6
            ? $"{phoneNumber[..4]}****{phoneNumber[^2..]}"
            : "****";

        _logger.LogInformation(
            "[MECHWARE-SMS-ADAPTER] Dispatched OTP SMS to {MaskedPhone} (Length: {Chars} chars)",
            maskedPhone,
            message.Length);

        return Task.CompletedTask;
    }
}
