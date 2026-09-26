using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>The calling tenant's lead discovery profile and the leads discovery has found for it. The
/// discovery run itself is <see cref="ILeadDiscoveryRunService"/>.</summary>
public interface ILeadDiscoveryService
{
    /// <summary>The saved profile, or a disabled one with default values when none has been saved yet.</summary>
    Task<LeadDiscoveryProfileDto> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the profile. Throws PlanLimitExceededException when BatchSize is above
    /// what the tenant's plan allows per run.</summary>
    Task<LeadDiscoveryProfileDto> SaveProfileAsync(SaveLeadDiscoveryProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Newest first. <paramref name="minScore"/> keeps leads scoring at least that much; Search
    /// matches business name, type or city.</summary>
    Task<PagedResult<DiscoveredLeadDto>> GetDiscoveredLeadsAsync(PagedRequest request, int? minScore = null, CancellationToken cancellationToken = default);

    /// <summary>What each run cost and produced, newest first.</summary>
    Task<PagedResult<LeadDiscoveryRunDto>> GetRunsAsync(PagedRequest request, CancellationToken cancellationToken = default);

    /// <summary>This calendar month's and all-time discovery spend for the calling tenant, in USD and in the
    /// tenant's own currency.</summary>
    Task<LeadDiscoverySpendDto> GetSpendAsync(CancellationToken cancellationToken = default);

    /// <summary>Legacy auto-campaign enrollment outcomes (Started/Skipped/Failed), newest first - recorded
    /// before Lead Discovery History replaced them, and no longer written. See ILeadDiscoveryHistoryService.</summary>
    Task<PagedResult<AutoCampaignEnrollmentDto>> GetAutoCampaignEnrollmentsAsync(PagedRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One lead discovery run for one tenant - what the per-tenant lead-discovery job executes.</summary>
public interface ILeadDiscoveryRunService
{
    /// <summary>Resumes any RetryPending executions (automatic retry), then discovers, qualifies and saves up
    /// to the effective batch size of new leads and runs the Auto-Campaign steps - each as a recorded
    /// execution. Returns the one-line summary recorded on the tenant's job schedule. A tenant with no enabled
    /// profile, or no Anthropic API key, is skipped with a summary saying so rather than failing.</summary>
    Task<string> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Resumes one retryable execution as a new execution (manual retry). Returns its summary, or a
    /// "Skipped:" line when the execution is not retryable.</summary>
    Task<string> RetryExecutionAsync(Guid tenantId, Guid executionId, CancellationToken cancellationToken = default);
}
