using FluentValidation;
using FluentValidation.Results;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.WhatsAppBridge;

/// <summary>The platform's bridge connection. The key never leaves the server: only whether one is stored, plus its last four characters.</summary>
public record WhatsAppBridgeSettingsDto(
    string BaseUrl,
    bool HasApiKey,
    string? ApiKeyHint,
    string WebhookBaseUrl,
    IReadOnlyList<Guid> PilotTenantIds,
    bool IsConfigured);

/// <param name="ApiKey">null keeps the stored key, "" clears it, anything else replaces it.</param>
/// <param name="PilotTenantIds">While non-empty, only these tenants are routed to the bridge when it is the active provider; every other
/// tenant stays on Meta. Empty means every tenant (platform-wide).</param>
public record UpdateWhatsAppBridgeSettingsRequest(string BaseUrl, string? ApiKey, string WebhookBaseUrl, IReadOnlyList<Guid>? PilotTenantIds);

public interface IWhatsAppBridgeSettingsService
{
    Task<WhatsAppBridgeSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The stored endpoint, or null until a base address and key are both saved.</summary>
    Task<BridgeEndpoint?> GetEndpointAsync(CancellationToken cancellationToken = default);

    /// <summary>The public address the bridge calls back on, no trailing slash; empty until saved.</summary>
    Task<string> GetWebhookBaseUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates, proves the bridge answers with the key, then stores. Anything wrong leaves the stored settings untouched.</summary>
    Task<WhatsAppBridgeSettingsDto> UpdateAsync(UpdateWhatsAppBridgeSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>Platform-admin-owned bridge settings, stored only in the AppSettings table (the key encrypted at rest).</summary>
public class WhatsAppBridgeSettingsService : IWhatsAppBridgeSettingsService
{
    public const string BaseUrlKey = "WhatsAppBridge:BaseUrl";
    public const string ApiKeyKey = "WhatsAppBridge:ApiKey";
    public const string WebhookBaseUrlKey = "WhatsAppBridge:WebhookBaseUrl";
    public const string PilotTenantIdsKey = "WhatsAppBridge:PilotTenantIds";

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdateWhatsAppBridgeSettingsRequest> _validator;
    private readonly IWhatsAppBridgeClient _client;

    public WhatsAppBridgeSettingsService(IAppSettingsStore store, IValidator<UpdateWhatsAppBridgeSettingsRequest> validator, IWhatsAppBridgeClient client)
    {
        _store = store;
        _validator = validator;
        _client = client;
    }

    public async Task<WhatsAppBridgeSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await _store.GetAllAsync(cancellationToken));

    public async Task<BridgeEndpoint?> GetEndpointAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        var url = Get(stored, BaseUrlKey);
        var key = Get(stored, ApiKeyKey);
        return url.Length == 0 || key.Length == 0 ? null : new BridgeEndpoint(url.TrimEnd('/'), key);
    }

    public async Task<string> GetWebhookBaseUrlAsync(CancellationToken cancellationToken = default) =>
        Get(await _store.GetAllAsync(cancellationToken), WebhookBaseUrlKey).TrimEnd('/');

    public async Task<WhatsAppBridgeSettingsDto> UpdateAsync(UpdateWhatsAppBridgeSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var stored = await _store.GetAllAsync(cancellationToken);
        var key = request.ApiKey is null ? Get(stored, ApiKeyKey) : request.ApiKey.Trim();
        var baseUrl = request.BaseUrl.Trim().TrimEnd('/');

        // Never overwrite a working configuration with one the bridge does not accept.
        if (baseUrl.Length > 0 && key.Length > 0)
        {
            try
            {
                await _client.PingAsync(new BridgeEndpoint(baseUrl, key), cancellationToken);
            }
            catch (WhatsAppBridgeException ex)
            {
                throw new ValidationException(new[] { new ValidationFailure(nameof(request.BaseUrl), $"The bridge did not accept these settings: {ex.Message}") });
            }
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [BaseUrlKey] = baseUrl,
            [WebhookBaseUrlKey] = request.WebhookBaseUrl.Trim().TrimEnd('/'),
            [PilotTenantIdsKey] = string.Join(",", (request.PilotTenantIds ?? Array.Empty<Guid>()).Distinct()),
        };
        if (request.ApiKey is not null)
            values[ApiKeyKey] = key;

        await _store.UpsertAsync(values, updatedByUserId, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static WhatsAppBridgeSettingsDto ToDto(IReadOnlyDictionary<string, string?> stored)
    {
        var key = Get(stored, ApiKeyKey);
        var url = Get(stored, BaseUrlKey);
        var pilots = Get(stored, PilotTenantIdsKey)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue).Select(id => id!.Value).ToList();

        return new WhatsAppBridgeSettingsDto(
            url, key.Length > 0, key.Length == 0 ? null : key.Length <= 4 ? "••••" : $"••••{key[^4..]}",
            Get(stored, WebhookBaseUrlKey), pilots, url.Length > 0 && key.Length > 0);
    }
}

public class UpdateWhatsAppBridgeSettingsRequestValidator : AbstractValidator<UpdateWhatsAppBridgeSettingsRequest>
{
    public UpdateWhatsAppBridgeSettingsRequestValidator()
    {
        RuleFor(x => x.BaseUrl).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || IsHttp(v.Trim())))
            .WithMessage("The bridge address must be an http or https address, for example https://bridge.example.com.");
        RuleFor(x => x.WebhookBaseUrl).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || IsHttp(v.Trim())))
            .WithMessage("The webhook address must be an http or https address the bridge can reach this API on.");
        RuleFor(x => x.ApiKey).MaximumLength(500);
    }

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
