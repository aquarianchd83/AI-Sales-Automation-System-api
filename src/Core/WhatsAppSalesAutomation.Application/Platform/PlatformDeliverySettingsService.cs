using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>How the platform reaches people for sign-in: the web app's address (for links in emails), the SMTP server, and
/// the MSG91 SMS account. Secrets never leave the server - only whether one is stored, plus the last four characters.</summary>
public record PlatformDeliverySettingsDto(
    string PublicUrl,
    string SmtpHost,
    int SmtpPort,
    string SmtpUser,
    string SmtpFrom,
    bool SmtpEnableSsl,
    bool HasSmtpPassword,
    string? SmtpPasswordHint,
    bool IsEmailConfigured,
    bool SmsEnabled,
    string SmsOtpTemplateId,
    string SmsBaseUrl,
    bool HasSmsAuthKey,
    string? SmsAuthKeyHint,
    bool IsSmsConfigured);

/// <param name="SmtpPassword">null keeps the stored password, "" clears it, anything else replaces it.</param>
/// <param name="SmsAuthKey">Same convention as <paramref name="SmtpPassword"/>.</param>
public record UpdatePlatformDeliverySettingsRequest(
    string PublicUrl,
    string SmtpHost,
    int SmtpPort,
    string SmtpUser,
    string SmtpFrom,
    bool SmtpEnableSsl,
    string? SmtpPassword,
    bool SmsEnabled,
    string SmsOtpTemplateId,
    string SmsBaseUrl,
    string? SmsAuthKey);

public record SendDeliveryTestRequest(UpdatePlatformDeliverySettingsRequest Settings, string To);

public record DeliveryTestResultDto(bool Success, string Message);

/// <summary>What an email test needs. The password is already resolved (the form's, or the stored one).</summary>
public record DeliveryEmailSettings(string Host, int Port, string User, string Password, string From, bool EnableSsl);

public record DeliverySmsSettings(string AuthKey, string OtpTemplateId, string BaseUrl);

/// <summary>Sends one real message with the given settings, saved or not, so an admin can check before committing. Implemented in
/// Infrastructure on the same code a real send uses.</summary>
public interface IDeliveryTester
{
    Task<DeliveryTestResultDto> SendEmailAsync(DeliveryEmailSettings settings, string toEmail, CancellationToken cancellationToken = default);

    Task<DeliveryTestResultDto> SendSmsAsync(DeliverySmsSettings settings, string toPhoneE164, CancellationToken cancellationToken = default);
}

