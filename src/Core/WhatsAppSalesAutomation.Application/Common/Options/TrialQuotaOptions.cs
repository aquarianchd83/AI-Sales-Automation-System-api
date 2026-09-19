namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>The free quota a tenant gets for its trial, bound from "Billing:Trial". A trial tenant has no
/// plan and so no included allocation - without this it could not send a single message before paying.</summary>
public class TrialQuotaOptions
{
    public decimal WhatsAppMessages { get; set; } = 50m;

    public decimal AiConversations { get; set; } = 50m;

    public decimal LeadCandidates { get; set; } = 10m;
}
