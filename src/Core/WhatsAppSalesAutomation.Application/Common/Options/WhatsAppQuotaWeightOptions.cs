using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// How many quota units one billable WhatsApp template send uses, by template category. The pooled
/// WhatsApp quota is in "marketing-message equivalents": a Marketing send costs Meta the most, so it
/// uses a whole unit, and the cheaper categories use a fraction. Bound from "WhatsApp:QuotaWeights";
/// change the weights, not the plans, when Meta's price ratios move.
/// </summary>
public class WhatsAppQuotaWeightOptions
{
    public decimal Marketing { get; set; } = 1.0m;

    public decimal Authentication { get; set; } = 0.5m;

    public decimal Utility { get; set; } = 0.25m;

    public decimal For(TemplateCategory category) => category switch
    {
        TemplateCategory.Marketing => Marketing,
        TemplateCategory.Authentication => Authentication,
        _ => Utility
    };
}
