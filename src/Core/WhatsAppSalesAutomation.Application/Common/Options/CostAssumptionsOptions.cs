namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from "Costing", edited on the Platform Admin Console's Configuration page. The providers charge per token and per
/// message, but a plan sells whole conversations, leads and messages - so turning one into the other needs a picture of typical
/// use: how the WhatsApp quota splits between template categories, and how big one AI conversation or one lead candidate is.
/// Set once here, they price every plan the same way; the plan page just reads them.
/// </summary>
public class CostAssumptionsOptions
{
    // How the pooled WhatsApp units are spent, by template category. Relative shares - they need not add to 100.
    public decimal MarketingSharePercent { get; set; } = 60m;

    public decimal UtilitySharePercent { get; set; } = 30m;

    public decimal AuthenticationSharePercent { get; set; } = 10m;

    // One AI conversation, in tokens.
    public int PromptTokensPerConversation { get; set; } = 1500;

    public int CompletionTokensPerConversation { get; set; } = 300;

    // One lead candidate considered by a discovery run (fresh, duplicate or rejected all count).
    public int InputTokensPerCandidate { get; set; } = 6000;

    public int OutputTokensPerCandidate { get; set; } = 800;

    public decimal WebSearchesPerCandidate { get; set; } = 0.5m;
}
