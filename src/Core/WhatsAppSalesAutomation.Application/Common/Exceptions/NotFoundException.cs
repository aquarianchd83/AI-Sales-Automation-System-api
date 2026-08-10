namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>Mapped to HTTP 404 by <c>ExceptionHandlingMiddleware</c>.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string entityName, object key)
        : base($"{entityName} with id '{key}' was not found.")
    {
    }

    public NotFoundException(string message) : base(message)
    {
    }
}
