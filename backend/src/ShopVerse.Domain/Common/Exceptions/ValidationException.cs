namespace ShopVerse.Domain.Common.Exceptions;

public sealed record ValidationError(string Field, string Message);

/// <summary>
/// Exception thrown when domain or application validation fails.
/// </summary>
public sealed class ValidationException : DomainException
{
    public IReadOnlyList<ValidationError> Errors { get; }

    public ValidationException(IEnumerable<ValidationError> errors)
        : base("VALIDATION_ERROR", "One or more validation errors occurred.")
    {
        Errors = errors.ToList().AsReadOnly();
    }

    public ValidationException(string field, string message)
        : base("VALIDATION_ERROR", message)
    {
        Errors = new List<ValidationError> { new(field, message) }.AsReadOnly();
    }
}
