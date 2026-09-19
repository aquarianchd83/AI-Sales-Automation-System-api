namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>Mapped to HTTP 403 by <c>ExceptionHandlingMiddleware</c>. A feature the platform operator has
/// not switched on for this tenant - the server-side half of a per-tenant feature switch, so hiding a
/// button in the UI is never the only control.</summary>
public class FeatureDisabledException : Exception
{
    public FeatureDisabledException(string message) : base(message)
    {
    }
}
