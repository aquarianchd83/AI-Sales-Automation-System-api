using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Which WhatsApp provider one tenant goes through. A per-tenant choice made by the Platform Admin wins; otherwise the platform-wide
/// active provider applies, and while that is the QR bridge the pilot list (when non-empty) still limits which tenants use it.
/// Overrides are stored as "tenantId=Provider" entries.
/// </summary>
public static class WhatsAppProviderRouting
{
    public static string Resolve(string? active, IEnumerable<string>? overrides, IEnumerable<Guid>? pilotTenantIds, Guid? tenantId)
    {
        if (tenantId is not { } id)
            return WhatsAppProviders.Meta;

        var forTenant = OverrideFor(overrides, id);
        if (forTenant is not null)
            return forTenant;

        if (!string.Equals(active?.Trim(), WhatsAppProviders.QrBridge, StringComparison.OrdinalIgnoreCase))
            return WhatsAppProviders.Meta;

        var pilots = pilotTenantIds?.ToList() ?? new List<Guid>();
        return pilots.Count == 0 || pilots.Contains(id) ? WhatsAppProviders.QrBridge : WhatsAppProviders.Meta;
    }

    /// <summary>The explicit choice stored for this tenant, or null when it follows the platform default.</summary>
    public static string? OverrideFor(IEnumerable<string>? overrides, Guid tenantId)
    {
        foreach (var entry in overrides ?? Array.Empty<string>())
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var id) && id == tenantId)
                return Normalize(parts[1]);
        }

        return null;
    }

    /// <summary>The list with this tenant's entry replaced (or removed when <paramref name="provider"/> is null).</summary>
    public static List<string> With(IEnumerable<string>? overrides, Guid tenantId, string? provider)
    {
        var kept = (overrides ?? Array.Empty<string>())
            .Where(e => !(e.Split('=', 2, StringSplitOptions.TrimEntries) is { Length: 2 } p && Guid.TryParse(p[0], out var id) && id == tenantId))
            .ToList();
        if (provider is not null)
            kept.Add($"{tenantId}={Normalize(provider)}");
        return kept;
    }

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), WhatsAppProviders.QrBridge, StringComparison.OrdinalIgnoreCase) ? WhatsAppProviders.QrBridge : WhatsAppProviders.Meta;
}
