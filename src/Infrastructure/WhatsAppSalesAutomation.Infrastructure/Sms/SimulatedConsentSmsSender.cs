using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Sms;

/// <summary>
/// The no-cost <see cref="IConsentSmsSender"/> until a real SMS provider is chosen. Nothing is sent anywhere. Deterministic, so a number
/// behaves the same every time: one ending in 99 is rejected as unreachable (a permanent failure), one ending in 98 is rejected as a
/// provider outage (a transient one, so it is retried and eventually stays Failed), and everything else is accepted and reported
/// delivered in the same call. A real provider answers delivery later, by a receipt.
/// </summary>
public class SimulatedConsentSmsSender : IConsentSmsSender
{
    private readonly ILogger<SimulatedConsentSmsSender> _logger;
    private readonly ISimulatedSmsOutbox? _outbox;
    private readonly ITenantContext? _tenant;
    private readonly IDateTimeProvider? _clock;
    private readonly ConsentPageOptions? _options;

    /// <param name="outbox">Demo only: when given, and ConsentPage:DemoOutboxEnabled is on, each message is kept so its link can be clicked.</param>
    public SimulatedConsentSmsSender(
        ILogger<SimulatedConsentSmsSender> logger,
        ISimulatedSmsOutbox? outbox = null,
        ITenantContext? tenant = null,
        IDateTimeProvider? clock = null,
        IOptions<ConsentPageOptions>? options = null)
    {
        _logger = logger;
        _outbox = outbox;
        _tenant = tenant;
        _clock = clock;
        _options = options?.Value;
    }

    public Task<ConsentSmsSendResult> SendAsync(ConsentSmsRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Simulated consent SMS to {To}: {Body} (no SMS was sent)", request.ToPhoneE164, request.Body);

        if (_options?.DemoOutboxEnabled == true && _outbox is not null && _tenant?.TenantId is { } tenantId)
            _outbox.Add(tenantId, request.ToPhoneE164, request.Body, _clock?.UtcNow ?? DateTime.UtcNow);

        if (request.ToPhoneE164.EndsWith("99", StringComparison.Ordinal))
            return Task.FromResult(new ConsentSmsSendResult(false, null, "The number is unreachable (simulated).", Transient: false));

        if (request.ToPhoneE164.EndsWith("98", StringComparison.Ordinal))
            return Task.FromResult(new ConsentSmsSendResult(false, null, "The SMS provider is unavailable (simulated).", Transient: true));

        return Task.FromResult(new ConsentSmsSendResult(
            true,
            $"sim-sms-{Guid.NewGuid():N}",
            null,
            ImmediateReceipt: new ConsentSmsReceipt(ConsentInvitationStatus.Delivered)));
    }
}
