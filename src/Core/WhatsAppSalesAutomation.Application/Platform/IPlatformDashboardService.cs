namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform Admin Console's Platform Dashboard (spec item #1).</summary>
public interface IPlatformDashboardService
{
    Task<PlatformDashboardDto> GetAsync(CancellationToken cancellationToken = default);
}
