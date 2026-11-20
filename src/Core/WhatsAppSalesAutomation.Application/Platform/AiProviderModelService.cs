using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

public class AiProviderModelService : IAiProviderModelService
{
    private readonly IApplicationDbContext _context;
    private readonly IPlatformAuditService _audit;
    private readonly IValidator<SaveAiProviderModelRequest> _validator;

    public AiProviderModelService(IApplicationDbContext context, IPlatformAuditService audit, IValidator<SaveAiProviderModelRequest> validator)
    {
        _context = context;
        _audit = audit;
        _validator = validator;
    }

    public async Task<IReadOnlyList<AiProviderModelDto>> GetAllAsync(string? provider, string? search, CancellationToken cancellationToken = default)
    {
        var query = _context.AiProviderModels.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(provider))
        {
            var p = provider.Trim();
            query = query.Where(m => m.Provider == p);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(m => m.DisplayName.Contains(s) || m.ModelId.Contains(s));
        }

        var models = await query.OrderBy(m => m.Provider).ThenBy(m => m.DisplayName).ToListAsync(cancellationToken);
        return models.Select(ToDto).ToList();
    }

    public async Task<AiProviderModelDto> CreateAsync(SaveAiProviderModelRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var model = new AiProviderModel();
        Apply(model, request);
        await EnsureUniqueAsync(model, cancellationToken);

        _context.AiProviderModels.Add(model);
        await ClearOtherDefaultsAsync(model, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.AiProviderModelCreated, details: $"{model.Provider}/{model.ModelId}", cancellationToken: cancellationToken);
        return ToDto(model);
    }

    public async Task<AiProviderModelDto> UpdateAsync(Guid id, SaveAiProviderModelRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var model = await _context.AiProviderModels.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(AiProviderModel), id);

        Apply(model, request);
        await EnsureUniqueAsync(model, cancellationToken);
        await ClearOtherDefaultsAsync(model, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.AiProviderModelUpdated, details: $"{model.Provider}/{model.ModelId}", cancellationToken: cancellationToken);
        return ToDto(model);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var model = await _context.AiProviderModels.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(AiProviderModel), id);

        _context.AiProviderModels.Remove(model);
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.AiProviderModelDeleted, details: $"{model.Provider}/{model.ModelId}", cancellationToken: cancellationToken);
    }

    private static void Apply(AiProviderModel model, SaveAiProviderModelRequest r)
    {
        model.Provider = r.Provider.Trim();
        model.DisplayName = r.DisplayName.Trim();
        model.ModelId = r.ModelId.Trim();
        model.ModelType = r.ModelType;
        model.ContextWindowTokens = r.ContextWindowTokens;
        model.MaxOutputTokens = r.MaxOutputTokens;
        model.InputPricePer1MUsd = r.InputPricePer1MUsd;
        model.OutputPricePer1MUsd = r.OutputPricePer1MUsd;
        model.CostTier = r.CostTier;
        model.BestFor = AiModelTasks.Join(r.BestFor);
        model.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        model.IsActive = r.IsActive;
        model.IsDefault = r.IsDefault;
    }

    private async Task EnsureUniqueAsync(AiProviderModel model, CancellationToken cancellationToken)
    {
        var exists = await _context.AiProviderModels.AnyAsync(
            m => m.Id != model.Id && m.Provider == model.Provider && m.ModelId == model.ModelId, cancellationToken);
        if (exists)
            throw new ConflictException($"{model.Provider} already has a model with id '{model.ModelId}'.");
    }

    /// <summary>At most one default per provider and model type: making this one the default unmarks the others.</summary>
    private async Task ClearOtherDefaultsAsync(AiProviderModel model, CancellationToken cancellationToken)
    {
        if (!model.IsDefault)
            return;

        var others = await _context.AiProviderModels
            .Where(m => m.Id != model.Id && m.Provider == model.Provider && m.ModelType == model.ModelType && m.IsDefault)
            .ToListAsync(cancellationToken);
        foreach (var other in others)
            other.IsDefault = false;
    }

    private static AiProviderModelDto ToDto(AiProviderModel m) => new(
        m.Id, m.Provider, m.DisplayName, m.ModelId, m.ModelType, m.ContextWindowTokens, m.MaxOutputTokens,
        m.InputPricePer1MUsd, m.OutputPricePer1MUsd, m.CostTier, AiModelTasks.Split(m.BestFor), m.Description, m.IsActive, m.IsDefault, m.CreatedAt);
}
