namespace Mechware.Domain.Common.Exceptions;

/// <summary>
/// A well-formed request the server cannot carry out as asked, for a reason the client must act on (maps to HTTP 422):
/// for example an order whose delivery cannot be priced automatically. <see cref="Details"/> names what is affected.
/// </summary>
public sealed class UnprocessableException(string code, string message, IReadOnlyList<ValidationError>? details = null)
    : DomainException(code, message)
{
    public IReadOnlyList<ValidationError> Details { get; } = details ?? [];
}
