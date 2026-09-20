using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Tenancy;

/// <summary>
/// One customer business account on the platform - the root everything else in the system hangs off
/// via <see cref="ITenantOwned.TenantId"/>. Not itself <see cref="ITenantOwned"/> - it is the tenant,
/// not something owned by one, and carries no query filter.
/// </summary>
public class Tenant : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase, URL-safe, unique. Doubles as the subdomain slug (e.g. "acme" for
    /// acme.saleautomation.com) once the frontend's wildcard DNS routing is wired up - see
    /// <c>TenantsController.GetBySlugAsync</c>.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public TenantStatus Status { get; set; } = TenantStatus.Trial;

    /// <summary>The ApplicationUser who signed this tenant up - set once, at the end of
    /// AuthService.SignUpAsync's transaction, after the first admin user exists.</summary>
    public Guid? OwnerUserId { get; set; }

    public DateTime? TrialEndsAtUtc { get; set; }

    /// <summary>Wired up in the billing phase - null means "no active plan selected yet" (still on trial
    /// or between plans), not an error.</summary>
    public Guid? PlanId { get; set; }

    /// <summary>ISO 3166-1 alpha-2 (e.g. "US", "IN"), picked on the signup form - null for a tenant
    /// who signed up before this existed, or who left it blank. Drives which currency/exchange rate
    /// IBillingService.GetPlansAsync quotes plan prices in (see RegionalPricingCatalog); null or any
    /// unmatched code falls back to USD, never an error.</summary>
    public string? CountryCode { get; set; }

    /// <summary>The tenant's state, where its country's tax splits by state (India: a code from IndianStates). Decides
    /// CGST + SGST versus IGST on what it pays. Null when unknown or not applicable.</summary>
    public string? StateCode { get; set; }

    /// <summary>IANA timezone id (e.g. "America/New_York", "Asia/Kolkata") - one of
    /// TimeZoneCatalog.All, set at signup or changed later by the tenant's own Admin or a
    /// PlatformSuperAdmin. Null defaults to TimeZoneCatalog.DefaultId (India Standard Time) via
    /// ITenantTimeZoneProvider - the same fixed offset this platform always assumed before per-tenant
    /// timezones existed, so an existing tenant's campaign scheduling is unaffected until it
    /// explicitly sets one.</summary>
    public string? Timezone { get; set; }

    // ---- Business profile - edited by the tenant's own Admin on the Business Profile page. Stored for
    // the tenant's own reference; nothing else in the platform reads these yet. <see cref="Name"/> is
    // the company name. ----

    public string? ProductName { get; set; }

    public string? Industry { get; set; }

    public string? BusinessDescription { get; set; }

    public string? WebsiteUrl { get; set; }

    public string? SupportEmail { get; set; }

    public string? SupportPhone { get; set; }

    /// <summary>Where the business operates, as a customer would understand it, e.g. "Mohali, Punjab"
    /// or "Pan-India (online)". Free text rather than derived from <see cref="CountryCode"/>/
    /// <see cref="StateCode"/>: those exist for tax and pricing, and neither answers "where are you
    /// based?" in a form that belongs in a sales reply.</summary>
    public string? BusinessLocation { get; set; }

    /// <summary>Working hours as prose, e.g. "Mon-Sat 10am-7pm IST, Sunday closed". Free text because
    /// nothing computes against it - it is injected into the AI sales agent's business context and
    /// read by a human. Answering "are we open right now?" would need a structured shape instead.</summary>
    public string? WorkingHours { get; set; }

    /// <summary>The outcome this tenant's AI sales agent steers conversations toward. Shapes the
    /// closing move it offers a customer who is ready to act, so a clinic gets "book a demo" and a
    /// builder gets "schedule a site visit".</summary>
    public ConversationGoal AiConversationGoal { get; set; } = ConversationGoal.Enquiry;

    /// <summary>Whether the AI may say anything to a customer about how they are assessed or ranked.
    /// Off by default: a lead score is internal, and a customer told they scored 42 is a customer lost.</summary>
    public bool AiMayDiscloseLeadScore { get; set; }

    /// <summary>Switch, set by a PlatformSuperAdmin per tenant: whether this tenant sees a "request a refund"
    /// option at all. Off by default. The server enforces it - hiding the button is not the control.</summary>
    public bool RefundRequestsEnabled { get; set; }

    /// <summary>Where billing alerts go, set by the tenant's own admin. Null email falls back to the
    /// owner's login email; a null phone means no WhatsApp alerts.</summary>
    public string? BillingAlertEmail { get; set; }

    public string? BillingAlertPhoneE164 { get; set; }

    public bool BillingAlertWhatsAppEnabled { get; set; } = true;

    /// <summary>Words and phrases describing what the business deals in - trimmed and de-duplicated
    /// case-insensitively on save. Persisted as a JSON array in one column (see TenantConfiguration);
    /// never null, empty when none were added.</summary>
    public List<string> DomainKeywords { get; set; } = new();
}
