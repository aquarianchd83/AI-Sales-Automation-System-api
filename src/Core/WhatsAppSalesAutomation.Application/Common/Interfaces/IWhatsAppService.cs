namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Sends messages via the WhatsApp Cloud API. Implemented in Infrastructure by a real Meta client
/// and a simulated one selected via <c>WhatsApp:Provider</c> config - the Application layer never
/// knows which is running, per the provider-abstraction principle in the Phase 1 design.
/// </summary>
public interface IWhatsAppService
{
    /// <summary>
    /// Sends an approved template message. <paramref name="parameterValues"/> fill the template's
    /// <c>{{1}}</c>, <c>{{2}}</c>... placeholders in order; <paramref name="mediaUrl"/>, if the
    /// template has a header media component, is the file WhatsApp should attach.
    /// </summary>
    Task<WhatsAppSendResult> SendTemplateMessageAsync(
        string toPhoneNumberE164,
        string templateName,
        string languageCode,
        IReadOnlyList<string> parameterValues,
        string? mediaUrl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file to Meta so it can be referenced by a WhatsApp media handle in a send. The
    /// returned id is cached on <c>MediaAsset.WhatsAppMediaId</c> so repeat sends of the same file
    /// do not re-upload it.
    /// </summary>
    Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}

public record WhatsAppSendResult(bool Success, string? WhatsAppMessageId, string? ErrorMessage)
{
    public static WhatsAppSendResult Succeeded(string whatsAppMessageId) => new(true, whatsAppMessageId, null);

    public static WhatsAppSendResult Failed(string errorMessage) => new(false, null, errorMessage);
}
