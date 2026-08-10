namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>Mapped to HTTP 409 by <c>ExceptionHandlingMiddleware</c>. Used for uniqueness violations, etc.</summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message)
    {
    }
}
