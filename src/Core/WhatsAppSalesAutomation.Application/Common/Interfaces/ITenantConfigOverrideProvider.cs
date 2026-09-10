namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Merges a tenant's own overrides (see <c>TenantAppSettingOverride</c>, Infrastructure-internal) over
/// the platform-global <c>IOptionsSnapshot&lt;T&gt;</c> value for the Campaigns/Media/Messaging/Ai
/// tuning knobs - the only categories in AppSettingCatalog marked <c>IsTenantOverridable</c>. A tenant
/// with no override for a given key simply uses the platform default; nothing here is a secret, so
/// every value round-trips in full, no Has*/masking convention needed (unlike
/// ITenantWhatsAppConfigProvider/ITenantAiConfigProvider).
///
/// The ambient-tenant Get*OptionsAsync methods are what CampaignService/MediaService/
/// ConversationService/CampaignSendService/ConversationOrchestrator/KnowledgeBaseService resolve
/// instead of injecting IOptionsSnapshot&lt;T&gt; directly now - see each class's own doc comment for
/// why the ambient tenant is always already set correctly by the time they call in. When there's no
/// ambient tenant at all (a PlatformSuperAdmin request has none by design), these fall straight back
/// to the platform default with no DB lookup.
///
/// The cross-tenant methods are PlatformSuperAdmin-only, called from PlatformTenantConfigController -
/// same "explicit tenantId parameter, IgnoreQueryFilters()" shape as
/// ITenantWhatsAppConfigProvider.GetConfigForTenantAsync/SaveConfigForTenantAsync/
/// DeleteConfigForTenantAsync.
/// </summary>
public interface ITenantConfigOverrideProvider
{
    Task<Common.Options.CampaignOptions> GetCampaignOptionsAsync(CancellationToken cancellationToken = default);

    Task<Common.Options.MediaOptions> GetMediaOptionsAsync(CancellationToken cancellationToken = default);

    Task<Common.Options.MessagingOptions> GetMessagingOptionsAsync(CancellationToken cancellationToken = default);

    Task<Common.Options.AiOptions> GetAiOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every tenant-overridable key across all four categories, each showing the platform
    /// default, this tenant's override (if any) and the effective value - what the Platform Admin
    /// Console's tenant detail screen renders.</summary>
    Task<IReadOnlyList<TenantSettingCategoryDto>> GetOverridesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Applies the given overrides on a PlatformSuperAdmin's behalf. A null/blank value for a
    /// key deletes that key's override row (reverts to the platform default); a non-blank value must
    /// parse to that key's expected shape (validated before any row is touched) and is upserted.
    /// Unknown or non-tenant-overridable keys throw NotFoundException, same treatment as
    /// SettingsService.UpdateCategoryAsync. Returns the freshly-merged result, same as
    /// GetOverridesForTenantAsync.</summary>
    Task<IReadOnlyList<TenantSettingCategoryDto>> SaveOverridesForTenantAsync(
        Guid tenantId, UpdateTenantSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every override this tenant has across all four categories, reverting all of
    /// them to the platform default in one action - a no-op (not an error) if the tenant had none.</summary>
    Task DeleteOverridesForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>One tenant-overridable key's platform default, this tenant's override (if any), and the
/// effective value the tenant is actually running with (== OverrideValue when set, else ==
/// GlobalValue). List-typed keys (IsList) use the same comma-separated encoding as the platform-global
/// settings screen.</summary>
public record TenantSettingItemDto(
    string Key,
    string Category,
    bool IsList,
    string? Description,
    string GlobalValue,
    string? OverrideValue,
    string EffectiveValue);

public record TenantSettingCategoryDto(string Category, IReadOnlyList<TenantSettingItemDto> Items);

/// <summary>Body of PUT the tenant config-overrides endpoint. Only the keys present are changed; a
/// present key with a null/blank value clears that override back to the platform default.</summary>
public record UpdateTenantSettingsRequest(Dictionary<string, string?> Values);
