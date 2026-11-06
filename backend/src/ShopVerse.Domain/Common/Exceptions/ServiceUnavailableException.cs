namespace ShopVerse.Domain.Common.Exceptions;

/// <summary>A dependency the request needs is down, so the request is refused rather than half done (maps to HTTP 503).</summary>
public sealed class ServiceUnavailableException(string code, string message) : DomainException(code, message);
