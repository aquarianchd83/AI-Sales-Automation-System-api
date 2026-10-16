using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>How one outside service is handled: by the built-in simulator (nothing leaves the platform) or by a third-party API.
/// The API key never leaves the server - only whether one is stored, plus its last four characters.</summary>
public record IntegrationChannelDto(
    string Mode,
    string ApiUrl,
    string ApiKeyHeader,
    string SenderId,
    int TimeoutSeconds,
    bool HasApiKey,
    string? ApiKeyHint,
    bool IsLive);

public record PlatformIntegrationSettingsDto(IntegrationChannelDto ConsentSms, IntegrationChannelDto DndCheck);

/// <param name="Mode">"Simulator" or "Api".</param>
/// <param name="ApiKey">null keeps the stored key, "" clears it, anything else replaces it.</param>
/// <param name="SenderId">Only used by the consent SMS channel.</param>
public record IntegrationChannelRequest(
    string Mode,
    string ApiUrl,
    string? ApiKey,
    string ApiKeyHeader,
    string SenderId,
    int TimeoutSeconds);

public record UpdatePlatformIntegrationSettingsRequest(IntegrationChannelRequest ConsentSms, IntegrationChannelRequest DndCheck);

public record SendIntegrationTestRequest(IntegrationChannelRequest Settings, string To);

/// <summary>A channel's settings with the key already resolved (the form's, or the stored one), as the HTTP adapters take them.</summary>
public record IntegrationApiSettings(string ApiUrl, string ApiKey, string ApiKeyHeader, string SenderId, int TimeoutSeconds);

/// <summary>Makes one real call to a third-party API with the given settings, saved or not, so an admin can check before committing.
/// Implemented in Infrastructure on the same code a real send uses.</summary>
public interface IIntegrationTester
{
    Task<DeliveryTestResultDto> SendConsentSmsAsync(IntegrationApiSettings settings, string toPhoneE164, CancellationToken cancellationToken = default);

    Task<DeliveryTestResultDto> CheckDndAsync(IntegrationApiSettings settings, string phoneE164, CancellationToken cancellationToken = default);
}

