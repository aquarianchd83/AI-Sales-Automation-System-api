using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.MetaOnboarding;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// WhatsApp Embedded Signup, completed on the server. The browser only runs Meta's own popup (the tenant signs in to
/// Meta there - we never see a password) and hands back a one-time code and the ids Meta reports. Everything after that
/// happens here, with the platform's Meta App:
///
///  1. exchange the code for the tenant's business token (client_secret never leaves the server);
///  2. re-read the WhatsApp Business Account and number WITH that token - the ids the browser sent are not trusted;
///  3. refuse a number that another tenant on this platform already has connected;
///  4. save the credentials encrypted against the tenant (the existing TenantWhatsAppConfig row);
///  5. register the number when Meta says it is not yet connected, subscribe this app to the account's webhooks,
///     read the templates, and record Meta's confirmation.
///
/// Each step reports its own status. A step that fails never undoes the ones before it - the credentials stay saved, so
/// <see cref="ResumeAsync"/> carries on from where it stopped. Failures are turned into plain-language issues by
/// <see cref="MetaIssueCatalog"/>; the technical detail (HTTP status, Meta code and subcode, Meta's trace id, tenant,
/// step) goes to the log, and tokens, the client secret and the authorization code never do.
///
/// Not exercised against live Meta in this repository - the first real sign-in is the first real test, and it needs the
/// platform's Meta App to have WhatsApp Embedded Signup enabled (see docs/META-WHATSAPP-ONBOARDING.md).
/// </summary>
public class MetaEmbeddedSignupService : IMetaEmbeddedSignupService
{
    private const int MaxLoggedMessage = 300;

    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _context;
    private readonly ITenantWhatsAppConfigProvider _configProvider;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDateTimeProvider _dateTime;
    private readonly IOptionsSnapshot<WhatsAppSettings> _settings;
    private readonly ILogger<MetaEmbeddedSignupService> _logger;

    public MetaEmbeddedSignupService(
        HttpClient httpClient,
        ApplicationDbContext context,
        ITenantWhatsAppConfigProvider configProvider,
        IDataProtectionProvider dataProtectionProvider,
        IDateTimeProvider dateTime,
        IOptionsSnapshot<WhatsAppSettings> settings,
        ILogger<MetaEmbeddedSignupService> logger)
    {
        _httpClient = httpClient;
        _context = context;
        _configProvider = configProvider;
        _dataProtectionProvider = dataProtectionProvider;
        _dateTime = dateTime;
        _settings = settings;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    private WhatsAppSettings Platform => _settings.Value;

    private bool PlatformReady =>
        !string.IsNullOrWhiteSpace(Platform.AppId) && !string.IsNullOrWhiteSpace(Platform.AppSecret)
        && !string.IsNullOrWhiteSpace(Platform.EmbeddedSignupConfigId);

    public Task<MetaSignupClientConfigDto> GetClientConfigAsync(CancellationToken cancellationToken = default)
    {
        var ready = PlatformReady;
        return Task.FromResult(new MetaSignupClientConfigDto(
            ready,
            ready ? Platform.AppId : null,
            ready ? Platform.EmbeddedSignupConfigId : null,
            Platform.ApiVersion,
            ready ? null : MetaIssueCatalog.NotConfigured()));
    }

    public async Task<MetaSignupResultDto> GetStatusAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await LoadRowAsync(tenantId, cancellationToken);
        if (row is null || !row.IsConnected)
            return Result(NotStartedSteps(), null, null, null);

        if (!TryReadToken(row, out var token))
            return Result(StepsFor(MetaSignupSteps.Credentials, MetaSignupStepStatus.ActionRequired, "The saved access could not be read."),
                MetaIssueCatalog.TokenUnreadable(null), TenantWhatsAppConfigProvider.ToDto(row), null);

        return await InspectAsync(row, token!, repair: false, cancellationToken);
    }

    public async Task<MetaSignupResultDto> ResumeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await LoadRowAsync(tenantId, cancellationToken);
        if (row is null || !row.IsConnected)
            return Result(NotStartedSteps(), MetaIssueCatalog.NotConnected(), null, null);

        if (!TryReadToken(row, out var token))
            return Result(StepsFor(MetaSignupSteps.Credentials, MetaSignupStepStatus.ActionRequired, "The saved access could not be read."),
                MetaIssueCatalog.TokenUnreadable(null), TenantWhatsAppConfigProvider.ToDto(row), null);

