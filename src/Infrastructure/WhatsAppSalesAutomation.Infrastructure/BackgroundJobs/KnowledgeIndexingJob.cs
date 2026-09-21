using Hangfire;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Enqueues <see cref="KnowledgeIndexingJob"/>. The tenant id has to cross the enqueue
/// boundary as an explicit argument: the job runs in its own DI scope with no request to inherit one
/// from.</summary>
public sealed class HangfireKnowledgeIngestionQueue : IKnowledgeIngestionQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireKnowledgeIngestionQueue(IBackgroundJobClient client)
    {
        _client = client;
    }

    public void Enqueue(Guid jobId, Guid? tenantId, bool securityReviewed) =>
        _client.Enqueue<KnowledgeIndexingJob>(job => job.RunAsync(jobId, tenantId, securityReviewed));
}

/// <summary>
/// Runs one ingestion job in the right scope: the article's tenant, or - for a GLOBAL article, which
/// has none - the platform, entered explicitly.
///
/// No [DisableConcurrentExecution]: different articles are independent, and per-article idempotency is
/// already the job row's unique (ArticleId, ArticleVersionNumber) index. No Hangfire automatic retry
/// either - the pipeline records its own failures as job state and is resumed on purpose, and a
/// blind retry would re-pay embedding costs for a failure a human may need to look at first.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class KnowledgeIndexingJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IKnowledgeIngestionService _ingestion;

    public KnowledgeIndexingJob(ITenantContext tenantContext, IKnowledgeIngestionService ingestion)
    {
        _tenantContext = tenantContext;
        _ingestion = ingestion;
    }

    public Task RunAsync(Guid jobId, Guid? tenantId, bool securityReviewed)
    {
        // Scope first, before anything queries: the ambient filters and the stamping interceptor read
        // it lazily on every query and save.
        if (tenantId is { } tenant)
            _tenantContext.SetTenant(tenant);
        else
            _tenantContext.EnterPlatformScope();

        return _ingestion.RunAsync(jobId, securityReviewed);
    }
}
