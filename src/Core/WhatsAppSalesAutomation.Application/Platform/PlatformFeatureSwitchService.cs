using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One feature as the platform admin's Features page shows it.</summary>
/// <param name="Published">False when the platform admin has unpublished the feature: no tenant can use it, whatever its plan includes.</param>
/// <param name="UpdatedAtUtc">When the switch was last changed, always UTC; null when it has never been changed.</param>
public record PlatformFeatureSwitchDto(PlatformFeature Feature, string Label, string Description, bool Published, DateTime? UpdatedAtUtc);

public record SetFeaturePublishedRequest(bool Published);

/// <summary>
/// The platform-wide publish switch of each <see cref="PlatformFeature"/>. A feature is published unless a row says otherwise, so a new feature
/// starts live. Stored in the AppSettings table (no schema change) and read back through IConfiguration, which the store reloads on every save, so a
/// change applies from the next request.
/// </summary>
public static class PlatformFeatureSwitches
{
    public const string KeyPrefix = "Platform:FeatureSwitch:";

    public static string KeyOf(PlatformFeature feature) => KeyPrefix + feature;

    public static string UpdatedKeyOf(PlatformFeature feature) => KeyPrefix + feature + ":UpdatedAtUtc";

    /// <summary>The features the platform admin has unpublished.</summary>
    public static IReadOnlySet<PlatformFeature> Unpublished(IConfiguration? configuration)
    {
        var set = new HashSet<PlatformFeature>();
        if (configuration is null)
            return set;
        foreach (var info in PlatformFeatureCatalog.All)
            if (string.Equals(configuration[KeyOf(info.Feature)], "false", StringComparison.OrdinalIgnoreCase))
                set.Add(info.Feature);
        return set;
    }
}

public interface IPlatformFeatureSwitchService
{
    Task<IReadOnlyList<PlatformFeatureSwitchDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PlatformFeatureSwitchDto> SetPublishedAsync(PlatformFeature feature, bool published, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

public class PlatformFeatureSwitchService : IPlatformFeatureSwitchService
{
    private readonly IAppSettingsStore _store;
    private readonly IConfiguration _configuration;
    private readonly IDateTimeProvider _dateTime;

    public PlatformFeatureSwitchService(IAppSettingsStore store, IConfiguration configuration, IDateTimeProvider dateTime)
    {
        _store = store;
        _configuration = configuration;
        _dateTime = dateTime;
    }

    public Task<IReadOnlyList<PlatformFeatureSwitchDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PlatformFeatureSwitchDto>>(PlatformFeatureCatalog.All.Select(Map).ToList());

    public async Task<PlatformFeatureSwitchDto> SetPublishedAsync(PlatformFeature feature, bool published, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        if (!PlatformFeatureCatalog.All.Any(f => f.Feature == feature))
            throw new NotFoundException("Feature", feature);

        // Only unpublished features keep a row: publishing removes it, which is the same as never having been switched.
        var rows = new Dictionary<string, string?>();
        foreach (var info in PlatformFeatureCatalog.All)
        {
            var isOff = info.Feature == feature ? !published : PlatformFeatureSwitches.Unpublished(_configuration).Contains(info.Feature);
            if (!isOff)
                continue;
            rows[PlatformFeatureSwitches.KeyOf(info.Feature)] = "false";
            rows[PlatformFeatureSwitches.UpdatedKeyOf(info.Feature)] = info.Feature == feature
                ? _dateTime.UtcNow.ToString("O")
                : _configuration[PlatformFeatureSwitches.UpdatedKeyOf(info.Feature)];
        }

        await _store.ReplacePrefixesAsync(rows, new[] { PlatformFeatureSwitches.KeyPrefix }, updatedByUserId, cancellationToken);

        // The store has reloaded IConfiguration, but answer from what was just written rather than depend on the reload having reached this scope.
        var info2 = PlatformFeatureCatalog.For(feature);
        return new PlatformFeatureSwitchDto(feature, info2.Label, info2.Description, published, _dateTime.UtcNow);
    }

    private PlatformFeatureSwitchDto Map(PlatformFeatureInfo info)
    {
        var published = !PlatformFeatureSwitches.Unpublished(_configuration).Contains(info.Feature);
        DateTime? at = DateTime.TryParse(_configuration[PlatformFeatureSwitches.UpdatedKeyOf(info.Feature)], null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : null;
        return new PlatformFeatureSwitchDto(info.Feature, info.Label, info.Description, published, at);
    }
}
