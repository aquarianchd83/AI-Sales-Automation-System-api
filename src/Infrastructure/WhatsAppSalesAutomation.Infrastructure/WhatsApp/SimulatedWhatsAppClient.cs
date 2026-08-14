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

    public SimulatedWhatsAppClient(IOptions<WhatsAppSettings> settings, ILogger<SimulatedWhatsAppClient> logger)
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
        if (_settings.SimulatedFailureRatePercent > 0 && _random.Next(100) < _settings.SimulatedFailureRatePercent)
        {
            _logger.LogWarning(
                "[Simulated WhatsApp] Send FAILED (injected) to {Phone}: template={Template} lang={Language} params=[{Params}]",
                toPhoneNumberE164, templateName, languageCode, string.Join(", ", parameterValues));

            return Task.FromResult(WhatsAppSendResult.Failed("Simulated transient failure"));
        }

        var messageId = $"sim.{Guid.NewGuid():N}";

        _logger.LogInformation(
            "[Simulated WhatsApp] Sent {MessageId} to {Phone}: template={Template} lang={Language} params=[{Params}] media={Media}",
            messageId, toPhoneNumberE164, templateName, languageCode, string.Join(", ", parameterValues), mediaUrl ?? "(none)");

        return Task.FromResult(WhatsAppSendResult.Succeeded(messageId));
    }

    public Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var mediaId = $"sim-media.{Guid.NewGuid():N}";
        _logger.LogInformation("[Simulated WhatsApp] Uploaded media {MediaId} ({ContentType})", mediaId, contentType);
        return Task.FromResult(mediaId);
    }
}
