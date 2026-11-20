using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// The models "Model by task" offers, read from the platform's own catalogue (Platform Admin > AI Provider Models) rather than asked of the
/// provider's API: the active models of the provider (chat unless a type is asked for). Nothing leaves the platform, it works with no key or while a provider is down, and
/// the admin decides what can be chosen - switch a model off there and it is no longer offered.
/// </summary>
public sealed class DbProviderModelCatalog : IProviderModelCatalog
{
    private readonly IApplicationDbContext _context;

    public DbProviderModelCatalog(IApplicationDbContext context) => _context = context;

    public Task<IReadOnlyList<string>> ListAsync(string provider, CancellationToken cancellationToken = default) =>
        ListAsync(provider, AiModelType.Chat, cancellationToken);

    public async Task<IReadOnlyList<string>> ListAsync(string provider, AiModelType type, CancellationToken cancellationToken = default)
    {
        var name = provider.Trim().ToLower();
        return await _context.AiProviderModels
            .AsNoTracking()
            .Where(m => m.IsActive && m.ModelType == type && m.Provider.ToLower() == name)
            .OrderBy(m => m.DisplayName)
            .Select(m => m.ModelId)
            .ToListAsync(cancellationToken);
    }
}
