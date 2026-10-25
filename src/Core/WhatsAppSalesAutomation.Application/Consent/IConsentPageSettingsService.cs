namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>The tenant's own WhatsApp Consent Page: edit the draft, preview it, publish it, switch it off. Always the signed-in tenant's.</summary>
public interface IConsentPageSettingsService
{
    Task<ConsentPageSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the draft. May be incomplete, but a bad URL, markup or an over-long value is refused (400). Does not change what customers see.</summary>
    Task<ConsentPageSettingsDto> SaveAsync(SaveConsentPageSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>The saved draft as a customer would see it. Nothing is stored; no customer can reach it.</summary>
    Task<ConsentPagePreviewDto> PreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates the saved draft and makes it the live version (a new immutable snapshot). Refused with the list of problems when incomplete.</summary>
    Task<ConsentPageSettingsDto> PublishAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes the live page down (false) or puts the published version back (true). Customers with open links see "unavailable" meanwhile.</summary>
    Task<ConsentPageSettingsDto> SetActiveAsync(bool active, CancellationToken cancellationToken = default);
}
