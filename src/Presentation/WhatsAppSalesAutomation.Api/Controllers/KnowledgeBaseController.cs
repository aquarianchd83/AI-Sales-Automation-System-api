using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/knowledge-base")]
[Authorize]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IKnowledgeBaseService _knowledgeBaseService;
    private readonly ICurrentUserService _currentUser;
    private readonly IActiveAiProviderAccessor _activeProvider;
    private readonly IEmbeddingProviderCatalog _embeddingCatalog;

    public KnowledgeBaseController(
        IKnowledgeBaseService knowledgeBaseService,
        ICurrentUserService currentUser,
        IActiveAiProviderAccessor activeProvider,
        IEmbeddingProviderCatalog embeddingCatalog)
    {
        _knowledgeBaseService = knowledgeBaseService;
        _currentUser = currentUser;
        _activeProvider = activeProvider;
        _embeddingCatalog = embeddingCatalog;
    }

    /// <summary>Which chat models and embedding providers are actually usable in this deployment -
    /// i.e. have a real API key configured - so the article-list UI can hide publish/embed badges
    /// for ones that could never do anything. See AvailableAiProvidersDto's own doc comment.</summary>
    [HttpGet("available-providers")]
    public ActionResult<AvailableAiProvidersDto> GetAvailableProviders()
    {
        var chatModels = Enum.GetNames<AiModelProvider>()
            .Where(p => _activeProvider.HasApiKey(p))
            .ToList();

        var embeddingProviders = _embeddingCatalog.AllProviders
            .Select(p => p.ProviderName)
            .Where(p => string.Equals(p, "Simulated", StringComparison.OrdinalIgnoreCase) || _activeProvider.HasApiKey(p))
            .ToList();

        return Ok(new AvailableAiProvidersDto(chatModels, embeddingProviders));
    }

    [HttpGet("articles")]
    public async Task<ActionResult<PagedResult<KnowledgeBaseArticleDto>>> GetPaged(
        [FromQuery] PagedRequest request, [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.GetPagedAsync(request, status, cancellationToken));

    [HttpGet("articles/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.GetByIdAsync(id, cancellationToken));

    [HttpPost("articles")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Create([FromBody] CreateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken)
    {
        var created = await _knowledgeBaseService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("articles/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Update(Guid id, [FromBody] UpdateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("articles/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _knowledgeBaseService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Two behaviors behind one optional query param - see IKnowledgeBaseService.PublishAsync's
    /// own doc comment for the full explanation of both. No <paramref name="provider"/>: full
    /// canonical publish (re-chunks, embeds every available provider, records approval). With
    /// <paramref name="provider"/> ("Simulated"/"OpenAI"/"Google"): targeted publish - only embeds
    /// that one provider, leaves every other provider's existing embeddings for this article
    /// untouched, does not record approval. 400s if the provider name doesn't match a known embedding
    /// provider, or if that provider has no API key configured.</summary>
    [HttpPost("articles/{id:guid}/publish")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Publish(Guid id, [FromQuery] string? provider, CancellationToken cancellationToken)
    {
        var approvedByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _knowledgeBaseService.PublishAsync(id, approvedByUserId, provider, cancellationToken));
    }

    /// <summary>Makes this article eligible for retrieval when <paramref name="provider"/>
    /// ("OpenAI"/"Google"/"Anthropic", case-insensitive) is the active chat model. Safe to call again
    /// for a model this article is already published to.</summary>
    [HttpPost("articles/{id:guid}/models/{provider}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> PublishToModel(Guid id, string provider, CancellationToken cancellationToken)
    {
        var publishedByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _knowledgeBaseService.PublishToModelAsync(id, provider, publishedByUserId, cancellationToken));
    }

    /// <summary>Removes this article's eligibility for <paramref name="provider"/>'s retrieval. A
    /// no-op if the article was not published to that model.</summary>
    [HttpDelete("articles/{id:guid}/models/{provider}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> UnpublishFromModel(Guid id, string provider, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.UnpublishFromModelAsync(id, provider, cancellationToken));

    /// <summary>Publishes several articles at once - each still re-chunks/re-embeds individually, but
    /// a not-found or failed id is reported rather than aborting the rest of the batch.</summary>
    [HttpPost("articles/bulk-publish")]
    public async Task<ActionResult<BulkPublishArticlesResultDto>> BulkPublish(
        [FromBody] BulkPublishArticlesRequest request, CancellationToken cancellationToken)
    {
        var approvedByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _knowledgeBaseService.BulkPublishAsync(request, approvedByUserId, cancellationToken));
    }

    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken cancellationToken)
    {
        await _knowledgeBaseService.ReindexAsync(cancellationToken);
        return NoContent();
    }
}
