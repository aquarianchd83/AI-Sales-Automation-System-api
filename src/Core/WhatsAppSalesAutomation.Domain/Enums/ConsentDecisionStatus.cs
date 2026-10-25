namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// What the customer decided about one consent request. Kept apart from <see cref="ConsentInvitationStatus"/> (did the SMS go out)
/// and from the WhatsApp conversation's own status: a delivered SMS is not consent, and a failed WhatsApp message never undoes a Granted.
/// Only an explicit, server-validated POST from the consent page moves a request to Granted or NotInterested; opening the link never does.
/// </summary>
public enum ConsentDecisionStatus
{
    /// <summary>A request exists but its link has not gone out (held, or the SMS is still being sent).</summary>
    Pending = 0,

    /// <summary>The SMS with the consent link was accepted by the provider; the customer has not answered.</summary>
    LinkSent = 1,

    /// <summary>The customer ticked the WhatsApp checkbox and chose "Yes, I Agree".</summary>
    Granted = 2,

    /// <summary>The customer chose "No, Not Interested". Final: no WhatsApp conversation is started from this request.</summary>
    NotInterested = 3,

    /// <summary>A Granted consent was later withdrawn.</summary>
    Revoked = 4,

    /// <summary>The link lapsed before the customer answered.</summary>
    Expired = 5
}

/// <summary>Whether the WhatsApp conversation that follows a Granted consent has been started. Separate from the consent itself.</summary>
public enum WhatsAppInitiationStatus
{
    /// <summary>Nothing started (no consent yet, or consent declined).</summary>
    NotStarted = 0,

    /// <summary>Claimed by exactly one worker; the first message has been handed to the existing campaign pipeline.</summary>
    Started = 1,

    /// <summary>The attempt failed (provider down, number disconnected). Consent stands; the retry sweep tries again.</summary>
    Failed = 2
}
