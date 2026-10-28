using FluentValidation;
using FluentValidation.Results;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One selectable provider. <paramref name="Available"/> is false while it cannot be activated (the bridge is not configured yet) -
/// it is listed so the console can show what is missing, but cannot be chosen.</summary>
public record WhatsAppProviderOptionDto(string Id, string Name, bool Available, string? UnavailableReason);

/// <param name="Active">The provider every tenant's send/receive goes through.</param>
public record WhatsAppProviderSettingsDto(string Active, IReadOnlyList<WhatsAppProviderOptionDto> Providers);

public record UpdateWhatsAppProviderRequest(string Provider);

public interface IWhatsAppProviderSettingsService
{
    Task<WhatsAppProviderSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The provider in force right now: what is stored, or Meta when nothing is (every platform from before this setting existed).</summary>
    Task<string> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates, then stores the new active provider. A failure leaves the previous one in force. <see cref="WhatsAppProviderChange.Previous"/>
    /// is what the audit log records alongside the new value.</summary>
    Task<WhatsAppProviderChange> UpdateAsync(UpdateWhatsAppProviderRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    Task<TenantWhatsAppProviderDto> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Sets (or clears, with a null provider) one tenant's own choice. QR needs a configured, reachable bridge, like the global switch.</summary>
    Task<TenantWhatsAppProviderDto> SetForTenantAsync(Guid tenantId, UpdateTenantWhatsAppProviderRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

public record WhatsAppProviderChange(string Previous, WhatsAppProviderSettingsDto Settings);

/// <param name="Override">The tenant's own choice, or null when it follows the platform default.</param>
/// <param name="Effective">What the tenant really goes through right now.</param>
/// <param name="PlatformDefault">The platform-wide active provider.</param>
public record TenantWhatsAppProviderDto(string? Override, string Effective, string PlatformDefault);

/// <param name="Provider">"Meta", "QrBridge", or null to follow the platform default.</param>
public record UpdateTenantWhatsAppProviderRequest(string? Provider);

/// <summary>
/// Platform-wide choice of WhatsApp provider, stored in the AppSettings table. Activating the QR bridge requires its settings to be saved and
/// the bridge to answer with the saved key: an unreachable or unconfigured bridge can never become the active provider, so the choice
/// cannot silently leave every send with nothing to deliver it.
/// </summary>
public class WhatsAppProviderSettingsService : IWhatsAppProviderSettingsService
{
    public const string ActiveKey = "WhatsAppProvider:Active";
    public const string TenantOverridesKey = "WhatsAppProvider:TenantOverrides";

    private const string BridgeNotConfigured = "Save the bridge address and API key in the bridge settings first.";

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdateWhatsAppProviderRequest> _validator;
    private readonly IWhatsAppBridgeSettingsService _bridgeSettings;
    private readonly IWhatsAppBridgeClient _bridgeClient;
    private readonly IInFlightSendCounter _inFlight;

    public WhatsAppProviderSettingsService(
        IAppSettingsStore store, IValidator<UpdateWhatsAppProviderRequest> validator,
        IWhatsAppBridgeSettingsService bridgeSettings, IWhatsAppBridgeClient bridgeClient, IInFlightSendCounter inFlight)
    {
        _store = store;
        _validator = validator;
        _bridgeSettings = bridgeSettings;
        _bridgeClient = bridgeClient;
        _inFlight = inFlight;
    }

    public async Task<WhatsAppProviderSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await GetActiveAsync(cancellationToken), bridgeConfigured: await _bridgeSettings.GetEndpointAsync(cancellationToken) is not null);

    public async Task<string> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return Normalize(stored.TryGetValue(ActiveKey, out var value) ? value : null);
    }

    public async Task<WhatsAppProviderChange> UpdateAsync(UpdateWhatsAppProviderRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var previous = await GetActiveAsync(cancellationToken);
        var next = Normalize(request.Provider);
        var endpoint = await _bridgeSettings.GetEndpointAsync(cancellationToken);

        if (next == WhatsAppProviders.QrBridge)
        {
            if (endpoint is null)
                throw Fail(BridgeNotConfigured);

            try
            {
                await _bridgeClient.PingAsync(endpoint, cancellationToken);
            }
            catch (WhatsAppBridgeException ex)
            {
                throw Fail($"The bridge is not reachable with the saved settings: {ex.Message}");
            }
        }

        if (next != previous)
        {
            // Never switch under a send whose outcome is not known yet: the old provider may already have accepted it and the new one would send it again.
            var inFlight = await _inFlight.CountAsync(cancellationToken);
            if (inFlight > 0)
                throw Fail($"{inFlight} message(s) are being sent right now or their outcome is not known yet. Wait for them to finish (a few minutes), then switch.");

            await _store.UpsertAsync(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [ActiveKey] = next }, updatedByUserId, cancellationToken);
        }

        return new WhatsAppProviderChange(previous, ToDto(next, endpoint is not null));
    }

