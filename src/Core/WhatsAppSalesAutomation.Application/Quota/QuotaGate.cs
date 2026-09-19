using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

public class QuotaGate : IQuotaGate
{
    private readonly IQuotaLedgerService _ledger;
    private readonly WhatsAppQuotaWeightOptions _weights;
    private readonly TrialQuotaOptions _trial;

    public QuotaGate(IQuotaLedgerService ledger, IOptions<WhatsAppQuotaWeightOptions> weights, IOptions<TrialQuotaOptions> trial)
    {
        _ledger = ledger;
        _weights = weights.Value;
        _trial = trial.Value;
    }

    public Task GrantTrialAsync(Guid tenantId, DateTime trialEndsAtUtc, CancellationToken cancellationToken = default) =>
        _ledger.GrantTrialQuotaAsync(tenantId, new Dictionary<QuotaType, decimal>
        {
            [QuotaType.WhatsAppMessages] = _trial.WhatsAppMessages,
            [QuotaType.AiConversations] = _trial.AiConversations,
            [QuotaType.LeadCandidates] = _trial.LeadCandidates
        }, trialEndsAtUtc, cancellationToken);

    public async Task<bool> TryConsumeWhatsAppTemplateAsync(Guid tenantId, TemplateCategory category, string operationKey, string referenceId, CancellationToken cancellationToken = default)
    {
        var weight = _weights.For(category);
        var result = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.WhatsAppMessages, weight, operationKey, "Message", referenceId, category.ToString()), cancellationToken);
        return result.Sufficient;
    }

    public async Task<bool> TryConsumeAiConversationAsync(Guid tenantId, string operationKey, string referenceId, CancellationToken cancellationToken = default)
    {
        var result = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.AiConversations, 1m, operationKey, "AiInteraction", referenceId), cancellationToken);
        return result.Sufficient;
    }

    public async Task<decimal> ConsumeLeadCandidatesAsync(Guid tenantId, decimal candidates, string operationKey, string referenceId, string note, CancellationToken cancellationToken = default)
    {
        if (candidates <= 0)
            return 0m;

        var result = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.LeadCandidates, candidates, operationKey, "LeadDiscoveryRun", referenceId, note, AllowPartial: true), cancellationToken);
        return result.Consumed;
    }

    public Task ReleaseAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default) =>
        _ledger.ReverseConsumptionAsync(tenantId, operationKey, cancellationToken);

    public async Task<decimal> GetAvailableAsync(Guid tenantId, QuotaType quotaType, CancellationToken cancellationToken = default) =>
        (await _ledger.GetBalancesAsync(tenantId, cancellationToken)).Single(b => b.QuotaType == quotaType).Balance;
}
