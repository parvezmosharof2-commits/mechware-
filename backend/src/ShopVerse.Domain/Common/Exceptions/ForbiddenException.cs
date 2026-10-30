namespace ShopVerse.Domain.Common.Exceptions;

/// <summary>The caller is known but lacks the role required for the action (maps to HTTP 403).</summary>
public sealed class ForbiddenException(string code, string message) : DomainException(code, message);