        return await InspectAsync(row, token!, repair: true, cancellationToken);
    }

    public async Task<MetaSignupResultDto> CompleteAsync(
        Guid tenantId, Guid? userId, CompleteMetaSignupRequest request, CancellationToken cancellationToken = default)
    {
        var steps = NotStartedSteps();

        if (!PlatformReady)
        {
            _logger.LogWarning("Meta onboarding unavailable for tenant {TenantId}: the platform Meta App id, secret or Embedded Signup configuration id is not set.", tenantId);
            return Result(steps, MetaIssueCatalog.NotConfigured(), await CurrentConfigAsync(tenantId, cancellationToken), null);
        }

        // 1. The popup was closed or failed before Meta issued a code. Nothing was changed, nothing is lost.
        if (string.Equals(request.ClientEvent, "CANCEL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.ClientEvent, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            var reference = NewReference();
            _logger.LogWarning(
                "Meta onboarding not completed for tenant {TenantId} at {Step}: popup reported {ClientEvent} at client step {ClientStep}, message {ClientMessage}, reference {Reference}",
                tenantId, MetaSignupSteps.Authorization, request.ClientEvent, Cap(request.ClientStep), Cap(request.ClientErrorMessage), reference);
            return Result(Set(steps, MetaSignupSteps.Authorization, MetaSignupStepStatus.ActionRequired, "Sign-in with Meta was not finished."),
                MetaIssueCatalog.FromClientEvent(request.ClientEvent, request.ClientStep, reference),
                await CurrentConfigAsync(tenantId, cancellationToken), null);
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            var reference = NewReference();
            _logger.LogWarning("Meta onboarding for tenant {TenantId} at {Step}: no authorization code was returned, reference {Reference}", tenantId, MetaSignupSteps.Authorization, reference);
            return Result(Set(steps, MetaSignupSteps.Authorization, MetaSignupStepStatus.ActionRequired, "Meta did not grant access."),
                MetaIssueCatalog.AuthorizationDenied(reference), await CurrentConfigAsync(tenantId, cancellationToken), null);
        }

        // 2. code -> business token
        var exchange = await ExchangeCodeAsync(request.Code.Trim(), cancellationToken);
        if (exchange.Error is not null)
            return Fail(tenantId, MetaSignupSteps.Authorization, steps, exchange.Error, await CurrentConfigAsync(tenantId, cancellationToken));

        var token = exchange.Token!;
        steps = Set(steps, MetaSignupSteps.Authorization, MetaSignupStepStatus.Completed, "Meta access granted.");

        // 3. Which WhatsApp Business Account and number? Read with the new token; the browser's ids are only a hint.
        var assets = await ResolveAssetsAsync(tenantId, token, request.WabaId, request.PhoneNumberId, cancellationToken);
        if (assets.Issue is not null)
        {
            steps = Set(steps, MetaSignupSteps.Assets, MetaSignupStepStatus.ActionRequired, assets.Issue.Title);
            return Result(steps, assets.Issue, await CurrentConfigAsync(tenantId, cancellationToken), null);
        }
        if (assets.Error is not null)
            return Fail(tenantId, MetaSignupSteps.Assets, steps, assets.Error, await CurrentConfigAsync(tenantId, cancellationToken));

        var phone = assets.Phone!;
        var wabaId = assets.WabaId!;

        // 4. One platform-wide owner per number. Said without naming the other tenant.
        var existing = await _configProvider.GetByPhoneNumberIdAsync(phone.Id, cancellationToken);
        if (existing is not null && existing.TenantId != tenantId)
        {
            var reference = NewReference();
            _logger.LogWarning("Meta onboarding for tenant {TenantId} at {Step}: phone number id {PhoneNumberId} is already connected to another tenant, reference {Reference}",
                tenantId, MetaSignupSteps.Assets, phone.Id, reference);
            return Result(Set(steps, MetaSignupSteps.Assets, MetaSignupStepStatus.ActionRequired, "This number is connected to another account."),
                MetaIssueCatalog.FromMetaError(MetaSignupSteps.Assets, new MetaErrorInfo(null, null, null, "phone number already connected to another account", reference)),
                await CurrentConfigAsync(tenantId, cancellationToken), null);
        }

        steps = Set(steps, MetaSignupSteps.Assets, MetaSignupStepStatus.Completed,
            $"{phone.DisplayPhoneNumber ?? phone.Id}{(string.IsNullOrWhiteSpace(phone.VerifiedName) ? string.Empty : " · " + phone.VerifiedName)}");

        // 5. Store the credentials (encrypted by the provider). From here on, nothing below can undo them.
        await _configProvider.SaveConfigForTenantAsync(
            tenantId,
            new UpdateTenantWhatsAppConfigRequest(
                phone.Id, wabaId, token, Platform.AppSecret, Platform.ApiVersion, Platform.ApiBaseUrl, null, Platform.AppId),
            userId,
            cancellationToken);
        steps = Set(steps, MetaSignupSteps.Credentials, MetaSignupStepStatus.Completed, "Saved securely.");

        var row = await LoadRowAsync(tenantId, cancellationToken);
        if (row is null)
            return Result(steps, MetaIssueCatalog.NotConnected(), null, null);

        // 6. The rest, from the stored state - the same path "Resume" takes.
        var rest = await InspectAsync(row, token, repair: true, cancellationToken);
        return rest with { Steps = Merge(steps, rest.Steps) };
    }

    // ---- the post-authorization steps ---------------------------------------------------------------------------------

    /// <summary>
    /// Looks at the connection as Meta has it, and (when <paramref name="repair"/>) fixes what can be fixed:
    /// registers the number if Meta does not show it connected, subscribes the app to the account's webhooks, and records
    /// Meta's confirmation on the saved config. Stops at the first mandatory step Meta refuses, leaving the later ones
    /// not started and the earlier ones as they were.
    /// </summary>
    private async Task<MetaSignupResultDto> InspectAsync(TenantWhatsAppConfig row, string token, bool repair, CancellationToken cancellationToken)
    {
        var tenantId = row.TenantId;
        var steps = NotStartedSteps();
        steps = Set(steps, MetaSignupSteps.Authorization, MetaSignupStepStatus.Completed, "Meta access granted.");
        steps = Set(steps, MetaSignupSteps.Assets, MetaSignupStepStatus.Completed, row.VerifiedDisplayPhoneNumber ?? row.PhoneNumberId);
        steps = Set(steps, MetaSignupSteps.Credentials, MetaSignupStepStatus.Completed, "Saved securely.");

        // The number as Meta shows it now. This is also the "final check": a token Meta rejects fails here.
        var phoneCall = await GetPhoneAsync(row.PhoneNumberId, token, cancellationToken);
        if (phoneCall.Error is not null)
        {
            if (repair)
                await RecordVerificationAsync(row, null, phoneCall.Error, cancellationToken);
            return Fail(tenantId, MetaSignupSteps.Verification, steps, phoneCall.Error, TenantWhatsAppConfigProvider.ToDto(row));
        }
        var phone = phoneCall.Phone!;

        // Registration.
        var connected = string.Equals(phone.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase);
        if (connected)
        {
            steps = Set(steps, MetaSignupSteps.Registration, MetaSignupStepStatus.Completed, "Number is registered.");
        }
        else if (!repair)
        {
            steps = Set(steps, MetaSignupSteps.Registration, MetaSignupStepStatus.ActionRequired, "The number is not registered for messaging yet.");
        }
        else
        {
            var register = await RegisterPhoneAsync(row.PhoneNumberId, token, cancellationToken);
            if (register is not null)
                return Fail(tenantId, MetaSignupSteps.Registration, steps, register, TenantWhatsAppConfigProvider.ToDto(row));
            steps = Set(steps, MetaSignupSteps.Registration, MetaSignupStepStatus.Completed, "Number registered.");
        }

        // Webhook subscription: this platform's app must be subscribed to the tenant's account to receive its events.
        var subscribed = await IsSubscribedAsync(row.WhatsAppBusinessAccountId, token, cancellationToken);
        if (subscribed.Error is not null)
            return Fail(tenantId, MetaSignupSteps.Webhook, steps, subscribed.Error, TenantWhatsAppConfigProvider.ToDto(row));
        if (subscribed.Subscribed)
        {
            steps = Set(steps, MetaSignupSteps.Webhook, MetaSignupStepStatus.Completed, "Message notifications are on.");
        }
        else if (!repair)
        {
            steps = Set(steps, MetaSignupSteps.Webhook, MetaSignupStepStatus.ActionRequired, "Message notifications are not turned on yet.");
        }
        else
        {
            var subscribe = await SubscribeAsync(row.WhatsAppBusinessAccountId, token, cancellationToken);
            if (subscribe is not null)
                return Fail(tenantId, MetaSignupSteps.Webhook, steps, subscribe, TenantWhatsAppConfigProvider.ToDto(row));
            steps = Set(steps, MetaSignupSteps.Webhook, MetaSignupStepStatus.Completed, "Message notifications turned on.");
        }

        // Templates: informational. Having none yet does not stop the connection from being active.
        MetaTemplateSummaryDto? templates = null;
        var templateCall = await GetTemplatesAsync(row.WhatsAppBusinessAccountId, token, cancellationToken);
        if (templateCall.Error is not null)
        {
            LogMetaFailure(tenantId, MetaSignupSteps.Templates, templateCall.Error);
            steps = Set(steps, MetaSignupSteps.Templates, MetaSignupStepStatus.ActionRequired, "Templates could not be read just now.");
        }
        else
        {
            templates = templateCall.Summary;
            steps = Set(steps, MetaSignupSteps.Templates,
                templates!.Total == 0 ? MetaSignupStepStatus.ActionRequired : MetaSignupStepStatus.Completed,
                templates.Total == 0
                    ? "No message templates yet. Create one and submit it to Meta before running a campaign."
                    : $"{templates.Approved} approved, {templates.Pending} awaiting Meta, {templates.Rejected} rejected.");
        }

        // Final check: Meta answered for the number with this token, and it is registered and subscribed.
        if (repair)
            await RecordVerificationAsync(row, phone, null, cancellationToken);
        steps = Set(steps, MetaSignupSteps.Verification, MetaSignupStepStatus.Completed,
            $"{phone.DisplayPhoneNumber ?? row.PhoneNumberId}{(string.IsNullOrWhiteSpace(phone.VerifiedName) ? string.Empty : " · " + phone.VerifiedName)}");

        return Result(steps, null, TenantWhatsAppConfigProvider.ToDto(row), templates);
    }

    private async Task RecordVerificationAsync(TenantWhatsAppConfig row, PhoneInfo? phone, MetaErrorInfo? error, CancellationToken cancellationToken)
    {
        row.VerifiedAtUtc = phone is null ? null : _dateTime.UtcNow;
        row.VerificationError = phone is null ? Cap(MetaIssueCatalog.FromMetaError(MetaSignupSteps.Verification, error!).Message) : null;
        row.VerifiedDisplayPhoneNumber = phone?.DisplayPhoneNumber;
        row.VerifiedName = phone?.VerifiedName;
        await _context.SaveChangesAsync(cancellationToken);
    }

    // ---- asset discovery ------------------------------------------------------------------------------------------------

    private sealed record Assets(string? WabaId, PhoneInfo? Phone, MetaErrorInfo? Error, MetaIssueDto? Issue);

    private async Task<Assets> ResolveAssetsAsync(Guid tenantId, string token, string? hintWabaId, string? hintPhoneId, CancellationToken cancellationToken)
    {
        // The WABA ids the token was actually granted - the source of truth, whatever the browser claimed.
        var granted = await GetGrantedWabaIdsAsync(token, cancellationToken);
        if (granted.Error is not null)
            return new Assets(null, null, granted.Error, null);

        var wabaIds = granted.Ids;
        if (!string.IsNullOrWhiteSpace(hintWabaId) && wabaIds.Count > 0 && !wabaIds.Contains(hintWabaId.Trim()))
        {
            _logger.LogWarning("Meta onboarding for tenant {TenantId}: the WABA id reported by the browser is not among those the token was granted; ignoring the hint.", tenantId);
            hintWabaId = null;
        }
        else if (!string.IsNullOrWhiteSpace(hintWabaId) && wabaIds.Count == 0)
        {
            wabaIds = new List<string> { hintWabaId.Trim() }; // Meta returned no scope list; the phone lookup below still has to confirm it.
        }

        var candidates = !string.IsNullOrWhiteSpace(hintWabaId) ? new List<string> { hintWabaId!.Trim() } : wabaIds;
        if (candidates.Count == 0)
            return new Assets(null, null, null, MetaIssueCatalog.NoAssetsFound(NewReference()));

        var wanted = await WantedNumberDigitsAsync(tenantId, cancellationToken);

        PhoneInfo? first = null;
        string? firstWaba = null;
        foreach (var waba in candidates)
        {
            var list = await ListPhonesAsync(waba, token, cancellationToken);
            if (list.Error is not null)
                return new Assets(null, null, list.Error, null);

            foreach (var p in list.Phones)
            {
                if (!string.IsNullOrWhiteSpace(hintPhoneId) && p.Id == hintPhoneId.Trim())
                    return new Assets(waba, p, null, null);
                if (wanted is not null && Digits(p.DisplayPhoneNumber) == wanted)
                    return new Assets(waba, p, null, null);
                if (first is null)
                {
                    first = p;
                    firstWaba = waba;
                }
            }
        }

        if (first is null)
            return new Assets(null, null, null, MetaIssueCatalog.NoAssetsFound(NewReference()));

        return new Assets(firstWaba, first, null, null);
    }

    /// <summary>The number the tenant typed at the start, digits only - used to pick the matching number when Meta shared several.</summary>
    private async Task<string?> WantedNumberDigitsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var number = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == tenantId).Select(t => t.WhatsAppNumber).FirstOrDefaultAsync(cancellationToken);
        var digits = Digits(number);
        return string.IsNullOrEmpty(digits) ? null : digits;
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    // ---- Meta calls ---------------------------------------------------------------------------------------------------

    private async Task<(string? Token, MetaErrorInfo? Error)> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        // Documented as a GET with the secret in the query; the typed client has its loggers removed (see DependencyInjection).
        var path = "oauth/access_token" +
                   $"?client_id={Uri.EscapeDataString(Platform.AppId)}" +
                   $"&client_secret={Uri.EscapeDataString(Platform.AppSecret)}" +
                   $"&code={Uri.EscapeDataString(code)}";
        var call = await SendAsync(HttpMethod.Get, path, null, null, cancellationToken);
        if (call.Error is not null)
            return (null, call.Error);

        var token = call.Json!.Value.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        return string.IsNullOrWhiteSpace(token)
            ? (null, new MetaErrorInfo(200, 190, null, "no access_token in the exchange response", null))
            : (token, null);
    }

    private async Task<(List<string> Ids, MetaErrorInfo? Error)> GetGrantedWabaIdsAsync(string token, CancellationToken cancellationToken)
    {
        // debug_token takes an app access token ("{app-id}|{app-secret}") and reports which assets the token may use.
        var appToken = $"{Platform.AppId}|{Platform.AppSecret}";
        var path = $"debug_token?input_token={Uri.EscapeDataString(token)}";
        var call = await SendAsync(HttpMethod.Get, path, appToken, null, cancellationToken);
        if (call.Error is not null)
            return (new List<string>(), call.Error);

        var ids = new List<string>();
        if (call.Json!.Value.TryGetProperty("data", out var data) && data.TryGetProperty("granular_scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
        {
            foreach (var scope in scopes.EnumerateArray())
            {
                if (scope.TryGetProperty("scope", out var name) && name.GetString() == "whatsapp_business_management"
                    && scope.TryGetProperty("target_ids", out var targets) && targets.ValueKind == JsonValueKind.Array)
                {
                    ids.AddRange(targets.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))!);
                }
            }
        }
        return (ids.Distinct().ToList(), null);
    }

    private sealed record PhoneInfo(string Id, string? DisplayPhoneNumber, string? VerifiedName, string? Status);

    private const string PhoneFields = "id,display_phone_number,verified_name,status";

    private async Task<(List<PhoneInfo> Phones, MetaErrorInfo? Error)> ListPhonesAsync(string wabaId, string token, CancellationToken cancellationToken)
    {
        var call = await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(wabaId)}/phone_numbers?fields={PhoneFields}&limit=50", token, null, cancellationToken);
        if (call.Error is not null)
            return (new List<PhoneInfo>(), call.Error);

        var phones = new List<PhoneInfo>();
        if (call.Json!.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            phones.AddRange(data.EnumerateArray().Select(ReadPhone).Where(p => p is not null)!);
        return (phones, null);
    }

    private async Task<(PhoneInfo? Phone, MetaErrorInfo? Error)> GetPhoneAsync(string phoneNumberId, string token, CancellationToken cancellationToken)
    {
        var call = await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(phoneNumberId)}?fields={PhoneFields}", token, null, cancellationToken);
        return call.Error is not null ? (null, call.Error) : (ReadPhone(call.Json!.Value), null);
    }

    private static PhoneInfo? ReadPhone(JsonElement e)
    {
        var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return new PhoneInfo(
            id,
            e.TryGetProperty("display_phone_number", out var d) ? d.GetString() : null,
            e.TryGetProperty("verified_name", out var v) ? v.GetString() : null,
            e.TryGetProperty("status", out var s) ? s.GetString() : null);
    }

    /// <summary>Registers the number with the Cloud API. Meta needs a 6-digit two-step-verification PIN for it; one is
    /// generated here and not kept - it can be reset in Meta's WhatsApp Manager if it is ever needed.</summary>
    private async Task<MetaErrorInfo?> RegisterPhoneAsync(string phoneNumberId, string token, CancellationToken cancellationToken)
    {
        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var body = JsonSerializer.Serialize(new { messaging_product = "whatsapp", pin });
        return (await SendAsync(HttpMethod.Post, $"{Uri.EscapeDataString(phoneNumberId)}/register", token, body, cancellationToken)).Error;
    }

    private async Task<(bool Subscribed, MetaErrorInfo? Error)> IsSubscribedAsync(string wabaId, string token, CancellationToken cancellationToken)
    {
        var call = await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(wabaId)}/subscribed_apps", token, null, cancellationToken);
        if (call.Error is not null)
            return (false, call.Error);

        if (call.Json!.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var app in data.EnumerateArray())
            {
                if (app.TryGetProperty("whatsapp_business_api_data", out var api)
                    && api.TryGetProperty("id", out var id) && id.GetString() == Platform.AppId)
                    return (true, null);
            }
        }
        return (false, null);
    }

    private async Task<MetaErrorInfo?> SubscribeAsync(string wabaId, string token, CancellationToken cancellationToken)
        => (await SendAsync(HttpMethod.Post, $"{Uri.EscapeDataString(wabaId)}/subscribed_apps", token, "{}", cancellationToken)).Error;

    private async Task<(MetaTemplateSummaryDto? Summary, MetaErrorInfo? Error)> GetTemplatesAsync(string wabaId, string token, CancellationToken cancellationToken)
    {
        var call = await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(wabaId)}/message_templates?fields=name,status&limit=250", token, null, cancellationToken);
        if (call.Error is not null)
            return (null, call.Error);

        int total = 0, approved = 0, pending = 0, rejected = 0;
        if (call.Json!.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in data.EnumerateArray())
            {
                total++;
                switch ((t.TryGetProperty("status", out var s) ? s.GetString() : null)?.ToUpperInvariant())
                {
                    case "APPROVED": approved++; break;
                    case "REJECTED" or "DISABLED" or "PAUSED": rejected++; break;
                    default: pending++; break;
                }
            }
        }
        return (new MetaTemplateSummaryDto(total, approved, pending, rejected), null);
    }

    private async Task<(JsonElement? Json, MetaErrorInfo? Error)> SendAsync(
        HttpMethod method, string path, string? bearerToken, string? jsonBody, CancellationToken cancellationToken)
    {
        var baseUrl = Platform.ApiBaseUrl.EndsWith('/') ? Platform.ApiBaseUrl : $"{Platform.ApiBaseUrl}/";
        var uri = new Uri(new Uri($"{baseUrl}{Platform.ApiVersion}/"), path);

        using var request = new HttpRequestMessage(method, uri);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (null, ParseError((int)response.StatusCode, body));

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return (doc.RootElement.Clone(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // These exceptions' messages do not include the request URL, so they are safe to log.
            return (null, new MetaErrorInfo(null, null, null, ex.Message, null, Unreachable: true));
        }
        catch (JsonException)
        {
            return (null, new MetaErrorInfo(502, null, null, "Meta returned a response that is not JSON.", null));
        }
    }

    private static MetaErrorInfo ParseError(int httpStatus, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                return new MetaErrorInfo(
                    httpStatus,
                    e.TryGetProperty("code", out var c) && c.TryGetInt32(out var code) ? code : null,
                    e.TryGetProperty("error_subcode", out var sc) && sc.TryGetInt32(out var sub) ? sub : null,
                    e.TryGetProperty("message", out var m) ? m.GetString() : null,
                    e.TryGetProperty("fbtrace_id", out var f) ? f.GetString() : null);
            }
        }
        catch (JsonException)
        {
            // Fall through: not Meta's error envelope.
        }
        return new MetaErrorInfo(httpStatus, null, null, null, null);
    }

    // ---- state and reporting ------------------------------------------------------------------------------------------

    private Task<TenantWhatsAppConfig?> LoadRowAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _context.TenantWhatsAppConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

    private async Task<TenantWhatsAppConfigDto?> CurrentConfigAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _configProvider.GetConfigForTenantAsync(tenantId, cancellationToken);

    private bool TryReadToken(TenantWhatsAppConfig row, out string? token)
    {
        token = null;
        if (row.AccessToken is null)
            return false;
        try
        {
            token = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider).Unprotect(row.AccessToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private MetaSignupResultDto Fail(Guid tenantId, string step, List<MetaSignupStepDto> steps, MetaErrorInfo error, TenantWhatsAppConfigDto? config)
    {
        var reference = error.TraceId ?? NewReference();
        LogMetaFailure(tenantId, step, error with { TraceId = reference });

        var issue = MetaIssueCatalog.FromMetaError(step, error with { TraceId = reference });
        var status = issue.Retryable && issue.PrimaryAction == MetaIssueAction.Retry ? MetaSignupStepStatus.Failed : MetaSignupStepStatus.ActionRequired;
        var overall = status == MetaSignupStepStatus.Failed ? MetaSignupResultDto.Failed : MetaSignupResultDto.ActionRequired;
        return new MetaSignupResultDto(overall, Set(steps, step, status, issue.Title), issue, config, null);
    }

    private void LogMetaFailure(Guid tenantId, string step, MetaErrorInfo error) =>
        _logger.LogWarning(
            "Meta WhatsApp onboarding failed for tenant {TenantId} at {Step}: HTTP {HttpStatus}, Meta code {MetaCode}, subcode {MetaSubcode}, trace {TraceId}, unreachable {Unreachable}, message {MetaMessage}",
            tenantId, step, error.HttpStatus, error.Code, error.Subcode, error.TraceId, error.Unreachable, Cap(error.Message));

    private static string NewReference() => Guid.NewGuid().ToString("N")[..12];

    private static string? Cap(string? value) => value is { Length: > MaxLoggedMessage } ? value[..MaxLoggedMessage] : value;

    private static MetaSignupResultDto Result(List<MetaSignupStepDto> steps, MetaIssueDto? issue, TenantWhatsAppConfigDto? config, MetaTemplateSummaryDto? templates)
    {
        // Active only when every step that messaging depends on is done. Templates inform but do not block.
        var mandatoryDone = steps.Where(s => s.Key != MetaSignupSteps.Templates).All(s => s.Status == MetaSignupStepStatus.Completed);
        var status = issue is null && mandatoryDone
            ? MetaSignupResultDto.Completed
            : issue is { Retryable: true, PrimaryAction: MetaIssueAction.Retry } ? MetaSignupResultDto.Failed : MetaSignupResultDto.ActionRequired;
        return new MetaSignupResultDto(status, steps, issue, config, templates);
    }

    private static List<MetaSignupStepDto> NotStartedSteps() =>
        MetaSignupSteps.All.Select(s => new MetaSignupStepDto(s.Key, s.Title, MetaSignupStepStatus.NotStarted, null)).ToList();

    private static List<MetaSignupStepDto> StepsFor(string key, MetaSignupStepStatus status, string? detail) => Set(NotStartedSteps(), key, status, detail);

    private static List<MetaSignupStepDto> Set(List<MetaSignupStepDto> steps, string key, MetaSignupStepStatus status, string? detail) =>
        steps.Select(s => s.Key == key ? s with { Status = status, Detail = detail } : s).ToList();

    /// <summary>Keeps what the earlier pass learned (e.g. the number's display name) over the later pass's generic detail.</summary>
    private static IReadOnlyList<MetaSignupStepDto> Merge(List<MetaSignupStepDto> earlier, IReadOnlyList<MetaSignupStepDto> later) =>
        later.Select(l => earlier.FirstOrDefault(e => e.Key == l.Key) is { Status: MetaSignupStepStatus.Completed } e
                          && l.Status == MetaSignupStepStatus.Completed
                          && (l.Key is MetaSignupSteps.Authorization or MetaSignupSteps.Assets or MetaSignupSteps.Credentials)
            ? e : l).ToList();
}
