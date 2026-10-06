using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Verifies a tenant's WhatsApp connection the cheapest way Meta allows: <c>GET /{version}/{phone-number-id}</c>
/// with the stored token. A 200 proves the token is valid and can reach that exact number; the reply's
/// display number and verified name are kept so the screen shows which number is connected.
///
/// The token travels in the Authorization header, never the URL, and the typed HttpClient has its loggers removed
/// anyway (see DependencyInjection.AddWhatsAppClient) - the same care TenantWhatsAppTokenRefreshService takes.
/// </summary>
public class TenantWhatsAppConnectionVerifier : ITenantWhatsAppConnectionVerifier
{
    private const int MaxErrorLength = 300;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _context;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDateTimeProvider _dateTime;

    public TenantWhatsAppConnectionVerifier(
        HttpClient httpClient, ApplicationDbContext context, IDataProtectionProvider dataProtectionProvider, IDateTimeProvider dateTime)
    {
        _httpClient = httpClient;
        _context = context;
        _dataProtectionProvider = dataProtectionProvider;
        _dateTime = dateTime;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<TenantWhatsAppConfigDto> VerifyAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // By explicit tenant id: the caller is either that tenant's Admin or a PlatformSuperAdmin.
        var row = await _context.TenantWhatsAppConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (row is null || string.IsNullOrWhiteSpace(row.PhoneNumberId) || row.AccessToken is null)
            throw new ConflictException("Save the phone number ID and access token first, then verify the connection.");

        var (ok, displayNumber, verifiedName, error) = await AskMetaAsync(row, cancellationToken);

        row.VerifiedAtUtc = ok ? _dateTime.UtcNow : null;
        row.VerificationError = ok ? null : Cap(error ?? "Meta did not confirm the connection.");
        row.VerifiedDisplayPhoneNumber = ok ? displayNumber : null;
        row.VerifiedName = ok ? verifiedName : null;
        await _context.SaveChangesAsync(cancellationToken);

        return TenantWhatsAppConfigProvider.ToDto(row);
    }

    private async Task<(bool Ok, string? DisplayNumber, string? VerifiedName, string? Error)> AskMetaAsync(
        TenantWhatsAppConfig row, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider).Unprotect(row.AccessToken!);
        }
        catch (Exception)
        {
            return (false, null, null, "The stored access token could not be decrypted - enter it again and save.");
        }

        var baseUrl = row.ApiBaseUrl.EndsWith('/') ? row.ApiBaseUrl : $"{row.ApiBaseUrl}/";
        var uri = new Uri(new Uri($"{baseUrl}{row.ApiVersion}/"),
            $"{Uri.EscapeDataString(row.PhoneNumberId)}?fields=display_phone_number,verified_name");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, null, null, $"Meta rejected the connection ({(int)response.StatusCode}): {DescribeMetaError(body)}");

            var phone = JsonSerializer.Deserialize<MetaPhoneNumber>(body, JsonOptions);
            return (true, phone?.DisplayPhoneNumber, phone?.VerifiedName, null);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (false, null, null, $"Could not reach Meta: {ex.Message}");
        }
    }

    private static string DescribeMetaError(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<MetaErrorEnvelope>(body, JsonOptions)?.Error;
            if (!string.IsNullOrWhiteSpace(error?.Message))
                return error.Code is null ? error.Message : $"{error.Message} (code {error.Code})";
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return "no error message in the response";
    }

    private static string Cap(string value) => value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;

    private sealed class MetaPhoneNumber
    {
        [JsonPropertyName("display_phone_number")]
        public string? DisplayPhoneNumber { get; set; }

        [JsonPropertyName("verified_name")]
        public string? VerifiedName { get; set; }
    }

    private sealed class MetaErrorEnvelope
    {
        [JsonPropertyName("error")]
        public MetaError? Error { get; set; }
    }

    private sealed class MetaError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("code")]
        public int? Code { get; set; }
    }
}
