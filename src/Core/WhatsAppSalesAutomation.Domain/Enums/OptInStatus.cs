namespace WhatsAppSalesAutomation.Domain.Enums;

public enum OptInStatus
{
    PendingOptIn = 0,
    OptedIn = 1,
    OptedOut = 2,

    /// <summary>A new customer of a tenant that requires consent, who has not given it yet. Not consent: only OptedIn can be messaged.</summary>
    WaitingForConsent = 3
}
