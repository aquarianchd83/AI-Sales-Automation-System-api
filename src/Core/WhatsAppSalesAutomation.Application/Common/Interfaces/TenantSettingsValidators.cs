using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

public class UpdateTenantWhatsAppConfigRequestValidator : AbstractValidator<UpdateTenantWhatsAppConfigRequest>
{
    public UpdateTenantWhatsAppConfigRequestValidator()
    {
        RuleFor(x => x.PhoneNumberId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.WhatsAppBusinessAccountId).NotEmpty().MaximumLength(64);
    }
}

public class UpdateTenantAiProviderConfigRequestValidator : AbstractValidator<UpdateTenantAiProviderConfigRequest>
{
    private static readonly string[] ChatProviders = { "simulated", "anthropic", "openai", "google" };
    private static readonly string[] EmbeddingProviders = { "simulated", "openai", "google" };

    public UpdateTenantAiProviderConfigRequestValidator()
    {
        RuleFor(x => x.Provider)
            .Must(p => ChatProviders.Contains(p!.ToLowerInvariant()))
            .When(x => x.Provider is not null)
            .WithMessage("Provider must be one of: Simulated, Anthropic, OpenAI, Google.");

        RuleFor(x => x.EmbeddingProvider)
            .Must(p => EmbeddingProviders.Contains(p!.ToLowerInvariant()))
            .When(x => x.EmbeddingProvider is not null)
            .WithMessage("EmbeddingProvider must be one of: Simulated, OpenAI, Google.");
    }
}