public interface IPlatformDeliverySettingsService
{
    Task<PlatformDeliverySettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores the settings in the AppSettings table. Takes effect on the next request - no restart.</summary>
    Task<PlatformDeliverySettingsDto> UpdateAsync(UpdatePlatformDeliverySettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    Task<DeliveryTestResultDto> TestEmailAsync(SendDeliveryTestRequest request, CancellationToken cancellationToken = default);

    Task<DeliveryTestResultDto> TestSmsAsync(SendDeliveryTestRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-admin-owned delivery settings. They live ONLY in the AppSettings table (the SMTP password and the SMS auth key encrypted
/// at rest); appsettings.json carries none of them. The keys are the ones <c>SmtpOptions</c>, <c>Msg91Options</c> and
/// <c>AppLinkOptions</c> bind from, and the senders read them through IOptionsSnapshot, so a save is live on the next request.
/// </summary>
public class PlatformDeliverySettingsService : IPlatformDeliverySettingsService
{
    public const string PublicUrlKey = "App:PublicUrl";
    public const string SmtpHostKey = "Email:Smtp:Host";
    public const string SmtpPortKey = "Email:Smtp:Port";
    public const string SmtpUserKey = "Email:Smtp:User";
    public const string SmtpPasswordKey = "Email:Smtp:Password";
    public const string SmtpFromKey = "Email:Smtp:From";
    public const string SmtpEnableSslKey = "Email:Smtp:EnableSsl";
    public const string SmsEnabledKey = "Sms:Msg91:Enabled";
    public const string SmsAuthKeyKey = "Sms:Msg91:AuthKey";
    public const string SmsTemplateKey = "Sms:Msg91:OtpTemplateId";
    public const string SmsBaseUrlKey = "Sms:Msg91:BaseUrl";

    public const string DefaultSmsBaseUrl = "https://control.msg91.com/api/v5/";
    public const int DefaultSmtpPort = 587;

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdatePlatformDeliverySettingsRequest> _validator;
    private readonly IDeliveryTester _tester;

    public PlatformDeliverySettingsService(IAppSettingsStore store, IValidator<UpdatePlatformDeliverySettingsRequest> validator, IDeliveryTester tester)
    {
        _store = store;
        _validator = validator;
        _tester = tester;
    }

    public async Task<PlatformDeliverySettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await _store.GetAllAsync(cancellationToken));

    public async Task<PlatformDeliverySettingsDto> UpdateAsync(UpdatePlatformDeliverySettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var stored = await _store.GetAllAsync(cancellationToken);
        var smtpPassword = Resolve(request.SmtpPassword, Get(stored, SmtpPasswordKey));
        var smsAuthKey = Resolve(request.SmsAuthKey, Get(stored, SmsAuthKeyKey));

        // Switching SMS on without an account behind it would make every code request fail - refuse it here instead.
        if (request.SmsEnabled && (smsAuthKey.Length == 0 || string.IsNullOrWhiteSpace(request.SmsOtpTemplateId)))
            throw Problem("SmsEnabled", "Enter the MSG91 auth key and OTP template id before turning SMS on.");

        if (string.IsNullOrWhiteSpace(request.SmtpUser) != (smtpPassword.Length == 0))
            throw Problem("SmtpPassword", "The SMTP user and password go together: provide both, or clear both for a server that needs no login.");

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [PublicUrlKey] = request.PublicUrl.Trim().TrimEnd('/'),
            [SmtpHostKey] = request.SmtpHost.Trim(),
            [SmtpPortKey] = request.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SmtpUserKey] = request.SmtpUser.Trim(),
            [SmtpFromKey] = request.SmtpFrom.Trim(),
            [SmtpEnableSslKey] = request.SmtpEnableSsl ? "true" : "false",
            [SmsEnabledKey] = request.SmsEnabled ? "true" : "false",
            [SmsTemplateKey] = request.SmsOtpTemplateId.Trim(),
            [SmsBaseUrlKey] = string.IsNullOrWhiteSpace(request.SmsBaseUrl) ? DefaultSmsBaseUrl : request.SmsBaseUrl.Trim(),
        };

        // Only touch the secrets when the caller said something about them.
        if (request.SmtpPassword is not null)
            values[SmtpPasswordKey] = smtpPassword;
        if (request.SmsAuthKey is not null)
            values[SmsAuthKeyKey] = smsAuthKey;

        await _store.UpsertAsync(values, updatedByUserId, cancellationToken);

        return await GetAsync(cancellationToken);
    }

    public async Task<DeliveryTestResultDto> TestEmailAsync(SendDeliveryTestRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request.Settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.To) || !request.To.Contains('@'))
            throw Problem("To", "Enter the email address to send the test to.");
        if (string.IsNullOrWhiteSpace(request.Settings.SmtpHost) || string.IsNullOrWhiteSpace(request.Settings.SmtpFrom))
            throw Problem("SmtpHost", "Enter an SMTP host and a From address to test.");

        var stored = await _store.GetAllAsync(cancellationToken);
        var password = Resolve(request.Settings.SmtpPassword, Get(stored, SmtpPasswordKey));

