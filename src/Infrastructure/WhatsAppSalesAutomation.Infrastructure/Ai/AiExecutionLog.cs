using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Writes AI execution records through a context of its own, so recording a call never saves - or fails with - whatever the caller has
/// pending on its own context.
/// </summary>
public sealed class AiExecutionLog : IAiExecutionLog
{
    private readonly IServiceScopeFactory _scopes;

    public AiExecutionLog(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task WriteAsync(AiExecution execution, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        db.AiExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);
    }
}
