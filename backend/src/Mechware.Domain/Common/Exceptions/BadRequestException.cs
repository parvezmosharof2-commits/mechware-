namespace Mechware.Domain.Common.Exceptions;

/// <summary>A request refused as a whole with its own error code (maps to HTTP 400), e.g. <c>TOO_MANY_VARIANTS</c>.</summary>
public sealed class BadRequestException(string code, string message) : DomainException(code, message);
