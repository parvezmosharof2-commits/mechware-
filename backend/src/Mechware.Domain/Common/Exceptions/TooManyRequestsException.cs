namespace Mechware.Domain.Common.Exceptions;

/// <summary>
/// A business throttle refused the request (maps to HTTP 429 with the same body as the endpoint rate limiter, so a caller
/// cannot tell which limit it hit, or whether an account was involved).
/// </summary>
public sealed class TooManyRequestsException() : DomainException(RateLimitedCode, GenericMessage)
{
    public const string RateLimitedCode = "RATE_LIMITED";
    public const string GenericMessage = "Too many requests. Please wait a moment and try again.";
}
