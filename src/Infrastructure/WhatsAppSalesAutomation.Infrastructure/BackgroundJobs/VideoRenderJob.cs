using Hangfire;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Enqueues the steps of a video render. The tenant id crosses the boundary explicitly: each job runs in its own scope.</summary>
public sealed class HangfireVideoRenderQueue : IVideoRenderQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireVideoRenderQueue(IBackgroundJobClient client) => _client = client;

    public void EnqueueStart(Guid jobId, Guid tenantId) =>
        _client.Enqueue<VideoRenderJob>(job => job.StartAsync(jobId, tenantId));

    public void SchedulePoll(Guid jobId, Guid tenantId, TimeSpan delay) =>
        _client.Schedule<VideoRenderJob>(job => job.PollAsync(jobId, tenantId), delay);
}

/// <summary>
/// The background steps of making a video, in the tenant's scope. No automatic retry: a failed step ends the job with its reason and returns
/// the charge, and the tenant decides to ask again - re-running blindly could pay the voice and video services twice.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class VideoRenderJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IVideoRenderProcessor _processor;

    public VideoRenderJob(ITenantContext tenantContext, IVideoRenderProcessor processor)
    {
        _tenantContext = tenantContext;
        _processor = processor;
    }

    public Task StartAsync(Guid jobId, Guid tenantId)
    {
        _tenantContext.SetTenant(tenantId);
        return _processor.StartAsync(jobId);
    }

    public Task PollAsync(Guid jobId, Guid tenantId)
    {
        _tenantContext.SetTenant(tenantId);
        return _processor.PollAsync(jobId);
    }
}
