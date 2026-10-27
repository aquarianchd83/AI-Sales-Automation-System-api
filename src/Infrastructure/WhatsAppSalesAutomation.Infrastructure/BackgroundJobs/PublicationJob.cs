using Hangfire;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Enqueues the sending of a publication, now or at its time. The tenant id crosses the boundary explicitly: the job runs in its own scope.</summary>
public sealed class HangfirePublicationQueue : IPublicationQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfirePublicationQueue(IBackgroundJobClient client) => _client = client;

    public void Enqueue(Guid publicationId, Guid tenantId, TimeSpan? delay)
    {
        if (delay is { } wait && wait > TimeSpan.Zero)
            _client.Schedule<PublicationJob>(job => job.RunAsync(publicationId, tenantId), wait);
        else
            _client.Enqueue<PublicationJob>(job => job.RunAsync(publicationId, tenantId));
    }
}

/// <summary>
/// Sends one publication in the tenant's scope. No automatic retry from Hangfire itself: the processor decides what is safe to retry (and queues
/// that itself with a growing wait), because re-running blindly after a lost connection could post the same video twice.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class PublicationJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IPublicationProcessor _processor;

    public PublicationJob(ITenantContext tenantContext, IPublicationProcessor processor)
    {
        _tenantContext = tenantContext;
        _processor = processor;
    }

    public Task RunAsync(Guid publicationId, Guid tenantId)
    {
        _tenantContext.SetTenant(tenantId);
        return _processor.RunAsync(publicationId);
    }
}
