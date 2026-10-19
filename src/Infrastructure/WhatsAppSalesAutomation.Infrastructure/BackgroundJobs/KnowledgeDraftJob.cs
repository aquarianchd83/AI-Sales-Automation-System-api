using Hangfire;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Enqueues <see cref="KnowledgeDraftJob"/>. The tenant id crosses the enqueue boundary explicitly: the job runs in its own scope.</summary>
public sealed class HangfireKnowledgeDraftQueue : IKnowledgeDraftQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireKnowledgeDraftQueue(IBackgroundJobClient client) => _client = client;

    public void Enqueue(Guid runId, Guid tenantId) => _client.Enqueue<KnowledgeDraftJob>(job => job.RunAsync(runId, tenantId));
}

/// <summary>Runs one "draft answers from my sources" run in the tenant's scope. No automatic retry: every AI call costs the platform money, and
/// the run records its own failures and can be started again on purpose.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class KnowledgeDraftJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IKnowledgeDraftRunner _runner;

    public KnowledgeDraftJob(ITenantContext tenantContext, IKnowledgeDraftRunner runner)
    {
        _tenantContext = tenantContext;
        _runner = runner;
    }

    public Task RunAsync(Guid runId, Guid tenantId)
    {
        _tenantContext.SetTenant(tenantId);
        return _runner.RunAsync(runId);
    }
}
