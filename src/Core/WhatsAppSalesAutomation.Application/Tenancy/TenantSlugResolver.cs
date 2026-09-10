using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Tenancy;

public class TenantSlugResolver : ITenantSlugResolver
{
    private readonly IApplicationDbContext _context;

    public TenantSlugResolver(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<string> ResolveAsync(string? requestedSlug, string companyName, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requestedSlug))
        {
            var normalized = requestedSlug.Trim().ToLowerInvariant();
            if (await _context.Tenants.AnyAsync(t => t.Slug == normalized, cancellationToken))
                throw new ConflictException($"Workspace URL '{normalized}' is already taken.");

            return normalized;
        }

        var baseSlug = new string(companyName.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());
        while (baseSlug.Contains("--"))
            baseSlug = baseSlug.Replace("--", "-");
        baseSlug = baseSlug.Trim('-');
        if (string.IsNullOrEmpty(baseSlug))
            baseSlug = "workspace";
        if (baseSlug.Length > 55)
            baseSlug = baseSlug[..55].Trim('-');

        var candidate = baseSlug;
        var suffix = 2;
        while (await _context.Tenants.AnyAsync(t => t.Slug == candidate, cancellationToken))
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return candidate;
    }
}
