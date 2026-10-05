namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>Mapped to HTTP 409 by <c>ExceptionHandlingMiddleware</c> - an application was asked to run while its
/// setup is incomplete. <see cref="Missing"/> lists what is outstanding (field labels) so the caller can send the
/// user straight to the right place in the setup wizard.</summary>
public class SetupIncompleteException : Exception
{
    public const string DefaultMessage = "Please complete the required setup before running this application.";

    public SetupIncompleteException(IReadOnlyList<string> missing) : base(DefaultMessage)
    {
        Missing = missing;
    }

    public IReadOnlyList<string> Missing { get; }
}
