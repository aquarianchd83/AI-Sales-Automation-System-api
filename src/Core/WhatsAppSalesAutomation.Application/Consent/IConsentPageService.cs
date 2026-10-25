namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// The customer's side of a consent link. Anonymous: the secret in the link is the only credential, and it resolves the tenant, the customer
/// and the request - nothing the browser sends is trusted for those.
/// </summary>
public interface IConsentPageService
{
    /// <summary>
    /// What the link shows. Strictly read-only: opening a link (a person, an SMS scanner, a link preview) never records consent, never marks
    /// anything as seen and never starts a conversation.
    /// </summary>
    Task<PublicConsentPageDto> GetAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the customer's explicit choice. "agree" needs the checkbox and a page that has not changed under them; "decline" needs neither.
    /// Idempotent: repeating the same choice changes nothing and starts nothing twice; the opposite choice after a decision is refused.
    /// The consent is saved first; only then is the WhatsApp conversation started, once, and a failure to start it never undoes the consent.
    /// </summary>
    Task<ConsentDecisionResultDto> DecideAsync(string token, ConsentDecisionRequest request, ConsentClientInfo client, CancellationToken cancellationToken = default);
}