public interface IPlatformIntegrationSettingsService
{
    Task<PlatformIntegrationSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores the settings in the AppSettings table. Takes effect on the next request - no restart.</summary>
    Task<PlatformIntegrationSettingsDto> UpdateAsync(UpdatePlatformIntegrationSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    Task<DeliveryTestResultDto> TestConsentSmsAsync(SendIntegrationTestRequest request, CancellationToken cancellationToken = default);

    Task<DeliveryTestResultDto> TestDndCheckAsync(SendIntegrationTestRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-admin-owned choice, per outside service (the consent SMS and the DND check), between the built-in simulator and a third-party
/// API. Lives ONLY in the AppSettings table (the API keys encrypted at rest). The routing providers read it through IOptionsSnapshot, so a
/// save is live on the next request. Absent settings mean the simulator, so nothing changes until an admin switches a channel.
/// </summary>
public class PlatformIntegrationSettingsService : IPlatformIntegrationSettingsService
{
    public const string SimulatorMode = "Simulator";
    public const string ApiMode = "Api";
    public const string DefaultApiKeyHeader = "Authorization";
    public const int DefaultTimeoutSeconds = 15;

    public const string ConsentSmsPrefix = "Integrations:ConsentSms";
    public const string DndCheckPrefix = "Integrations:DndCheck";

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdatePlatformIntegrationSettingsRequest> _validator;
    private readonly IIntegrationTester _tester;

    public PlatformIntegrationSettingsService(
        IAppSettingsStore store, IValidator<UpdatePlatformIntegrationSettingsRequest> validator, IIntegrationTester tester)
    {
        _store = store;
        _validator = validator;
        _tester = tester;
    }

    public async Task<PlatformIntegrationSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return new PlatformIntegrationSettingsDto(ToDto(stored, ConsentSmsPrefix), ToDto(stored, DndCheckPrefix));
    }

    public async Task<PlatformIntegrationSettingsDto> UpdateAsync(UpdatePlatformIntegrationSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var stored = await _store.GetAllAsync(cancellationToken);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Collect(values, stored, ConsentSmsPrefix, request.ConsentSms, "ConsentSms");
        Collect(values, stored, DndCheckPrefix, request.DndCheck, "DndCheck");

        await _store.UpsertAsync(values, updatedByUserId, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public Task<DeliveryTestResultDto> TestConsentSmsAsync(SendIntegrationTestRequest request, CancellationToken cancellationToken = default) =>
        TestAsync(request, ConsentSmsPrefix, "ConsentSms", (settings, phone) =>
            _tester.SendConsentSmsAsync(settings, phone, cancellationToken), cancellationToken);

    public Task<DeliveryTestResultDto> TestDndCheckAsync(SendIntegrationTestRequest request, CancellationToken cancellationToken = default) =>
        TestAsync(request, DndCheckPrefix, "DndCheck", (settings, phone) =>
            _tester.CheckDndAsync(settings, phone, cancellationToken), cancellationToken);

    private async Task<DeliveryTestResultDto> TestAsync(
        SendIntegrationTestRequest request, string prefix, string property,
        Func<IntegrationApiSettings, string, Task<DeliveryTestResultDto>> run, CancellationToken cancellationToken)
    {
        if (!Auth.PhoneNumbers.TryNormalize(request.To, out var phone))
            throw Problem("To", "Enter the phone number with the country code, for example +919876543210.");

        if (!string.Equals(request.Settings.Mode, ApiMode, StringComparison.OrdinalIgnoreCase))
            return new DeliveryTestResultDto(true, "The simulator is selected, so nothing is sent anywhere. Switch to Third-party API to test a real call.");

        var channelErrors = ChannelRules.Validate(request.Settings, property);
        if (channelErrors.Count > 0)
            throw new ValidationException(channelErrors);

        var stored = await _store.GetAllAsync(cancellationToken);
        var apiKey = Resolve(request.Settings.ApiKey, Get(stored, prefix + ":ApiKey"));
        if (apiKey.Length == 0)
            throw Problem(property + ".ApiKey", "Enter the API key to test.");

        return await run(ToApiSettings(request.Settings, apiKey), phone);
    }

    private static void Collect(
        Dictionary<string, string?> values, IReadOnlyDictionary<string, string?> stored, string prefix, IntegrationChannelRequest channel, string property)
    {
        var isApi = string.Equals(channel.Mode, ApiMode, StringComparison.OrdinalIgnoreCase);
        var apiKey = Resolve(channel.ApiKey, Get(stored, prefix + ":ApiKey"));

        // Switching to the API without a key behind it would make every call fail - refuse it here instead.
        if (isApi && apiKey.Length == 0)
            throw Problem(property + ".ApiKey", "Enter the API key before switching to the third-party API.");

        values[prefix + ":Mode"] = isApi ? ApiMode : SimulatorMode;
        values[prefix + ":ApiUrl"] = channel.ApiUrl.Trim();
        values[prefix + ":ApiKeyHeader"] = string.IsNullOrWhiteSpace(channel.ApiKeyHeader) ? DefaultApiKeyHeader : channel.ApiKeyHeader.Trim();
        values[prefix + ":SenderId"] = channel.SenderId.Trim();
        values[prefix + ":TimeoutSeconds"] = channel.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Only touch the secret when the caller said something about it.
        if (channel.ApiKey is not null)
            values[prefix + ":ApiKey"] = apiKey;
    }

    internal static IntegrationApiSettings ToApiSettings(IntegrationChannelRequest channel, string apiKey) =>
        new(channel.ApiUrl.Trim(), apiKey,
            string.IsNullOrWhiteSpace(channel.ApiKeyHeader) ? DefaultApiKeyHeader : channel.ApiKeyHeader.Trim(),
            channel.SenderId.Trim(), channel.TimeoutSeconds);

    private static IntegrationChannelDto ToDto(IReadOnlyDictionary<string, string?> stored, string prefix)
    {
        var mode = string.Equals(Get(stored, prefix + ":Mode"), ApiMode, StringComparison.OrdinalIgnoreCase) ? ApiMode : SimulatorMode;
        var url = Get(stored, prefix + ":ApiUrl");
        var key = Get(stored, prefix + ":ApiKey");
        var header = Get(stored, prefix + ":ApiKeyHeader");

        return new IntegrationChannelDto(
            mode,
            url,
            header.Length > 0 ? header : DefaultApiKeyHeader,
            Get(stored, prefix + ":SenderId"),
            int.TryParse(Get(stored, prefix + ":TimeoutSeconds"), out var timeout) && timeout > 0 ? timeout : DefaultTimeoutSeconds,
            key.Length > 0,
            Mask(key),
            mode == ApiMode && url.Length > 0 && key.Length > 0);
    }

    private static ValidationException Problem(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });

    private static string Resolve(string? requested, string current) => requested is null ? current : requested.Trim();

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static string? Mask(string value) =>
        value.Length == 0 ? null : value.Length <= 4 ? "••••" : $"••••{value[^4..]}";
}

internal static class ChannelRules
{
    public static List<FluentValidation.Results.ValidationFailure> Validate(IntegrationChannelRequest channel, string property)
    {
        var errors = new List<FluentValidation.Results.ValidationFailure>();
        void Add(string name, string message) => errors.Add(new FluentValidation.Results.ValidationFailure($"{property}.{name}", message));

        var mode = channel.Mode?.Trim() ?? string.Empty;
        var isApi = string.Equals(mode, PlatformIntegrationSettingsService.ApiMode, StringComparison.OrdinalIgnoreCase);
        if (!isApi && !string.Equals(mode, PlatformIntegrationSettingsService.SimulatorMode, StringComparison.OrdinalIgnoreCase))
            Add("Mode", "Choose Simulator or Third-party API.");

        var url = channel.ApiUrl?.Trim() ?? string.Empty;
        if (url.Length > 500)
            Add("ApiUrl", "The API address is too long.");
        else if (isApi && url.Length == 0)
            Add("ApiUrl", "Enter the third-party API address.");
        else if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            Add("ApiUrl", "The API address must be an https address.");

        var header = channel.ApiKeyHeader?.Trim() ?? string.Empty;
        if (header.Length > 100 || header.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
            Add("ApiKeyHeader", "The key header can only contain letters, digits, dashes and underscores.");

        if ((channel.SenderId?.Length ?? 0) > 50)
            Add("SenderId", "The sender id is too long.");
        if ((channel.ApiKey?.Length ?? 0) > 1024)
            Add("ApiKey", "The API key is too long.");
        if (channel.TimeoutSeconds is < 1 or > 120)
            Add("TimeoutSeconds", "The timeout must be between 1 and 120 seconds.");

        return errors;
    }
}

public class UpdatePlatformIntegrationSettingsRequestValidator : AbstractValidator<UpdatePlatformIntegrationSettingsRequest>
{
    public UpdatePlatformIntegrationSettingsRequestValidator()
    {
        RuleFor(x => x.ConsentSms).NotNull().Custom((channel, context) => Run(channel, "ConsentSms", context));
        RuleFor(x => x.DndCheck).NotNull().Custom((channel, context) => Run(channel, "DndCheck", context));
    }

    private static void Run(IntegrationChannelRequest? channel, string property, ValidationContext<UpdatePlatformIntegrationSettingsRequest> context)
    {
        if (channel is null)
            return;
        foreach (var error in ChannelRules.Validate(channel, property))
            context.AddFailure(error.PropertyName, error.ErrorMessage);
    }
}
