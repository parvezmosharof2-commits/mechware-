namespace ShopVerse.Domain.Common.Exceptions;

/// <summary>
/// Base exception for domain invariant violations.
/// </summary>
public abstract class DomainException : Exception
{
    public string Code { get; }

    protected DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    protected DomainException(string code, string message, Exception innerException) : base(message, innerException)
    {
        Code = code;
    }
}
