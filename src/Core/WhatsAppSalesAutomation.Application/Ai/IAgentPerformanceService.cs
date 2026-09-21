namespace WhatsAppSalesAutomation.Application.Ai;

public interface IAgentPerformanceService
{
    /// <summary><paramref name="days"/> is the window ending now, clamped by the implementation.</summary>
    Task<AgentPerformanceReportDto> GetReportAsync(int days, CancellationToken cancellationToken = default);
}
