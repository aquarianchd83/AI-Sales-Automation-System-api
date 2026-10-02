using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Notifications;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

/// <summary>Bound from "Sms:Msg91", kept by the Platform Admin Console's Authentication Delivery page (the auth key encrypted).
/// In India the message text is a DLT-registered template that lives at MSG91, so the platform sends only the code and
/// the template id.</summary>
public class Msg91Options
{
    public bool Enabled { get; set; }

    public string AuthKey { get; set; } = string.Empty;

    /// <summary>The MSG91 OTP template (its message must use the ##OTP## variable).</summary>
    public string OtpTemplateId { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://control.msg91.com/api/v5/";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(AuthKey) && !string.IsNullOrWhiteSpace(OtpTemplateId);
}

/// <summary>Sends our own code through MSG91's OTP API. We generate and check the code ourselves (so it lives and dies with the
/// account's security stamp); MSG91 only delivers it.</summary>
public static class Msg91Client
{
    public static async Task<DeliveryResult> SendOtpAsync(
        HttpClient http, Msg91Options options, string toPhoneE164, string code, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new DeliveryResult(false, "SMS is not set up", Skipped: true);

        // MSG91 wants the country code and digits only: no +.
        var mobile = new string(toPhoneE164.Where(char.IsDigit).ToArray());
        var baseUrl = options.BaseUrl.TrimEnd('/') + "/";
        var url = $"{baseUrl}otp?template_id={Uri.EscapeDataString(options.OtpTemplateId)}&mobile={mobile}&otp={Uri.EscapeDataString(code)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            // In a header, not the query string, so it never lands in a URL log.
            request.Headers.Add("authkey", options.AuthKey);

            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (type, message) = Parse(body);

            if (response.IsSuccessStatusCode && string.Equals(type, "success", StringComparison.OrdinalIgnoreCase))
                return new DeliveryResult(true);

            logger.LogWarning("MSG91 rejected an OTP ({Status}): {Message}", (int)response.StatusCode, message);
            return new DeliveryResult(false, message ?? $"MSG91 returned {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MSG91 OTP request failed");
            return new DeliveryResult(false, ex.Message);
        }
    }

    private static (string? Type, string? Message) Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? Read(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) ? v.ToString() : null;
            return (Read("type"), Read("message"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

public class Msg91SmsSender : ISmsOtpSender
{
    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<Msg91Options> _options;
    private readonly ILogger<Msg91SmsSender> _logger;

    public Msg91SmsSender(HttpClient http, IOptionsSnapshot<Msg91Options> options, ILogger<Msg91SmsSender> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public Task<DeliveryResult> SendOtpAsync(string toPhoneE164, string code, CancellationToken cancellationToken = default) =>
        Msg91Client.SendOtpAsync(_http, _options.Value, toPhoneE164, code, _logger, cancellationToken);
}
