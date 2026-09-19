using FluentValidation;
using FluentValidation.Results;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// Which of the countries the platform prices for are switched on. <see cref="RegionalPricingCatalog"/> is the fixed
/// list of countries that have a currency and rate; the operator can turn any of them off on the Configuration page,
/// and every picker in the application then stops offering it. Turning a country off never changes a tenant that is
/// already in it - its pricing keeps resolving; the country just can't be newly chosen.
/// </summary>
public interface ICountryAvailability
{
    /// <summary>Codes switched off (upper-case). Empty when none are.</summary>
    Task<IReadOnlySet<string>> GetDisabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The catalog's countries that are switched on.</summary>
    Task<IReadOnlyList<RegionalPricing>> GetEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Rejects choosing a disabled country. Keeping the country a record already has is always allowed, so
    /// turning a country off never blocks an unrelated edit of a tenant or user that is already in it.</summary>
    /// <exception cref="ValidationException">The new country is switched off.</exception>
    Task EnsureAllowedAsync(string? newCountryCode, string? currentCountryCode, CancellationToken cancellationToken = default);
}

/// <summary>Stored as AppSettings rows <c>Countries:Disabled:{code} = true</c>, read fresh each time so a change is
/// visible to the next request everywhere.</summary>
public class CountryAvailability : ICountryAvailability
{
    public const string DisabledPrefix = "Countries:Disabled:";

    private readonly IAppSettingsStore _store;

    public CountryAvailability(IAppSettingsStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlySet<string>> GetDisabledAsync(CancellationToken cancellationToken = default)
    {
        var all = await _store.GetAllAsync(cancellationToken);
        return all
            .Where(kv => kv.Key.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key[DisabledPrefix.Length..].ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<RegionalPricing>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        var disabled = await GetDisabledAsync(cancellationToken);
        return RegionalPricingCatalog.All.Where(r => !disabled.Contains(r.CountryCode)).ToList();
    }

    public async Task EnsureAllowedAsync(string? newCountryCode, string? currentCountryCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newCountryCode))
            return;

        var code = newCountryCode.Trim();
        if (string.Equals(code, currentCountryCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        if ((await GetDisabledAsync(cancellationToken)).Contains(code))
            throw new ValidationException(new[] { new ValidationFailure("CountryCode", "That country is not available.") });
    }
}
