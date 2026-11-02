namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>The caller is signed in but may not do this. Answered with 403 and the message, which is written for the person who asked.</summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message)
    {
    }
}
