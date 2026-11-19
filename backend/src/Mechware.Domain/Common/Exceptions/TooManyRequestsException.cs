namespace Mechware.Domain.Common.Exceptions;

/// <summary>
/// A business throttle refused the request (maps to HTTP 429 with the same body as the endpoint rate limiter, so a caller
/// cannot tell which limit it hit, or whether an account was involved). A throttle that sits behind an access check, such as the
/// RFQ cap of one company, may name its own code, because its caller has already proved who they are.
/// </summary>
public sealed class TooManyRequestsException(string code = TooManyRequestsException.RateLimitedCode, string message = TooManyRequestsException.GenericMessage)
    : DomainException(code, message)
{
    public const string RateLimitedCode = "RATE_LIMITED";
    public const string GenericMessage = "Too many requests. Please wait a moment and try again.";
}
