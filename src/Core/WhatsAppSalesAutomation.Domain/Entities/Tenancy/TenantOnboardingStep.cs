using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Tenancy;

/// <summary>
/// One onboarding step a tenant has completed. A row exists while the step is met and is removed when it stops being
/// (delete the only customer package and its row goes), so the table always says what is complete now, and since
/// when. Which steps exist, their order and weights live in code (Application.Onboarding.OnboardingCatalog).
/// </summary>
public class TenantOnboardingStep : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The catalog key, e.g. "profile" or "whatsapp".</summary>
    public string StepKey { get; set; } = string.Empty;

    public DateTime CompletedAt { get; set; }

    /// <summary>The user whose visit recorded the completion. Null when it was credited from data that already
    /// existed, with no user in scope.</summary>
    public Guid? CompletedBy { get; set; }
}
