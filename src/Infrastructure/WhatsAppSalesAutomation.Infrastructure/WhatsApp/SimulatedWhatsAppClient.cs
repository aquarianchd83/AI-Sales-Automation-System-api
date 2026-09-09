using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Stands in for the real Meta client when no WhatsApp Business Account is configured
/// (<c>WhatsApp:Provider = "Simulated"</c>, the default). Logs what would have been sent and
/// fabricates a message id, so the entire campaign pipeline - idempotency, follow-ups, retries - is
/// runnable and testable without live credentials. <see cref="WhatsAppSettings.SimulatedFailureRatePercent"/>
/// lets the retry path be exercised on demand instead of only in theory.
/// </summary>
public class SimulatedWhatsAppClient : IWhatsAppService
{
    private readonly WhatsAppSettings _settings;
    private readonly ILogger<SimulatedWhatsAppClient> _logger;
    private readonly Random _random = new();

    public SimulatedWhatsAppClient(IOptionsSnapshot<WhatsAppSettings> settings, ILogger<SimulatedWhatsAppClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task<WhatsAppSendResult> SendTemplateMessageAsync(
        string toPhoneNumberE164,
        string templateName,
        string languageCode,
        IReadOnlyList<string> parameterValues,
        string? mediaUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (TryInjectFailure(toPhoneNumberE164, out var failure))
            return Task.FromResult(failure);

        var messageId = $"sim.{Guid.NewGuid():N}";

        _logger.LogInformation(
            "[Simulated WhatsApp] Sent {MessageId} to {Phone}: template={Template} lang={Language} params=[{Params}] media={Media}",
            messageId, toPhoneNumberE164, templateName, languageCode, string.Join(", ", parameterValues), mediaUrl ?? "(none)");

        return Task.FromResult(WhatsAppSendResult.Succeeded(messageId));
    }

    public Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default)
    {
        if (TryInjectFailure(toPhoneNumberE164, out var failure))
            return Task.FromResult(failure);

        var messageId = $"sim.{Guid.NewGuid():N}";

        _logger.LogInformation("[Simulated WhatsApp] Sent {MessageId} (text) to {Phone}: {Text}", messageId, toPhoneNumberE164, text);

        return Task.FromResult(WhatsAppSendResult.Succeeded(messageId));
    }

    private bool TryInjectFailure(string toPhoneNumberE164, out WhatsAppSendResult failure)
    {
        if (_settings.SimulatedFailureRatePercent > 0 && _random.Next(100) < _settings.SimulatedFailureRatePercent)
        {
            _logger.LogWarning("[Simulated WhatsApp] Send FAILED (injected) to {Phone}", toPhoneNumberE164);
            failure = WhatsAppSendResult.Failed("Simulated transient failure");
            return true;
        }

        failure = null!;
        return false;
    }

    public Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var mediaId = $"sim-media.{Guid.NewGuid():N}";
        _logger.LogInformation("[Simulated WhatsApp] Uploaded media {MediaId} ({ContentType})", mediaId, contentType);
        return Task.FromResult(mediaId);
    }

    /// <summary>Empty, not fabricated data - there is no simulated WhatsApp Business Account for a
    /// template list to plausibly belong to (unlike sends, which just need *a* message id). A real
    /// review status is a genuine external fact this client has no basis to invent; MessageTemplateSyncJob
    /// harmlessly finds nothing to update, same as it would against a WABA with no templates yet.</summary>
    public Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[Simulated WhatsApp] GetMessageTemplatesAsync - no-op, returning an empty list.");
        return Task.FromResult<IReadOnlyList<WhatsAppRemoteTemplate>>(Array.Empty<WhatsAppRemoteTemplate>());
    }

    /// <summary>Unlike GetMessageTemplatesAsync, fabricating a result here is the right call, not a
    /// cop-out - the whole push half of MessageTemplateSyncJob (create, store MetaTemplateId, compare
    /// against LastPushedBodyText next run) should be exercisable locally with no real WABA, same
    /// reasoning as SendTemplateMessageAsync fabricating a message id.</summary>
    public Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        var metaTemplateId = $"sim-template.{Guid.NewGuid():N}";
        _logger.LogInformation(
            "[Simulated WhatsApp] Created template {MetaTemplateId}: name={Name} lang={Language} category={Category} body={Body}",
            metaTemplateId, submission.Name, submission.Language, submission.Category, submission.MetaBodyText);

        return Task.FromResult(new WhatsAppTemplateSubmitResult(true, metaTemplateId, "PENDING", null));
    }

    public Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[Simulated WhatsApp] Updated template {MetaTemplateId}: body={Body}", metaTemplateId, submission.MetaBodyText);

        return Task.FromResult(new WhatsAppTemplateSubmitResult(true, metaTemplateId, "PENDING", null));
    }
}
