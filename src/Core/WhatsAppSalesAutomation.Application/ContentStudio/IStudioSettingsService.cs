namespace WhatsAppSalesAutomation.Application.ContentStudio;

public interface IStudioSettingsService
{
    Task<StudioSettingsDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<StudioSettingsDto> UpdateAsync(Guid tenantId, UpdateStudioSettingsRequest request, CancellationToken cancellationToken = default);
}
