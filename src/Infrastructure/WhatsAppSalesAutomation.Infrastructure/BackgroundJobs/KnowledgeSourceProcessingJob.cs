using Hangfire;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Enqueues <see cref="KnowledgeSourceProcessingJob"/>. The tenant id crosses the enqueue boundary explicitly: the job runs in its own
/// scope with no request to inherit one from.</summary>
public sealed class HangfireKnowledgeSourceQueue : IKnowledgeSourceQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireKnowledgeSourceQueue(IBackgroundJobClient client) => _client = client;

    public void Enqueue(Guid sourceId, Guid tenantId) =>
        _client.Enqueue<KnowledgeSourceProcessingJob>(job => job.RunAsync(sourceId, tenantId));
}

/// <summary>
/// Reads one product-knowledge source in the tenant's scope. No automatic retry: a failure is recorded on the source with its reason and the
/// tenant chooses to retry - re-reading blindly could re-pay AI calls for scanned pages, or hit a website that asked to be left alone.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class KnowledgeSourceProcessingJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IKnowledgeSourceProcessor _processor;

    public KnowledgeSourceProcessingJob(ITenantContext tenantContext, IKnowledgeSourceProcessor processor)
    {
        _tenantContext = tenantContext;
        _processor = processor;
    }

    public Task RunAsync(Guid sourceId, Guid tenantId)
    {
        // Scope first, before anything queries: the tenant filters and the stamping read it on every query and save.
        _tenantContext.SetTenant(tenantId);
        return _processor.ProcessAsync(sourceId);
    }
}
