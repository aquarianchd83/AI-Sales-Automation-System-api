namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>Mapped to HTTP 401 by <c>ExceptionHandlingMiddleware</c>. Message is deliberately generic to avoid user enumeration.</summary>
public class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(string message = "Invalid credentials.") : base(message)
    {
    }
}
