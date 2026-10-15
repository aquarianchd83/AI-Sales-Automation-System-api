using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>DI-facing implementation of ITenantConfigOverrideProvider - EF Core against the same
/// ApplicationDbContext everything else uses, same pattern as TenantWhatsAppConfigProvider/
/// AppSettingsStore combined: the ambient-tenant reads merge a tenant's TenantAppSettingOverride rows
/// over the platform-global IOptionsSnapshot&lt;T&gt; value; the cross-tenant CRUD methods are the
/// PlatformSuperAdmin-facing half.</summary>
public class TenantConfigOverrideProvider : ITenantConfigOverrideProvider
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<UpdateTenantSettingsRequest> _saveValidator;
    private readonly IOptionsSnapshot<CampaignOptions> _campaignOptions;
    private readonly IOptionsSnapshot<MediaOptions> _mediaOptions;
    private readonly IOptionsSnapshot<MessagingOptions> _messagingOptions;
    private readonly IOptionsSnapshot<AiOptions> _aiOptions;
    private readonly IOptionsSnapshot<StudioOptions> _studioOptions;

    public TenantConfigOverrideProvider(
        ApplicationDbContext context,
        ITenantContext tenantContext,
        IDateTimeProvider dateTime,
        IValidator<UpdateTenantSettingsRequest> saveValidator,
        IOptionsSnapshot<CampaignOptions> campaignOptions,
        IOptionsSnapshot<MediaOptions> mediaOptions,
        IOptionsSnapshot<MessagingOptions> messagingOptions,
        IOptionsSnapshot<AiOptions> aiOptions,
        IOptionsSnapshot<StudioOptions> studioOptions)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dateTime = dateTime;
        _saveValidator = saveValidator;
        _campaignOptions = campaignOptions;
        _mediaOptions = mediaOptions;
        _messagingOptions = messagingOptions;
        _aiOptions = aiOptions;
        _studioOptions = studioOptions;
    }

    public async Task<CampaignOptions> GetCampaignOptionsAsync(CancellationToken cancellationToken = default)
    {
        var global = _campaignOptions.Value;
        var overrides = await LoadAmbientOverridesAsync("Campaigns", cancellationToken);
        if (overrides is null)
            return global;

        return new CampaignOptions
        {
            MinStepMedia = ParseInt(overrides, "Campaigns:MinStepMedia", global.MinStepMedia),
            MaxStepMedia = ParseInt(overrides, "Campaigns:MaxStepMedia", global.MaxStepMedia),
        };
    }

    public async Task<MediaOptions> GetMediaOptionsAsync(CancellationToken cancellationToken = default)
    {
        var global = _mediaOptions.Value;
        var overrides = await LoadAmbientOverridesAsync("Media", cancellationToken);
        if (overrides is null)
            return global;

        return new MediaOptions
        {
            MaxSizeBytes = ParseLong(overrides, "Media:MaxSizeBytes", global.MaxSizeBytes),
            AllowedContentTypes = ParseStringList(overrides, "Media:AllowedContentTypes", global.AllowedContentTypes),
        };
    }

    public async Task<MessagingOptions> GetMessagingOptionsAsync(CancellationToken cancellationToken = default)
    {
        var global = _messagingOptions.Value;
        var overrides = await LoadAmbientOverridesAsync("Messaging", cancellationToken);
        if (overrides is null)
            return global;

        return new MessagingOptions
        {
            MaxSendsPerRun = ParseInt(overrides, "Messaging:MaxSendsPerRun", global.MaxSendsPerRun),
            MaxRetryAttempts = ParseInt(overrides, "Messaging:MaxRetryAttempts", global.MaxRetryAttempts),
            RetryBackoffMinutes = ParseIntList(overrides, "Messaging:RetryBackoffMinutes", global.RetryBackoffMinutes),
            CustomerServiceWindowHours = ParseInt(overrides, "Messaging:CustomerServiceWindowHours", global.CustomerServiceWindowHours),
            DailyTemplateCap = ParseInt(overrides, "Messaging:DailyTemplateCap", global.DailyTemplateCap),
            MaxMarketingPerContactPerWindow = ParseInt(overrides, "Messaging:MaxMarketingPerContactPerWindow", global.MaxMarketingPerContactPerWindow),
            MarketingWindowDays = ParseInt(overrides, "Messaging:MarketingWindowDays", global.MarketingWindowDays),
            CapStepUpAfterGreenDays = ParseInt(overrides, "Messaging:CapStepUpAfterGreenDays", global.CapStepUpAfterGreenDays),
            CapStepUps = ParseIntList(overrides, "Messaging:CapStepUps", global.CapStepUps),
        };
    }

    public async Task<AiOptions> GetAiOptionsAsync(CancellationToken cancellationToken = default)
    {
        var global = _aiOptions.Value;
        var overrides = await LoadAmbientOverridesAsync("Ai", cancellationToken);
        if (overrides is null)
            return global;

        return new AiOptions
        {
            ConfidenceThreshold = ParseDouble(overrides, "Ai:ConfidenceThreshold", global.ConfidenceThreshold),
            EscalationIntents = ParseStringList(overrides, "Ai:EscalationIntents", global.EscalationIntents),
            KnowledgeBaseTopN = ParseInt(overrides, "Ai:KnowledgeBaseTopN", global.KnowledgeBaseTopN),
            MinRelevanceScore = ParseDouble(overrides, "Ai:MinRelevanceScore", global.MinRelevanceScore),
            ConversationHistoryTurns = ParseInt(overrides, "Ai:ConversationHistoryTurns", global.ConversationHistoryTurns),
            MinFieldExtractionConfidence = ParseDouble(overrides, "Ai:MinFieldExtractionConfidence", global.MinFieldExtractionConfidence),
            MaxFieldsToAsk = ParseInt(overrides, "Ai:MaxFieldsToAsk", global.MaxFieldsToAsk),
            HandoffOnHotLead = overrides.TryGetValue("Ai:HandoffOnHotLead", out var handoff) ? string.Equals(handoff, "true", StringComparison.OrdinalIgnoreCase) : global.HandoffOnHotLead,
        };
    }

    public async Task<StudioOptions> GetStudioOptionsForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var global = _studioOptions.Value;
        // Cross-tenant read by explicit id, like GetOverridesForTenantAsync - so IgnoreQueryFilters.
        var rows = await _context.TenantAppSettingOverrides.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Key.StartsWith("ContentStudio:"))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return global;

        var overrides = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        return new StudioOptions
        {
            Enabled = overrides.TryGetValue("ContentStudio:Enabled", out var enabled) ? string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) : global.Enabled,
            StorageMode = overrides.GetValueOrDefault("ContentStudio:StorageMode") ?? global.StorageMode,
            StorageKeyPrefix = overrides.GetValueOrDefault("ContentStudio:StorageKeyPrefix") ?? global.StorageKeyPrefix,
            SignedUrlMinutes = ParseInt(overrides, "ContentStudio:SignedUrlMinutes", global.SignedUrlMinutes),
        };
    }

    public async Task<IReadOnlyList<TenantSettingCategoryDto>> GetOverridesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Cross-tenant by design - the caller is a PlatformSuperAdmin, not guaranteed to have tenantId
        // as the ambient tenant, same IgnoreQueryFilters() reasoning as
        // TenantWhatsAppConfigProvider.GetConfigForTenantAsync.
        var rows = await _context.TenantAppSettingOverrides.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TenantId == tenantId)
            .ToListAsync(cancellationToken);
        var overridesByKey = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        return BuildCategories(overridesByKey);
    }

    public async Task<IReadOnlyList<TenantSettingCategoryDto>> SaveOverridesForTenantAsync(
        Guid tenantId, UpdateTenantSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _saveValidator.ValidateAndThrowAsync(request, cancellationToken);

        var overridableKeys = new HashSet<string>(
            AppSettingCatalog.All.Where(d => d.IsTenantOverridable).Select(d => d.Key),
            StringComparer.OrdinalIgnoreCase);

        var unknownKeys = request.Values.Keys.Where(k => !overridableKeys.Contains(k)).ToList();
        if (unknownKeys.Count > 0)
            throw new NotFoundException($"'{string.Join("', '", unknownKeys)}' is not a tenant-overridable setting.");

        var keys = request.Values.Keys.ToList();
        var existing = (await _context.TenantAppSettingOverrides.IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && keys.Contains(o.Key))
                .ToListAsync(cancellationToken))
            .ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in request.Values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                // Blank clears the override - delete the row entirely so "no row" stays the only
                // "using the platform default" state, never an empty-string row.
                if (existing.TryGetValue(key, out var toRemove))
                    _context.TenantAppSettingOverrides.Remove(toRemove);
                continue;
            }

            if (!existing.TryGetValue(key, out var row))
            {
                row = new TenantAppSettingOverride { TenantId = tenantId, Key = key };
                _context.TenantAppSettingOverrides.Add(row);
            }

            row.Value = rawValue.Trim();
            row.UpdatedAtUtc = _dateTime.UtcNow;
            row.UpdatedByUserId = updatedByUserId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await GetOverridesForTenantAsync(tenantId, cancellationToken);
    }

    public async Task DeleteOverridesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.TenantAppSettingOverrides.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        _context.TenantAppSettingOverrides.RemoveRange(rows);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Null when there's no ambient tenant (a PlatformSuperAdmin request has none by design) -
    /// callers treat that exactly like "no overrides," returning the platform default with no DB round
    /// trip. Otherwise an ordinary tenant-scoped read - the reflective ITenantOwned filter already
    /// restricts this to the ambient tenant, same reasoning as
    /// TenantWhatsAppConfigProvider.LoadForCurrentTenantAsync.</summary>
    private async Task<Dictionary<string, string>?> LoadAmbientOverridesAsync(string category, CancellationToken cancellationToken)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return null;

        var prefix = category + ":";
        var rows = await _context.TenantAppSettingOverrides.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Key.StartsWith(prefix))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<TenantSettingCategoryDto> BuildCategories(IReadOnlyDictionary<string, string> overridesByKey)
    {
        var globalByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Campaigns:MinStepMedia"] = _campaignOptions.Value.MinStepMedia.ToString(CultureInfo.InvariantCulture),
            ["Campaigns:MaxStepMedia"] = _campaignOptions.Value.MaxStepMedia.ToString(CultureInfo.InvariantCulture),
            ["Media:MaxSizeBytes"] = _mediaOptions.Value.MaxSizeBytes.ToString(CultureInfo.InvariantCulture),
            ["Media:AllowedContentTypes"] = string.Join(",", _mediaOptions.Value.AllowedContentTypes),
            ["Messaging:MaxSendsPerRun"] = _messagingOptions.Value.MaxSendsPerRun.ToString(CultureInfo.InvariantCulture),
            ["Messaging:MaxRetryAttempts"] = _messagingOptions.Value.MaxRetryAttempts.ToString(CultureInfo.InvariantCulture),
            ["Messaging:RetryBackoffMinutes"] = string.Join(",", _messagingOptions.Value.RetryBackoffMinutes),
            ["Messaging:CustomerServiceWindowHours"] = _messagingOptions.Value.CustomerServiceWindowHours.ToString(CultureInfo.InvariantCulture),
            ["Messaging:DailyTemplateCap"] = _messagingOptions.Value.DailyTemplateCap.ToString(CultureInfo.InvariantCulture),
            ["Messaging:MaxMarketingPerContactPerWindow"] = _messagingOptions.Value.MaxMarketingPerContactPerWindow.ToString(CultureInfo.InvariantCulture),
            ["Messaging:MarketingWindowDays"] = _messagingOptions.Value.MarketingWindowDays.ToString(CultureInfo.InvariantCulture),
            ["Messaging:CapStepUpAfterGreenDays"] = _messagingOptions.Value.CapStepUpAfterGreenDays.ToString(CultureInfo.InvariantCulture),
            ["Messaging:CapStepUps"] = string.Join(",", _messagingOptions.Value.CapStepUps),
            ["Ai:ConfidenceThreshold"] = _aiOptions.Value.ConfidenceThreshold.ToString(CultureInfo.InvariantCulture),
            ["Ai:EscalationIntents"] = string.Join(",", _aiOptions.Value.EscalationIntents),
            ["Ai:KnowledgeBaseTopN"] = _aiOptions.Value.KnowledgeBaseTopN.ToString(CultureInfo.InvariantCulture),
            ["Ai:MinRelevanceScore"] = _aiOptions.Value.MinRelevanceScore.ToString(CultureInfo.InvariantCulture),
            ["Ai:ConversationHistoryTurns"] = _aiOptions.Value.ConversationHistoryTurns.ToString(CultureInfo.InvariantCulture),
            ["Ai:MinFieldExtractionConfidence"] = _aiOptions.Value.MinFieldExtractionConfidence.ToString(CultureInfo.InvariantCulture),
            ["Ai:MaxFieldsToAsk"] = _aiOptions.Value.MaxFieldsToAsk.ToString(CultureInfo.InvariantCulture),
            ["Ai:HandoffOnHotLead"] = _aiOptions.Value.HandoffOnHotLead ? "true" : "false",
            ["ContentStudio:Enabled"] = _studioOptions.Value.Enabled ? "true" : "false",
            ["ContentStudio:StorageMode"] = _studioOptions.Value.StorageMode,
            ["ContentStudio:StorageKeyPrefix"] = _studioOptions.Value.StorageKeyPrefix,
            ["ContentStudio:SignedUrlMinutes"] = _studioOptions.Value.SignedUrlMinutes.ToString(CultureInfo.InvariantCulture),
        };

        return AppSettingCatalog.All
            .Where(d => d.IsTenantOverridable)
            .GroupBy(d => d.Category)
            .Select(group => new TenantSettingCategoryDto(
                group.Key,
                group.Select(d =>
                {
                    var globalValue = globalByKey[d.Key];
                    var overrideValue = overridesByKey.TryGetValue(d.Key, out var v) ? v : null;
                    return new TenantSettingItemDto(d.Key, d.Category, d.IsList, d.Description, globalValue, overrideValue, overrideValue ?? globalValue, d.Options);
                }).ToList()))
            .ToList();
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> overrides, string key, int fallback) =>
        overrides.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static long ParseLong(IReadOnlyDictionary<string, string> overrides, string key, long fallback) =>
        overrides.TryGetValue(key, out var raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double ParseDouble(IReadOnlyDictionary<string, string> overrides, string key, double fallback) =>
        overrides.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string[] ParseStringList(IReadOnlyDictionary<string, string> overrides, string key, string[] fallback) =>
        overrides.TryGetValue(key, out var raw)
            ? raw.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToArray()
            : fallback;

    private static int[] ParseIntList(IReadOnlyDictionary<string, string> overrides, string key, int[] fallback)
    {
        if (!overrides.TryGetValue(key, out var raw))
            return fallback;

        var parsed = raw.Split(',')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        // Validated before this is ever stored (UpdateTenantSettingsRequestValidator), but a
        // defensively-empty parse (e.g. a manually edited DB row) falls back rather than handing
        // callers a zero-length backoff schedule.
        return parsed.Length > 0 ? parsed : fallback;
    }
}
