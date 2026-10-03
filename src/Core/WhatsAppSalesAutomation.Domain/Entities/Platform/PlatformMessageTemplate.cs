using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A WhatsApp template the PLATFORM sends from its own number to a tenant - plan expiring, credits running out and so on.
/// One row per kind of notice (<see cref="EventKey"/> is a <see cref="TenantNotificationKind"/> name), seeded at startup and
/// then owned by the platform admin: reworded, given an image, switched off. Platform-global, not <see cref="ITenantOwned"/> -
/// it belongs to the operator, never to a tenant, and no tenant request reads it.
///
/// Business-initiated, so it has to be a template Meta has approved; the push/pull rules mirror the tenants' own
/// <c>MessageTemplate</c>, only against the platform's WhatsApp Business Account instead of a tenant's.
/// </summary>
public class PlatformMessageTemplate : BaseEntity
{
    /// <summary>Which notice this template carries: a <see cref="TenantNotificationKind"/> name, e.g. "PlanExpiring7". Unique.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>What the admin calls it, e.g. "Plan expiring in 7 days".</summary>
    public string Name { get; set; } = string.Empty;

    public string Language { get; set; } = "en";

    public TemplateCategory Category { get; set; } = TemplateCategory.Utility;

    /// <summary>The name registered with Meta - what a send puts in the API call.</summary>
    public string WhatsAppTemplateName { get; set; } = string.Empty;

    public WhatsAppTemplateStatus WhatsAppTemplateStatus { get; set; } = WhatsAppTemplateStatus.Pending;

    /// <summary>Body with <c>{{TenantName}}</c> / <c>{{Title}}</c> / <c>{{Message}}</c> tokens (see PlatformTemplateCatalog).</summary>
    public string BodyText { get; set; } = string.Empty;

    /// <summary>The admin's switch: off means this notice goes by email and in-app only. Meta's review status is separate.</summary>
    public bool IsActive { get; set; } = true;

    public string? MetaTemplateId { get; set; }

    public string? LastPushedBodyText { get; set; }

    /// <summary>The optional image above the message, from the platform media library.</summary>
    public Guid? HeaderMediaAssetId { get; set; }

    /// <summary>True once the template was created on Meta WITH an image header; Meta fixes that at creation.</summary>
    public bool HeaderOnMeta { get; set; }
}
