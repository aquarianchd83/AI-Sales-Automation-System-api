namespace WhatsAppSalesAutomation.Application.Common.Exceptions;

/// <summary>Mapped to HTTP 402 (Payment Required) by <c>ExceptionHandlingMiddleware</c> - thrown by
/// <c>IPlanLimitsService</c>'s Ensure* guards when a tenant is already at its plan's limit for the
/// resource being created/sent. 402 rather than 403: this isn't a permissions problem the caller
/// could retry as a different role, it's "upgrade your plan," which is what the frontend should show.</summary>
public class PlanLimitExceededException : Exception
{
    public PlanLimitExceededException(string message) : base(message)
    {
    }
}
