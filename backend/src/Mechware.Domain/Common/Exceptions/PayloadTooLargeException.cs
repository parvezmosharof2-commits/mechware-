namespace Mechware.Domain.Common.Exceptions;

/// <summary>An upload larger than its limit (maps to HTTP 413).</summary>
public sealed class PayloadTooLargeException(string code, string message) : DomainException(code, message);
