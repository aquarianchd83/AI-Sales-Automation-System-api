using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>
/// One tenant's override of one platform-global <see cref="Application.Settings.AppSettingCatalog"/>
/// key - only for keys marked <c>IsTenantOverridable</c> there (the Campaigns/Media/Messaging/Ai
/// tuning knobs; WhatsApp/AiProviders credentials have their own bespoke per-tenant tables, and
/// MediaStorage/Stripe are platform infra with no per-tenant concept). Composite-keyed by
/// (<see cref="TenantId"/>, <see cref="Key"/>) rather than a single-row-per-tenant shape like
/// <c>TenantWhatsAppConfig</c>, since a tenant may override anywhere from none to all of the
/// overridable keys independently - a missing row for a given key simply means "this tenant uses the
/// platform default for that one setting," not "not configured yet."
///
/// <see cref="Value"/> is always plain text - none of the tenant-overridable keys are secrets (see
/// AppSettingDefinition.IsTenantOverridable's own doc comment), so unlike AppSetting/
/// TenantWhatsAppConfig there is no encryption concern here. Implements <see cref="ITenantOwned"/>
/// purely to piggyback on ApplicationDbContext's reflective per-tenant query filter, same reasoning as
/// TenantWhatsAppConfig's own doc comment - deliberately NOT exposed on IApplicationDbContext, only
/// reachable through <see cref="Application.Common.Interfaces.ITenantConfigOverrideProvider"/>.
/// </summary>
public class TenantAppSettingOverride : ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Same "Category:Property" key format AppSettingCatalog itself uses, e.g.
    /// "Messaging:MaxSendsPerRun" - the other half of the composite primary key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The overriding value, in the same string/comma-separated-list encoding AppSetting uses
    /// for its own IsList keys. Never null on a stored row - a cleared override deletes the row
    /// entirely rather than storing an empty value, so "no row" and "no override" stay the same
    /// thing.</summary>
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
