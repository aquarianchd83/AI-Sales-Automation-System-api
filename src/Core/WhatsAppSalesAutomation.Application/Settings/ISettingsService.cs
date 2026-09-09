using WhatsAppSalesAutomation.Application.Common.Exceptions;

namespace WhatsAppSalesAutomation.Application.Settings;

/// <summary>Business-facing settings API consumed by SettingsController - see IAppSettingsStore for
/// the raw persistence this sits on top of.</summary>
public interface ISettingsService
{
    Task<IReadOnlyList<SettingCategoryDto>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <exception cref="NotFoundException">Unknown category - not one of AppSettingCatalog.Categories.</exception>
    Task<SettingCategoryDto> GetCategoryAsync(string category, CancellationToken cancellationToken = default);

    /// <exception cref="NotFoundException">Unknown category, or a key in the request that doesn't
    /// belong to it.</exception>
    Task UpdateCategoryAsync(string category, UpdateSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}
