using Hangfire;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Enqueues <see cref="StoryClipJob"/>, now or after a delay (the poll interval).</summary>
public sealed class HangfireStoryClipQueue : IStoryClipQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireStoryClipQueue(IBackgroundJobClient client) => _client = client;

    public void Enqueue(Guid jobId, Guid tenantId, TimeSpan? delay = null)
    {
        if (delay is { } wait) _client.Schedule<StoryClipJob>(job => job.RunAsync(jobId, tenantId), wait);
        else _client.Enqueue<StoryClipJob>(job => job.RunAsync(jobId, tenantId));
    }
}

/// <summary>One step of one clip job in the tenant's scope. No automatic retry: a retry could submit, and pay for, a second generation. The job row
/// records its own failures, and the user regenerates on purpose.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class StoryClipJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IStoryClipRunner _runner;

    public StoryClipJob(ITenantContext tenantContext, IStoryClipRunner runner)
    {
        _tenantContext = tenantContext;
        _runner = runner;
    }

    public Task RunAsync(Guid jobId, Guid tenantId)
    {
        _tenantContext.SetTenant(tenantId);
        return _runner.RunAsync(jobId);
    }
}

/// <summary>Enqueues <see cref="StoryRenderJob"/>.</summary>
public sealed class HangfireStoryRenderQueue : IStoryRenderQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireStoryRenderQueue(IBackgroundJobClient client) => _client = client;

    public void Enqueue(Guid renderId, Guid tenantId) => _client.Enqueue<StoryRenderJob>(job => job.RunAsync(renderId, tenantId));
}

/// <summary>Puts one story's clips together in the tenant's scope. No automatic retry: the render row records its own failure and the user starts it again.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class StoryRenderJob
{
    private readonly ITenantContext _tenantContext;
    private readonly IStoryRenderRunner _runner;

    public StoryRenderJob(ITenantContext tenantContext, IStoryRenderRunner runner)
    {
        _tenantContext = tenantContext;
        _runner = runner;
    }

    public Task RunAsync(Guid renderId, Guid tenantId)
    {
        _tenantContext.SetTenant(tenantId);
        return _runner.RunAsync(renderId);
    }
}
