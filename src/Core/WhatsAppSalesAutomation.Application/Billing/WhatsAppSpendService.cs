using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// See <see cref="IWhatsAppSpendService"/> for what this produces and how far to trust it.
///
/// Like PlanLimitsService, every query here filters by an explicit tenant id with IgnoreQueryFilters()
/// rather than leaning on the ambient ITenantContext - the platform usage screen asks for many tenants at
/// once and has no single ambient tenant to lean on.
/// </summary>
public class WhatsAppSpendService : IWhatsAppSpendService
{
    private readonly IApplicationDbContext _context;
    private readonly WhatsAppPricingOptions _pricing;

    public WhatsAppSpendService(IApplicationDbContext context, IOptionsSnapshot<WhatsAppPricingOptions> pricing)
    {
        _context = context;
        _pricing = pricing.Value;
    }

    public async Task<WhatsAppSpend> GetForTenantAsync(Guid tenantId, DateTime fromUtc, DateTime? toUtc = null, CancellationToken cancellationToken = default)
    {
        var byTenant = await GetForTenantsAsync(new[] { tenantId }, fromUtc, toUtc, cancellationToken);
        return byTenant.GetValueOrDefault(tenantId, WhatsAppSpend.Empty);
    }

    public async Task<IReadOnlyDictionary<Guid, WhatsAppSpend>> GetForTenantsAsync(
        IReadOnlyCollection<Guid> tenantIds, DateTime fromUtc, DateTime? toUtc = null, CancellationToken cancellationToken = default)
    {
        if (tenantIds.Count == 0)
            return new Dictionary<Guid, WhatsAppSpend>();

        var ids = tenantIds.ToList();

        // Grouped in the database down to one row per (tenant, billable?, template name), then priced in
        // memory: the category a template name maps to lives in another table, and the rate it maps to lives
        // in configuration, so neither can be part of the SQL.
        var messages = await _context.Messages.IgnoreQueryFilters()
            .Where(m => ids.Contains(m.TenantId) && m.CreatedAt >= fromUtc && (toUtc == null || m.CreatedAt < toUtc.Value))
            .GroupBy(m => new
            {
                m.TenantId,
                // Only a template send that WhatsApp actually accepted is charged for: an inbound message, a
                // free-form session reply, and a queued or failed send all cost nothing.
                Billable = m.Direction == MessageDirection.Outbound
                           && m.MessageType == MessageType.Template
                           && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered || m.Status == MessageStatus.Read),
                m.TemplateName
            })
            .Select(g => new { g.Key.TenantId, g.Key.Billable, g.Key.TemplateName, Count = g.Count() })
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
            return new Dictionary<Guid, WhatsAppSpend>();

        var presentTenantIds = messages.Select(m => m.TenantId).Distinct().ToList();

        // Message.TemplateName is a snapshot of the name as sent, so this can miss a template that has since
        // been renamed or deleted; UnknownCategory covers that.
        var templateCategories = (await _context.MessageTemplates.IgnoreQueryFilters()
                .Where(t => presentTenantIds.Contains(t.TenantId))
                .Select(t => new { t.TenantId, t.Name, t.Category })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.TenantId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                      .ToDictionary(t => t.Key, t => t.First().Category, StringComparer.OrdinalIgnoreCase));

        var countryByTenant = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => presentTenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.CountryCode })
            .ToDictionaryAsync(t => t.Id, t => t.CountryCode, cancellationToken);

        return messages
            .GroupBy(m => m.TenantId)
            .ToDictionary(
                g => g.Key,
                g => Price(
                    g.Select(m => (m.Billable, m.TemplateName, m.Count)),
                    templateCategories.GetValueOrDefault(g.Key) ?? new Dictionary<string, TemplateCategory>(StringComparer.OrdinalIgnoreCase),
                    _pricing.RatesFor(countryByTenant.GetValueOrDefault(g.Key))));
    }

    /// <summary>A template whose name no longer resolves to a template row is priced as Marketing - the
    /// dearest category. An estimate that overstates a cost prompts someone to check it; one that
    /// understates it is trusted and wrong, which is the worse failure for a figure about money.</summary>
    private const TemplateCategory UnknownCategory = TemplateCategory.Marketing;

    private static WhatsAppSpend Price(
        IEnumerable<(bool Billable, string? TemplateName, int Count)> rows,
        IReadOnlyDictionary<string, TemplateCategory> templateCategories,
        WhatsAppCategoryRates rates)
    {
        var messagesSent = 0;
        var billableMessages = 0;
        var byCategory = new Dictionary<TemplateCategory, int>();

        foreach (var (billable, templateName, count) in rows)
        {
            messagesSent += count;
            if (!billable)
                continue;

            billableMessages += count;

            var category = templateName is not null && templateCategories.TryGetValue(templateName, out var known)
                ? known
                : UnknownCategory;
            byCategory[category] = byCategory.GetValueOrDefault(category) + count;
        }

        var categorySpend = byCategory
            .OrderByDescending(entry => entry.Value)
            .Select(entry =>
            {
                var rate = rates.For(entry.Key);
                // Six decimal places, the same precision LeadDiscoveryCost keeps and for the same reason: a
                // handful of utility messages genuinely costs a fraction of a cent.
                return new WhatsAppCategorySpend(
                    entry.Key.ToString(), entry.Value, rate,
                    Math.Round(entry.Value * rate, 6, MidpointRounding.AwayFromZero));
            })
            .ToList();

        return new WhatsAppSpend(
            messagesSent,
            billableMessages,
            messagesSent - billableMessages,
            Math.Round(categorySpend.Sum(c => c.EstimatedCostUsd), 6, MidpointRounding.AwayFromZero),
            categorySpend);
    }
}
