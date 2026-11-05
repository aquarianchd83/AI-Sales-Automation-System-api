using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>One place a scene's clip can come from, and whether the platform lets a tenant use it right now (with the reason when not).</summary>
public sealed record VideoSourceDto(SceneVideoProviderKind Kind, bool Available, string? Reason);

public interface IStoryVideoSourcePolicy
{
    IReadOnlyList<VideoSourceDto> Sources();

    /// <summary>Throws a sentence the tenant can read when the source cannot be used. The free test clips and Google Flow import are always allowed.</summary>
    void EnsureAllowed(SceneVideoProviderKind kind);
}

/// <summary>
/// Veo is the one source that costs money and needs the platform's say-so: the operator switches it on under Integrations > Story Studio, and it also
/// needs the Gemini key saved under Configuration > AI Providers (and Gemini switched on there).
/// </summary>
public class StoryVideoSourcePolicy : IStoryVideoSourcePolicy
{
    private readonly IOptionsSnapshot<CinematicOptions> _options;
    private readonly IPlatformAiConfigProvider _credentials;

    public StoryVideoSourcePolicy(IOptionsSnapshot<CinematicOptions> options, IPlatformAiConfigProvider credentials)
    {
        _options = options;
        _credentials = credentials;
    }

    public IReadOnlyList<VideoSourceDto> Sources() => new[]
    {
        new VideoSourceDto(SceneVideoProviderKind.Simulator, true, null),
        new VideoSourceDto(SceneVideoProviderKind.Veo, VeoReason() is null, VeoReason()),
        new VideoSourceDto(SceneVideoProviderKind.FlowImport, true, null)
    };

    public void EnsureAllowed(SceneVideoProviderKind kind)
    {
        if (kind == SceneVideoProviderKind.Veo && VeoReason() is { } reason) throw new ConflictException(reason);
    }

    private string? VeoReason()
    {
        if (!string.Equals(_options.Value.VideoProvider, "Google", StringComparison.OrdinalIgnoreCase))
            return "Gemini (Veo) is not switched on for the studio. Choose the free test clips or Google Flow, or ask the platform operator to switch Gemini on under Integrations > Story Studio.";

        var ai = _credentials.Get();
        if (!ai.IsEnabled("Google") || string.IsNullOrWhiteSpace(ai.GoogleApiKey))
            return "Veo needs a Gemini key, and none is available. Ask the platform operator to add it under Configuration > AI Providers.";

        return null;
    }
}
