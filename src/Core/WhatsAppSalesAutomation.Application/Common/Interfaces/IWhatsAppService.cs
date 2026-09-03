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
    /// Sends free-form text - only valid within WhatsApp's 24-hour customer service window after the
    /// customer's last inbound message (Phase 4's ConversationService is what actually enforces that
    /// window; this method just performs the send). Never used by the campaign pipeline, which is
    /// always business-initiated and so always requires a template regardless of any window.
    /// </summary>
    Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file to Meta so it can be referenced by a WhatsApp media handle in a send. The
    /// returned id is cached on <c>MediaAsset.WhatsAppMediaId</c> so repeat sends of the same file
    /// do not re-upload it.
    /// </summary>
    Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every template currently registered against the configured WhatsApp Business Account, with
    /// Meta's own review status - used by MessageTemplateSyncJob to keep local MessageTemplate rows'
    /// WhatsAppTemplateStatus current without a human manually re-checking Meta's dashboard. Read-only:
    /// this never creates or edits anything on Meta's side.
    /// </summary>
    Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a brand-new template with Meta - the first half of MessageTemplateSyncJob's push
    /// direction (local Campaign template is authoritative; this and UpdateMessageTemplateAsync are
    /// how its content actually reaches Meta, rather than a human recreating it by hand in Meta's own
    /// UI). Meta typically returns Status "PENDING" immediately, occasionally an instant "APPROVED" for
    /// simple UTILITY templates - never assume PENDING.
    /// </summary>
    Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits an already-created template's body (Meta does not allow changing name/language/category
    /// after creation, only content) - Meta resets it to PENDING re-review as a side effect, mirroring
    /// what MessageTemplateService.UpdateAsync already does locally the moment a local edit is saved.
    /// </summary>
    Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default);
}

public record WhatsAppSendResult(bool Success, string? WhatsAppMessageId, string? ErrorMessage)
{
    public static WhatsAppSendResult Succeeded(string whatsAppMessageId) => new(true, whatsAppMessageId, null);

    public static WhatsAppSendResult Failed(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>One template as Meta currently reports it. <paramref name="Status"/> is Meta's raw string
/// (e.g. "APPROVED", "REJECTED", "PENDING", "PAUSED", "DISABLED") - MessageTemplateService maps it to
/// WhatsAppTemplateStatus, not this record, since the mapping is a business rule, not a WhatsApp
/// integration detail. <paramref name="Id"/> is Meta's own template id - MessageTemplateService's pull
/// phase backfills a matched local template's MetaTemplateId from this when it was null, so a template
/// that already existed on Meta before ever being pushed (found live: Meta's own "hello_world" sample
/// template) is recognized as already-created instead of the next push attempting - and failing - to
/// create it again.</summary>
public record WhatsAppRemoteTemplate(string Id, string Name, string Language, string Status, string Category);

/// <summary>What CreateMessageTemplateAsync/UpdateMessageTemplateAsync send to Meta.
/// <paramref name="MetaBodyText"/>/<paramref name="ExampleValues"/> already have named placeholders
/// converted to Meta's positional <c>{{1}}</c>/<c>{{2}}</c> syntax with example values - see
/// <c>TemplatePlaceholderResolver.ToMetaTemplateBody</c>, which is what produces them.</summary>
public record WhatsAppTemplateSubmission(
    string Name,
    string Language,
    string Category,
    string MetaBodyText,
    IReadOnlyList<string> ExampleValues);

/// <summary><paramref name="Status"/> is Meta's raw string, same convention as WhatsAppRemoteTemplate.
/// A failure is a normal, expected outcome (an invalid template name, a policy violation) - not every
/// caller needs to treat it as exceptional, hence a result record rather than a thrown exception.</summary>
public record WhatsAppTemplateSubmitResult(bool Success, string? MetaTemplateId, string? Status, string? ErrorMessage);