        return await _tester.SendEmailAsync(new DeliveryEmailSettings(
            request.Settings.SmtpHost.Trim(), request.Settings.SmtpPort, request.Settings.SmtpUser.Trim(), password,
            request.Settings.SmtpFrom.Trim(), request.Settings.SmtpEnableSsl), request.To.Trim(), cancellationToken);
    }

    public async Task<DeliveryTestResultDto> TestSmsAsync(SendDeliveryTestRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request.Settings, cancellationToken);
        if (!Auth.PhoneNumbers.TryNormalize(request.To, out var phone))
            throw Problem("To", "Enter the phone number with the country code, for example +919876543210.");
        if (string.IsNullOrWhiteSpace(request.Settings.SmsOtpTemplateId))
            throw Problem("SmsOtpTemplateId", "Enter the MSG91 OTP template id to test.");

        var stored = await _store.GetAllAsync(cancellationToken);
        var authKey = Resolve(request.Settings.SmsAuthKey, Get(stored, SmsAuthKeyKey));
        if (authKey.Length == 0)
            throw Problem("SmsAuthKey", "Enter the MSG91 auth key to test.");

        return await _tester.SendSmsAsync(new DeliverySmsSettings(
            authKey, request.Settings.SmsOtpTemplateId.Trim(),
            string.IsNullOrWhiteSpace(request.Settings.SmsBaseUrl) ? DefaultSmsBaseUrl : request.Settings.SmsBaseUrl.Trim()), phone, cancellationToken);
    }

    private static ValidationException Problem(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });

    private static string Resolve(string? requested, string current) => requested is null ? current : requested.Trim();

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static PlatformDeliverySettingsDto ToDto(IReadOnlyDictionary<string, string?> stored)
    {
        var host = Get(stored, SmtpHostKey);
        var from = Get(stored, SmtpFromKey);
        var password = Get(stored, SmtpPasswordKey);
        var authKey = Get(stored, SmsAuthKeyKey);
        var template = Get(stored, SmsTemplateKey);
        var smsEnabled = string.Equals(Get(stored, SmsEnabledKey), "true", StringComparison.OrdinalIgnoreCase);
        var baseUrl = Get(stored, SmsBaseUrlKey);

        return new PlatformDeliverySettingsDto(
            Get(stored, PublicUrlKey),
            host,
            int.TryParse(Get(stored, SmtpPortKey), out var port) && port > 0 ? port : DefaultSmtpPort,
            Get(stored, SmtpUserKey),
            from,
            !string.Equals(Get(stored, SmtpEnableSslKey), "false", StringComparison.OrdinalIgnoreCase),
            password.Length > 0,
            Mask(password),
            host.Length > 0 && from.Length > 0,
            smsEnabled,
            template,
            baseUrl.Length > 0 ? baseUrl : DefaultSmsBaseUrl,
            authKey.Length > 0,
            Mask(authKey),
            smsEnabled && authKey.Length > 0 && template.Length > 0);
    }

    private static string? Mask(string value) =>
        value.Length == 0 ? null : value.Length <= 4 ? "••••" : $"••••{value[^4..]}";
}

public class UpdatePlatformDeliverySettingsRequestValidator : AbstractValidator<UpdatePlatformDeliverySettingsRequest>
{
    public UpdatePlatformDeliverySettingsRequestValidator()
    {
        RuleFor(x => x.PublicUrl).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || IsHttpUrl(v.Trim())))
            .WithMessage("Web app address must be an absolute http(s) address, e.g. https://app.example.com.");

        RuleFor(x => x.SmtpHost).NotNull().MaximumLength(255);
        RuleFor(x => x.SmtpPort).InclusiveBetween(1, 65535);
        RuleFor(x => x.SmtpUser).NotNull().MaximumLength(256);
        RuleFor(x => x.SmtpFrom).NotNull().MaximumLength(256)
            .Must(v => string.IsNullOrWhiteSpace(v) || v.Contains('@')).WithMessage("From must be an email address.");
        RuleFor(x => x.SmtpPassword).MaximumLength(512);

        RuleFor(x => x.SmsOtpTemplateId).NotNull().MaximumLength(100);
        RuleFor(x => x.SmsAuthKey).MaximumLength(256);
        RuleFor(x => x.SmsBaseUrl).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || IsHttpsUrl(v.Trim())))
            .WithMessage("SMS API address must be an https address.");

        // A host with no From address would be accepted and then fail every send with "no SMTP host configured".
        RuleFor(x => x.SmtpFrom).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.SmtpHost))
            .WithMessage("Enter the From address the SMTP server should send as.");
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
