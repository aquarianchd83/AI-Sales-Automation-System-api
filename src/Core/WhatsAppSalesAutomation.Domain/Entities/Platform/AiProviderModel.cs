using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// One model of an AI provider (Google Gemini, Anthropic Claude, OpenAI GPT, or any other) in the platform's model catalogue, maintained by a
/// PlatformSuperAdmin. Platform-global, not <see cref="ITenantOwned"/>. <see cref="Provider"/> is free text so a new provider needs no code change.
/// </summary>
public class AiProviderModel : BaseEntity
{
    /// <summary>Provider name, e.g. "Google", "Anthropic", "OpenAI". Compared case-insensitively.</summary>
    public string Provider { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The exact model id sent to the provider's API, e.g. "claude-sonnet-5-5". Unique per provider.</summary>
    public string ModelId { get; set; } = string.Empty;

    public AiModelType ModelType { get; set; } = AiModelType.Chat;

    public int? ContextWindowTokens { get; set; }

    public int? MaxOutputTokens { get; set; }

    public decimal? InputPricePer1MUsd { get; set; }

    public decimal? OutputPricePer1MUsd { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>The model preferred for this provider and <see cref="ModelType"/>. At most one per (provider, type).</summary>
    public bool IsDefault { get; set; }
}

public enum AiModelType
{
    Chat = 0,
    Embedding = 1,
    Image = 2
}
