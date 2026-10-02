namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>The platform's file storage (e.g. its S3 bucket) is misconfigured or unreachable. Not the tenant's mistake and not
/// a bug: the message tells the platform administrator what to fix, and is shown to the caller as a 503.</summary>
public class StorageUnavailableException : Exception
{
    public StorageUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
