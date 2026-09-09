namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Raw persistence for the DB-backed AppSettings table - one row per <see
/// cref="Settings.AppSettingCatalog"/> key. Implemented in Infrastructure (EF Core + Data
/// Protection for <see cref="Settings.AppSettingDefinition.IsSecret"/> keys); <see
/// cref="Settings.SettingsService"/> is the business-facing layer that masks secrets and maps to/from
/// DTOs on top of this.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>All stored rows, keyed by <see cref="Settings.AppSettingDefinition.Key"/>. Secret
    /// values come back already decrypted - masking for API responses is SettingsService's job, not
    /// the store's.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates the given rows and reloads the live IConfiguration so every new
    /// request/job scope sees the change immediately - no app restart needed (except for the
    /// Provider/EmbeddingProvider keys, which pick their concrete client type at DI-build time).</summary>
    Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}
