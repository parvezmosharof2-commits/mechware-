namespace ShopVerse.Domain.Common.Exceptions;

/// <summary>A request that is valid on its own but conflicts with current state (maps to HTTP 409).</summary>
public sealed class ConflictException(string code, string message) : DomainException(code, message);
