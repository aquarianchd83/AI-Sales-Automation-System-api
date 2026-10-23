using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

public class QuotaGate : IQuotaGate
{
    private readonly IQuotaLedgerService _ledger;
    private readonly WhatsAppPricingOptions _pricing;
    private readonly ICreditAllocationService _credits;
    private readonly ICreditWalletService _wallet;

    public QuotaGate(IQuotaLedgerService ledger, IOptionsSnapshot<WhatsAppPricingOptions> pricing, ICreditAllocationService credits, ICreditWalletService wallet)
    {
        _ledger = ledger;
        _pricing = pricing.Value;
        _credits = credits;
        _wallet = wallet;
    }

    public Task AssignTrialPlanAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        _credits.AssignTrialPlanAsync(tenantId, cancellationToken);

    public async Task<bool> TryConsumeWhatsAppTemplateAsync(Guid tenantId, TemplateCategory category, string operationKey, string referenceId, CancellationToken cancellationToken = default)
    {
        var weight = WhatsAppQuotaWeights.For(_pricing, category);
        var result = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.Credits, weight, operationKey, "Message", referenceId, category.ToString()), cancellationToken);
        return result.Sufficient;
    }

    public async Task<bool> TryConsumeAiConversationAsync(Guid tenantId, string operationKey, string referenceId, CancellationToken cancellationToken = default)
    {
        var result = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.Credits, 1m, operationKey, "AiInteraction", referenceId), cancellationToken);
        return result.Sufficient;
    }

    public async Task<decimal> ConsumeLeadCandidatesAsync(Guid tenantId, decimal candidates, string operationKey, string referenceId, string note, CancellationToken cancellationToken = default)
    {
        if (candidates <= 0)
            return 0m;

        var result = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.Credits, candidates, operationKey, "LeadDiscoveryRun", referenceId, note, AllowPartial: true), cancellationToken);
        return result.Consumed;
    }

    public Task ReleaseAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default) =>
        _ledger.ReverseConsumptionAsync(tenantId, operationKey, cancellationToken);

    /// <summary>How many units of the action behind the (older, per-feature) <paramref name="quotaType"/> the tenant could still afford - see
    /// <see cref="ICreditWalletService.GetAffordableUnitsAsync"/>. Asking for <see cref="QuotaType.Credits"/> is the plain balance.</summary>
    public async Task<decimal> GetAvailableAsync(Guid tenantId, QuotaType quotaType, CancellationToken cancellationToken = default)
    {
        CreditScenario? scenario = quotaType switch
        {
            QuotaType.WhatsAppMessages => CreditScenario.WhatsAppTemplateSend,
            QuotaType.AiConversations => CreditScenario.CustomerChatReply,
            QuotaType.LeadCandidates => CreditScenario.LeadDiscovery,
            QuotaType.ContentGenerations => CreditScenario.ContentStudioGeneration,
            QuotaType.VideoRenders => CreditScenario.ContentStudioVideoRender,
            _ => null
        };

        return scenario is { } known
            ? await _wallet.GetAffordableUnitsAsync(tenantId, known, cancellationToken)
            : (await _ledger.GetBalancesAsync(tenantId, cancellationToken)).Single(b => b.QuotaType == QuotaType.Credits).Balance;
    }
}
