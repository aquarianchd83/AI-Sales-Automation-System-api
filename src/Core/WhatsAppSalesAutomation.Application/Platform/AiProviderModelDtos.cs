using FluentValidation;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

public record AiProviderModelDto(
    Guid Id,
    string Provider,
    string DisplayName,
    string ModelId,
    AiModelType ModelType,
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    decimal? InputPricePer1MUsd,
    decimal? OutputPricePer1MUsd,
    string? Description,
    bool IsActive,
    bool IsDefault,
    DateTime CreatedAt);

/// <summary>Create and update take the same fields; <c>IsActive</c> and <c>IsDefault</c> default to on / off for a new model.</summary>
public record SaveAiProviderModelRequest(
    string Provider,
    string DisplayName,
    string ModelId,
    AiModelType ModelType = AiModelType.Chat,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null,
    decimal? InputPricePer1MUsd = null,
    decimal? OutputPricePer1MUsd = null,
    string? Description = null,
    bool IsActive = true,
    bool IsDefault = false);

public class SaveAiProviderModelRequestValidator : AbstractValidator<SaveAiProviderModelRequest>
{
    public SaveAiProviderModelRequestValidator()
    {
        RuleFor(r => r.Provider).NotEmpty().MaximumLength(50);
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ModelId).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ModelType).IsInEnum();
        RuleFor(r => r.ContextWindowTokens).GreaterThan(0).When(r => r.ContextWindowTokens.HasValue);
        RuleFor(r => r.MaxOutputTokens).GreaterThan(0).When(r => r.MaxOutputTokens.HasValue);
        RuleFor(r => r.InputPricePer1MUsd).GreaterThanOrEqualTo(0).When(r => r.InputPricePer1MUsd.HasValue);
        RuleFor(r => r.OutputPricePer1MUsd).GreaterThanOrEqualTo(0).When(r => r.OutputPricePer1MUsd.HasValue);
        RuleFor(r => r.Description).MaximumLength(1000);
    }
}
