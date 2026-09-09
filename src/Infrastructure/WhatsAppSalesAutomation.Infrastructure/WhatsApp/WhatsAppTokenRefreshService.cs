using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Real implementation, backed by Meta's documented long-lived-token exchange:
/// <c>GET /{version}/oauth/access_token?grant_type=fb_exchange_token&amp;client_id=...&amp;client_secret=...&amp;fb_exchange_token=...</c>
/// A long-lived user/system-user token is valid ~60 days; exchanging it again before it expires
/// resets that clock. Never exercised against a live Meta App in this codebase - same "treat first
/// real use as the actual first test" caveat as MetaWhatsAppCloudApiClient.
/// </summary>
public class WhatsAppTokenRefreshService : IWhatsAppTokenRefreshService
{
    // 60-day validity with a 10-day safety margin comfortably covers a missed run or two of the
    // daily refresh job (see RecurringJobsRegistrar) without ever letting the token actually expire.
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IWhatsAppTokenStore _tokenStore;
    private readonly IDateTimeProvider _dateTime;
    private readonly WhatsAppSettings _settings;
    private readonly ILogger<WhatsAppTokenRefreshService> _logger;

    public WhatsAppTokenRefreshService(
        HttpClient httpClient,
        IWhatsAppTokenStore tokenStore,
        IDateTimeProvider dateTime,
        IOptionsSnapshot<WhatsAppSettings> settings,
        ILogger<WhatsAppTokenRefreshService> logger)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _dateTime = dateTime;
        _settings = settings.Value;
        _logger = logger;

        var baseUrl = _settings.ApiBaseUrl.EndsWith('/') ? _settings.ApiBaseUrl : $"{_settings.ApiBaseUrl}/";
        _httpClient.BaseAddress = new Uri($"{baseUrl}{_settings.ApiVersion}/");
    }

    public async Task RefreshIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(_settings.Provider, "Meta", StringComparison.OrdinalIgnoreCase))
            return; // Simulated has no real token to refresh.

        if (string.IsNullOrWhiteSpace(_settings.AppId) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            _logger.LogWarning("WhatsApp token refresh skipped: AppId/AppSecret is not configured.");
            return;
        }

        var current = await _tokenStore.GetCurrentAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(current.AccessToken))
        {
            _logger.LogWarning("WhatsApp token refresh skipped: no current access token to exchange.");
            return;
        }

        // ExpiresAt is null both before the first successful refresh (unknown expiry - always due)
        // and after a refresh whose response had no expires_in (a token Meta reports never expiring) -
        // treating both as "due" costs one extra daily call in the second case, which is a fair
        // trade-off for not needing to distinguish "never checked" from "never expires" separately.
        var due = current.ExpiresAt is null || current.ExpiresAt.Value - _dateTime.UtcNow <= RefreshWindow;
        if (!due)
            return;

        try
        {
            var query = "oauth/access_token" +
                $"?grant_type=fb_exchange_token&client_id={Uri.EscapeDataString(_settings.AppId)}" +
                $"&client_secret={Uri.EscapeDataString(_settings.AppSecret)}&fb_exchange_token={Uri.EscapeDataString(current.AccessToken)}";

            using var response = await _httpClient.GetAsync(query, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("WhatsApp token refresh failed ({Status}): {Body}", response.StatusCode, body);
                return;
            }

            var parsed = JsonSerializer.Deserialize<MetaTokenExchangeResponse>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(parsed?.AccessToken))
            {
                _logger.LogWarning("WhatsApp token refresh returned no access_token: {Body}", body);
                return;
            }

            var expiresAt = parsed.ExpiresIn.HasValue ? _dateTime.UtcNow.AddSeconds(parsed.ExpiresIn.Value) : (DateTime?)null;
            await _tokenStore.SaveRefreshedTokenAsync(parsed.AccessToken, expiresAt, cancellationToken);

            _logger.LogInformation("WhatsApp access token refreshed - next expiry {ExpiresAt}", expiresAt?.ToString("O") ?? "unknown/never");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "WhatsApp token refresh threw");
        }
    }

    private class MetaTokenExchangeResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public long? ExpiresIn { get; set; }
    }
}
