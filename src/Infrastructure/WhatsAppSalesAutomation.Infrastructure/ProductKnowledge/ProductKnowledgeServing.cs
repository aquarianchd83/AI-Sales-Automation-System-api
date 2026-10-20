using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;

/// <summary>Reads <c>ProductKnowledge:ServeChat</c>: the tenant's override if the platform administrator set one, else the platform default (false).</summary>
public sealed class ProductKnowledgeServing : IProductKnowledgeServing
{
    public const string Key = "ProductKnowledge:ServeChat";

    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly IOptionsSnapshot<ProductKnowledgeSettings> _options;

    public ProductKnowledgeServing(ApplicationDbContext context, ITenantContext tenant, IOptionsSnapshot<ProductKnowledgeSettings> options)
    {
        _context = context;
        _tenant = tenant;
        _options = options;
    }

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        _tenant.TenantId is { } tenantId ? IsEnabledForTenantAsync(tenantId, cancellationToken) : Task.FromResult(_options.Value.ServeChat);

    public async Task<bool> IsEnabledForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // By explicit tenant id, so the ambient tenant filter is set aside (a background job or a platform request may not have it).
        var value = await _context.TenantAppSettingOverrides.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Key == Key).Select(o => o.Value).FirstOrDefaultAsync(cancellationToken);
        return value is null ? _options.Value.ServeChat : string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
