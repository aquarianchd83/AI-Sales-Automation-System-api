using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Analytics;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Daily: asks the platform how every recently published video is doing, tenant by tenant. Each tenant runs in its own scope with its own tenant
/// context, so nothing it reads or writes can cross into another tenant; one tenant failing never stops the next. Never throws.
/// </summary>
public class PublicationMetricsJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PublicationMetricsJob> _logger;

    public PublicationMetricsJob(IServiceScopeFactory scopes, ILogger<PublicationMetricsJob> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        IReadOnlyList<Guid> tenants;
        try
        {
            using var scope = _scopes.CreateScope();
            tenants = await scope.ServiceProvider.GetRequiredService<IVideoAnalyticsService>().TenantsToSyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video metrics sync could not list the tenants to look at");
            return;
        }

        int updated = 0, errors = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IVideoAnalyticsService>().SyncTenantAsync(tenantId);
                updated += result.Updated;
                errors += result.Errors;
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogError(ex, "Video metrics sync failed for tenant {TenantId}", tenantId);
            }
        }

        _logger.LogInformation("Video metrics sync: {Tenants} tenant(s), {Updated} video(s) updated, {Errors} problem(s)", tenants.Count, updated, errors);
    }
}

/// <summary>
/// Daily: the studio's housekeeping for every tenant that has studio work or files - ends renders and publications that stopped moving, deletes video
/// files past their retention, removes sign-ins that needed reconnecting for too long. Each tenant in its own scope; never throws.
/// </summary>
public class StudioMaintenanceJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StudioMaintenanceJob> _logger;

    public StudioMaintenanceJob(IServiceScopeFactory scopes, ILogger<StudioMaintenanceJob> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        IReadOnlyList<Guid> tenants;
        try
        {
            using var scope = _scopes.CreateScope();
            tenants = await scope.ServiceProvider.GetRequiredService<IStudioMaintenanceService>().TenantsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Studio maintenance could not list the tenants to look after");
            return;
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                total += (await scope.ServiceProvider.GetRequiredService<IStudioMaintenanceService>().RunForTenantAsync(tenantId)).Total;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Studio maintenance failed for tenant {TenantId}", tenantId);
            }
        }

        if (total > 0)
            _logger.LogInformation("Studio maintenance: {Total} item(s) looked after across {Tenants} tenant(s)", total, tenants.Count);
    }
}