    public async Task<TenantWhatsAppProviderDto> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return ToTenantDto(stored, tenantId);
    }

    public async Task<TenantWhatsAppProviderDto> SetForTenantAsync(
        Guid tenantId, UpdateTenantWhatsAppProviderRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        if (request.Provider is not null && request.Provider.Trim() is not (WhatsAppProviders.Meta or WhatsAppProviders.QrBridge))
            throw Fail("Choose \"Meta\", \"QrBridge\", or leave it empty to follow the platform default.");

        var stored = await _store.GetAllAsync(cancellationToken);
        var current = ToTenantDto(stored, tenantId);
        var wanted = request.Provider is null ? null : Normalize(request.Provider);

        if (wanted == WhatsAppProviders.QrBridge)
        {
            var endpoint = await _bridgeSettings.GetEndpointAsync(cancellationToken) ?? throw Fail(BridgeNotConfigured);
            try
            {
                await _bridgeClient.PingAsync(endpoint, cancellationToken);
            }
            catch (WhatsAppBridgeException ex)
            {
                throw Fail($"The bridge is not reachable with the saved settings: {ex.Message}");
            }
        }

        var effectiveAfter = wanted ?? current.PlatformDefault;
        if (wanted != current.Override && effectiveAfter != current.Effective)
        {
            var inFlight = await _inFlight.CountAsync(cancellationToken);
            if (inFlight > 0)
                throw Fail($"{inFlight} message(s) are being sent right now or their outcome is not known yet. Wait for them to finish (a few minutes), then switch.");
        }

        var overrides = WhatsAppProviderRouting.With(ReadOverrides(stored), tenantId, wanted);
        await _store.UpsertAsync(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [TenantOverridesKey] = string.Join(",", overrides) }, updatedByUserId, cancellationToken);
        return ToTenantDto(new Dictionary<string, string?>(stored, StringComparer.OrdinalIgnoreCase) { [TenantOverridesKey] = string.Join(",", overrides) }, tenantId);
    }

    private static IEnumerable<string> ReadOverrides(IReadOnlyDictionary<string, string?> stored) =>
        (stored.TryGetValue(TenantOverridesKey, out var raw) ? raw ?? string.Empty : string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static TenantWhatsAppProviderDto ToTenantDto(IReadOnlyDictionary<string, string?> stored, Guid tenantId)
    {
        var platformDefault = Normalize(stored.TryGetValue(ActiveKey, out var active) ? active : null);
        var overrides = ReadOverrides(stored).ToList();
        var own = WhatsAppProviderRouting.OverrideFor(overrides, tenantId);
        var pilots = (stored.TryGetValue(WhatsAppBridgeSettingsService.PilotTenantIdsKey, out var raw) ? raw ?? string.Empty : string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null).Where(id => id.HasValue).Select(id => id!.Value).ToList();
        return new TenantWhatsAppProviderDto(own, WhatsAppProviderRouting.Resolve(platformDefault, overrides, pilots, tenantId), platformDefault);
    }

    /// <summary>Anything unknown or empty means Meta - the only provider that existed before this setting.</summary>
    private static string Normalize(string? value) =>
        string.Equals(value?.Trim(), WhatsAppProviders.QrBridge, StringComparison.OrdinalIgnoreCase) ? WhatsAppProviders.QrBridge : WhatsAppProviders.Meta;

    private static ValidationException Fail(string message) =>
        new(new[] { new ValidationFailure(nameof(UpdateWhatsAppProviderRequest.Provider), message) });

    private static WhatsAppProviderSettingsDto ToDto(string active, bool bridgeConfigured) => new(active, new[]
    {
        new WhatsAppProviderOptionDto(WhatsAppProviders.Meta, "Meta WhatsApp Cloud API", true, null),
        new WhatsAppProviderOptionDto(WhatsAppProviders.QrBridge, "QR-Code WhatsApp Bridge", bridgeConfigured, bridgeConfigured ? null : BridgeNotConfigured),
    });
}

public class UpdateWhatsAppProviderRequestValidator : AbstractValidator<UpdateWhatsAppProviderRequest>
{
    public UpdateWhatsAppProviderRequestValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty()
            .Must(p => p is WhatsAppProviders.Meta or WhatsAppProviders.QrBridge)
            .WithMessage("Choose \"Meta\" or \"QrBridge\".");
    }
}
