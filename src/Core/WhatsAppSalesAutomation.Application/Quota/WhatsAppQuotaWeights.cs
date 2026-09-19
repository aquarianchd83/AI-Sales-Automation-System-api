using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>
/// How many pooled WhatsApp quota units one template send uses, by category. The pool counts in marketing-message equivalents:
/// a marketing send is one whole unit, and a cheaper category uses the fraction its price is of a marketing send's. So the weights
/// are not a setting of their own - they are the ratios of the per-category message prices on the Configuration page (the
/// "all other countries" row, the platform-wide reference), and move by themselves when those prices do.
/// </summary>
public static class WhatsAppQuotaWeights
{
    public static decimal For(WhatsAppPricingOptions pricing, TemplateCategory category)
    {
        var marketing = pricing.Default.Marketing;

        // No marketing price to compare against: every send counts as one unit rather than dividing by nothing.
        if (marketing <= 0)
            return 1m;

        return Math.Round(pricing.Default.For(category) / marketing, 4, MidpointRounding.AwayFromZero);
    }
}
