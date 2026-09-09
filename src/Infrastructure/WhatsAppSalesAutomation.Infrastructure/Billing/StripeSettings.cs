namespace WhatsAppSalesAutomation.Infrastructure.Billing;

/// <summary>
/// Bound from the "Stripe" config section. Platform-global, not per-tenant, unlike
/// TenantWhatsAppConfig/TenantAiProviderConfig - there is exactly one Stripe account for the whole
/// platform (tenants are Stripe <i>Customers</i> under it, not separate Stripe accounts), the same
/// category of secret as Jwt:Secret rather than something routed through AppSetting/the per-tenant
/// settings mechanism.
/// </summary>
public class StripeSettings
{
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Verifies StripeWebhooksController's incoming events actually came from Stripe - see
    /// Stripe.EventUtility.ConstructEvent, which StripeWebhookHandler calls with this.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
}
