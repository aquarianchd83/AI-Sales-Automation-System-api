using System.Text.RegularExpressions;
using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The WhatsApp number the PLATFORM sends notices from - plan expiring, credits running out. Not a tenant's number. The access token
/// never leaves the server: only whether one is stored, plus its last four characters.</summary>
public record PlatformWhatsAppSettingsDto(
    bool Enabled,
    string PhoneNumberId,
    string WhatsAppBusinessAccountId,
    bool HasAccessToken,
    string? AccessTokenHint,
    string ApiVersion,
    string ApiBaseUrl,
    bool IsConfigured,
    bool CanManageTemplates);

/// <param name="AccessToken">null keeps the stored token, "" clears it, anything else replaces it.</param>
public record UpdatePlatformWhatsAppSettingsRequest(
    bool Enabled,
    string PhoneNumberId,
    string WhatsAppBusinessAccountId,
    string? AccessToken,
    string ApiVersion,
    string ApiBaseUrl);

public record SendPlatformWhatsAppTestRequest(string To);

public interface IPlatformWhatsAppSettingsService
{
    Task<PlatformWhatsAppSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores the settings in the AppSettings table. Takes effect on the next request - no restart.</summary>
    Task<PlatformWhatsAppSettingsDto> UpdateAsync(UpdatePlatformWhatsAppSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Sends Meta's own sample template ("hello_world") from the SAVED number, to prove the credentials work end to end.</summary>
    Task<DeliveryTestResultDto> SendTestAsync(string toPhone, CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-admin-owned WhatsApp number settings. They live ONLY in the AppSettings table (the access token encrypted at rest); the keys are the ones
/// the platform sender binds from "PlatformWhatsApp", read through IOptionsSnapshot, so a save is live on the next request.
/// </summary>
public class PlatformWhatsAppSettingsService : IPlatformWhatsAppSettingsService
{
    public const string EnabledKey = "PlatformWhatsApp:Enabled";
    public const string PhoneNumberIdKey = "PlatformWhatsApp:PhoneNumberId";
    public const string BusinessAccountIdKey = "PlatformWhatsApp:WhatsAppBusinessAccountId";
    public const string AccessTokenKey = "PlatformWhatsApp:AccessToken";
    public const string ApiVersionKey = "PlatformWhatsApp:ApiVersion";
    public const string ApiBaseUrlKey = "PlatformWhatsApp:ApiBaseUrl";

    public const string DefaultApiVersion = "v19.0";
    public const string DefaultApiBaseUrl = "https://graph.facebook.com/";

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdatePlatformWhatsAppSettingsRequest> _validator;
    private readonly IPlatformWhatsAppSender _sender;

    public PlatformWhatsAppSettingsService(IAppSettingsStore store, IValidator<UpdatePlatformWhatsAppSettingsRequest> validator, IPlatformWhatsAppSender sender)
    {
        _store = store;
        _validator = validator;
        _sender = sender;
    }

    public async Task<PlatformWhatsAppSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await _store.GetAllAsync(cancellationToken));

    public async Task<PlatformWhatsAppSettingsDto> UpdateAsync(UpdatePlatformWhatsAppSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var stored = await _store.GetAllAsync(cancellationToken);
        var token = request.AccessToken is null ? Get(stored, AccessTokenKey) : request.AccessToken.Trim();

        // Switching the number on without credentials would make every notice fail quietly - refuse it here instead.
        if (request.Enabled && (request.PhoneNumberId.Trim().Length == 0 || token.Length == 0))
            throw new ValidationException(new[]
            {
                new FluentValidation.Results.ValidationFailure(nameof(request.Enabled), "Enter the phone number id and the access token before turning the platform WhatsApp number on.")
            });

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [EnabledKey] = request.Enabled ? "true" : "false",
            [PhoneNumberIdKey] = request.PhoneNumberId.Trim(),
            [BusinessAccountIdKey] = request.WhatsAppBusinessAccountId.Trim(),
            [ApiVersionKey] = string.IsNullOrWhiteSpace(request.ApiVersion) ? DefaultApiVersion : request.ApiVersion.Trim(),
            [ApiBaseUrlKey] = string.IsNullOrWhiteSpace(request.ApiBaseUrl) ? DefaultApiBaseUrl : request.ApiBaseUrl.Trim(),
        };

        // Only touch the secret when the caller said something about it.
        if (request.AccessToken is not null)
            values[AccessTokenKey] = token;

        await _store.UpsertAsync(values, updatedByUserId, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<DeliveryTestResultDto> SendTestAsync(string toPhone, CancellationToken cancellationToken = default)
    {
        if (!Auth.PhoneNumbers.TryNormalize(toPhone, out var phone))
            throw new ValidationException(new[]
            {
                new FluentValidation.Results.ValidationFailure("To", "Enter the WhatsApp number with the country code, for example +919876543210.")
            });

        var sent = await _sender.SendTemplateAsync(
            phone, PlatformTemplateCatalog.ConnectionTestTemplate, PlatformTemplateCatalog.ConnectionTestLanguage, Array.Empty<string>(), cancellationToken);

        if (sent.Success)
            return new DeliveryTestResultDto(true, $"Sent Meta's sample message to {phone}.");

        return new DeliveryTestResultDto(false, sent.Skipped
            ? "The platform WhatsApp number isn't set up yet (or is switched off). Save the phone number id and access token first."
            : sent.Note ?? "WhatsApp did not accept the message.");
    }

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static PlatformWhatsAppSettingsDto ToDto(IReadOnlyDictionary<string, string?> stored)
    {
        var phoneId = Get(stored, PhoneNumberIdKey);
        var waba = Get(stored, BusinessAccountIdKey);
        var token = Get(stored, AccessTokenKey);
        // Absent means on: the switch only exists to pause a configured number.
        var enabled = !string.Equals(Get(stored, EnabledKey), "false", StringComparison.OrdinalIgnoreCase);
        var version = Get(stored, ApiVersionKey);
        var baseUrl = Get(stored, ApiBaseUrlKey);
        var configured = enabled && phoneId.Length > 0 && token.Length > 0;

        return new PlatformWhatsAppSettingsDto(
            enabled, phoneId, waba, token.Length > 0, Mask(token),
            version.Length > 0 ? version : DefaultApiVersion,
            baseUrl.Length > 0 ? baseUrl : DefaultApiBaseUrl,
            configured, configured && waba.Length > 0);
    }

    private static string? Mask(string value) =>
        value.Length == 0 ? null : value.Length <= 4 ? "••••" : $"••••{value[^4..]}";
}

public class UpdatePlatformWhatsAppSettingsRequestValidator : AbstractValidator<UpdatePlatformWhatsAppSettingsRequest>
{
    private static readonly Regex Digits = new(@"^\d{5,30}$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^v\d{1,2}\.\d{1,2}$", RegexOptions.Compiled);

    public UpdatePlatformWhatsAppSettingsRequestValidator()
    {
        RuleFor(x => x.PhoneNumberId).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || Digits.IsMatch(v.Trim())))
            .WithMessage("The phone number id is the numeric id Meta shows next to the number (not the phone number itself).");

        RuleFor(x => x.WhatsAppBusinessAccountId).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || Digits.IsMatch(v.Trim())))
            .WithMessage("The WhatsApp Business Account id is the numeric id from Meta's WhatsApp Manager.");

        RuleFor(x => x.AccessToken).MaximumLength(1000);

        RuleFor(x => x.ApiVersion).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || Version.IsMatch(v.Trim())))
            .WithMessage("API version looks like v19.0.");

        RuleFor(x => x.ApiBaseUrl).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || IsHttps(v.Trim())))
            .WithMessage("API address must be an https address.");
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
