using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Integrations page - PlatformSuperAdmin-only. Chooses, for the WhatsApp consent SMS and for the DND
/// check, between the built-in simulator and a third-party API. Stored in the database only (the API keys encrypted).</summary>
[ApiController]
[Route("api/v1/platform/integration-settings")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformIntegrationSettingsController : ControllerBase
{
    private readonly IPlatformIntegrationSettingsService _settings;
    private readonly IPlatformAiFeatureSettingsService _aiFeatures;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformIntegrationSettingsController(
        IPlatformIntegrationSettingsService settings,
        IPlatformAiFeatureSettingsService aiFeatures,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser)
    {
        _settings = settings;
        _aiFeatures = aiFeatures;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <summary>API keys are masked: only whether one is stored and its last four characters come back.</summary>
    [HttpGet]
    public async Task<ActionResult<PlatformIntegrationSettingsDto>> Get(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(cancellationToken));

    /// <summary>Takes effect on the next request, no restart. A null API key keeps the stored one; an empty string clears it.</summary>
    [HttpPut]
    public async Task<ActionResult<PlatformIntegrationSettingsDto>> Update([FromBody] UpdatePlatformIntegrationSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _settings.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        // Names what changed, never a key value.
        var keys = new List<string>();
        if (request.ConsentSms.ApiKey is not null) keys.Add("consent SMS API key");
        if (request.DndCheck.ApiKey is not null) keys.Add("DND check API key");
        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, PlatformAuditActions.IntegrationSettingsUpdated,
            details: $"Consent SMS: {result.ConsentSms.Mode}, DND check: {result.DndCheck.Mode}; " +
                     (keys.Count == 0 ? "keys unchanged" : $"{string.Join(" and ", keys)} updated"),
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>The engine behind each AI feature: simulator, or ChatGPT / Claude / Gemini with the key saved under AI Providers.</summary>
    [HttpGet("ai-features")]
    public async Task<ActionResult<PlatformAiFeatureSettingsDto>> GetAiFeatures(CancellationToken cancellationToken)
        => Ok(await _aiFeatures.GetAsync(cancellationToken));

    /// <summary>Takes effect on the next request, no restart. Switching to a provider with no saved key is refused.</summary>
    [HttpPut("ai-features")]
    public async Task<ActionResult<PlatformAiFeatureSettingsDto>> UpdateAiFeatures([FromBody] UpdatePlatformAiFeatureSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _aiFeatures.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        static string Describe(AiFeatureChannelDto c) => c.Mode == "Api" ? c.Provider : "Simulator";
        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, PlatformAuditActions.AiFeatureSettingsUpdated,
            details: $"Conversation: {(result.Conversation.Mode == "Api" ? result.Conversation.Provider : "Simulator")} " +
                     $"(embeddings {(result.Conversation.EmbeddingMode == "Api" ? result.Conversation.EmbeddingProvider : "Simulator")}), " +
                     $"discovery lead: {Describe(result.DiscoveryLead)}, knowledge base: {Describe(result.KnowledgeBase)}, " +
                     $"content generation: {Describe(result.ContentGeneration)}, video renders: {Describe(result.VideoRenders)}",
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Asks the chosen third-party tool one trivial question with the saved key, saved choice or not. Always 200: the result says whether it worked.</summary>
    [HttpPost("ai-features/{feature}/test")]
    public async Task<ActionResult<DeliveryTestResultDto>> TestAiFeature(string feature, [FromBody] TestAiFeatureRequest request, CancellationToken cancellationToken)
        => Ok(await _aiFeatures.TestAsync(feature, request, cancellationToken));

    /// <summary>Sends one real consent-style SMS through the third-party API in the request, saved or not. Always 200: the result says whether it worked.</summary>
    [HttpPost("test-consent-sms")]
    public async Task<ActionResult<DeliveryTestResultDto>> TestConsentSms([FromBody] SendIntegrationTestRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.TestConsentSmsAsync(request, cancellationToken));

    /// <summary>Checks one number against the third-party DND API in the request, saved or not. Always 200.</summary>
    [HttpPost("test-dnd-check")]
    public async Task<ActionResult<DeliveryTestResultDto>> TestDndCheck([FromBody] SendIntegrationTestRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.TestDndCheckAsync(request, cancellationToken));
}
