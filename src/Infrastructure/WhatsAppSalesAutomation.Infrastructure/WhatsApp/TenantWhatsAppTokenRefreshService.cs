using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Backed by Meta's documented long-lived-token exchange, called against the tenant's own
/// ApiBaseUrl/ApiVersion:
/// <c>GET /{version}/oauth/access_token?grant_type=fb_exchange_token&amp;client_id=...&amp;client_secret=...&amp;fb_exchange_token=...</c>
/// A long-lived user token is valid ~60 days, and exchanging it again before it expires resets that
/// clock. Never exercised against a live Meta App in this codebase - same "treat first real use as the
/// actual first test" caveat as MetaWhatsAppCloudApiClient.
///
/// The typed HttpClient behind this has its loggers removed (see DependencyInjection.AddWhatsAppClient).
/// Meta takes client_secret and the token as query parameters, and IHttpClientFactory's default logging
/// writes every request URL at Information level - which this app's Serilog configuration keeps - so
/// without that, every tenant's App Secret and access token would be written to the log files that the
/// LogViewer screen serves.
/// </summary>
public class TenantWhatsAppTokenRefreshService : ITenantWhatsAppTokenRefreshService
{
    /// <summary>A 60-day token with a 10-day margin tolerates many missed daily runs before it lapses.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(10);

    private const int MaxMetaErrorLength = 300;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _context;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDateTimeProvider _dateTime;

    public TenantWhatsAppTokenRefreshService(
        HttpClient httpClient,
        ApplicationDbContext context,
        IDataProtectionProvider dataProtectionProvider,
        IDateTimeProvider dateTime)
    {
        _httpClient = httpClient;
        _context = context;
        _dataProtectionProvider = dataProtectionProvider;
        _dateTime = dateTime;

        // Same reasoning as MetaWhatsAppCloudApiClient's own timeout: fail fast into the job's next run
        // rather than hang a Hangfire worker on a dead connection.
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<TenantWhatsAppTokenRefreshResult> RefreshAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // By explicit tenant id, bypassing the ITenantOwned filter - same as the provider's own
        // GetConfigForTenantAsync, so this reads the right row regardless of which tenant is ambient.
        var row = await _context.TenantWhatsAppConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (row?.AccessToken is null)
            return new(TenantWhatsAppTokenRefreshStatus.NotConfigured, null, "skipped: no WhatsApp access token configured");

        if (string.IsNullOrWhiteSpace(row.AppId) || row.AppSecret is null)
        {
            var missing = string.IsNullOrWhiteSpace(row.AppId) ? "App ID" : "App Secret";
            return new(TenantWhatsAppTokenRefreshStatus.MissingAppCredentials, row.AccessTokenExpiresAtUtc,
                $"cannot refresh: {missing} is not set - fine for a never-expiring System User token, " +
                "but a 60-day token will lapse");
        }

        var now = _dateTime.UtcNow;

        if (row.AccessTokenRefreshedAtUtc is not null && row.AccessTokenExpiresAtUtc is null)
            return new(TenantWhatsAppTokenRefreshStatus.NeverExpires, null, "not needed: Meta reports this token does not expire");

        if (row.AccessTokenExpiresAtUtc is { } expiresAt && expiresAt - now > RefreshWindow)
            return new(TenantWhatsAppTokenRefreshStatus.NotDue, expiresAt, $"not due: expires {expiresAt:yyyy-MM-dd}");

        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);
        var accessToken = Unprotect(protector, row.AccessToken, "access token");
        var appSecret = Unprotect(protector, row.AppSecret, "App Secret");

        var exchanged = await ExchangeAsync(row.ApiBaseUrl, row.ApiVersion, row.AppId, appSecret, accessToken, cancellationToken);

        // expires_in absent (or non-positive) is how Meta describes a token that does not expire - kept as
        // a real null rather than a made-up far-future date, and recognised by the NeverExpires check above
        // on every later run.
        DateTime? newExpiresAt = exchanged.ExpiresIn is > 0 ? now.AddSeconds(exchanged.ExpiresIn.Value) : null;

        row.AccessToken = protector.Protect(exchanged.AccessToken!);
        row.AccessTokenExpiresAtUtc = newExpiresAt;
        row.AccessTokenRefreshedAtUtc = now;
        await _context.SaveChangesAsync(cancellationToken);

        return new(TenantWhatsAppTokenRefreshStatus.Refreshed, newExpiresAt,
            newExpiresAt is null ? "refreshed: Meta reports no expiry" : $"refreshed: expires {newExpiresAt:yyyy-MM-dd}");
    }

    private async Task<MetaTokenExchangeResponse> ExchangeAsync(
        string apiBaseUrl,
        string apiVersion,
        string appId,
        string appSecret,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var baseUrl = apiBaseUrl.EndsWith('/') ? apiBaseUrl : $"{apiBaseUrl}/";
        var uri = new Uri(new Uri($"{baseUrl}{apiVersion}/"),
            "oauth/access_token?grant_type=fb_exchange_token" +
            $"&client_id={Uri.EscapeDataString(appId)}" +
            $"&client_secret={Uri.EscapeDataString(appSecret)}" +
            $"&fb_exchange_token={Uri.EscapeDataString(accessToken)}");

        string body;
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(uri, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Neither exception's message includes the request URL, so it is safe to surface as-is.
            throw new InvalidOperationException($"Meta token exchange request failed: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Meta rejected the token exchange ({(int)response.StatusCode}): {DescribeMetaError(body)}");
        }

        MetaTokenExchangeResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MetaTokenExchangeResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Meta token exchange returned a response that is not JSON.");
        }

        if (string.IsNullOrWhiteSpace(parsed?.AccessToken))
            throw new InvalidOperationException("Meta token exchange succeeded but returned no access_token.");

        return parsed;
    }

    /// <summary>Meta's own error message (e.g. "Error validating access token: Session has expired"),
    /// which is what an operator needs to act on. Meta error bodies do not echo request parameters, but
    /// this still extracts just the message and caps its length rather than storing a raw body.</summary>
    private static string DescribeMetaError(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<MetaErrorEnvelope>(body, JsonOptions)?.Error;
            if (!string.IsNullOrWhiteSpace(error?.Message))
                return Cap(error.Code is null ? error.Message : $"{error.Message} (code {error.Code})");
        }
        catch (JsonException)
        {
            // Fall through to the generic description below.
        }

        return "no error message in the response";
    }

    private static string Unprotect(IDataProtector protector, string ciphertext, string what)
    {
        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (Exception)
        {
            // Unlike the provider's own tolerant TryUnprotect, a refresh cannot proceed without the value -
            // and an undecryptable secret (key ring rotated, manual DB edit) means this tenant's sends are
            // failing too, which is exactly what should surface on its job row.
            throw new InvalidOperationException($"The stored {what} could not be decrypted - re-enter the WhatsApp credentials.");
        }
    }

    private static string Cap(string value) => value.Length > MaxMetaErrorLength ? value[..MaxMetaErrorLength] : value;

    private class MetaTokenExchangeResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public long? ExpiresIn { get; set; }
    }

    private class MetaErrorEnvelope
    {
        [JsonPropertyName("error")]
        public MetaError? Error { get; set; }
    }

    private class MetaError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("code")]
        public int? Code { get; set; }
    }
}
