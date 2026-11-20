namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform Admin Console's AI Provider Models screen: CRUD over the catalogue of models offered by Google, Anthropic, OpenAI and others.</summary>
public interface IAiProviderModelService
{
    /// <summary>Every model, grouped by provider then name. Optional filters; <paramref name="search"/> matches display name or model id.</summary>
    Task<IReadOnlyList<AiProviderModelDto>> GetAllAsync(string? provider, string? search, CancellationToken cancellationToken = default);

    Task<AiProviderModelDto> CreateAsync(SaveAiProviderModelRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<AiProviderModelDto> UpdateAsync(Guid id, SaveAiProviderModelRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}
