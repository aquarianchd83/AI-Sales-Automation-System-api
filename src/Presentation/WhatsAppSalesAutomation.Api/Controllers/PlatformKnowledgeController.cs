using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The platform's own knowledge base: the articles the AI support agent draws on when ANY tenant asks
/// about the product itself - the refund policy, how credits are bought, what a plan includes. The
/// platform team maintains them, and the platform pays to embed them (see PlatformEmbeddingService).
///
/// PlatformSuperAdmin only, throughout: everything here shapes what every tenant's support agent is
/// allowed to say. Runs through the same <see cref="IKnowledgeBaseService"/> as a tenant's Knowledge
/// Base - a SuperAdmin has no tenant, so the same code creates and lists GLOBAL articles instead of a
/// tenant's, and the same rules apply. Every change is written to the platform audit log.
/// </summary>
[ApiController]
[Route("api/v1/platform/knowledge")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformKnowledgeController : ControllerBase
{
    private readonly IKnowledgeRetrievalSimulator _simulator;
    private readonly IKnowledgeBaseService _articles;
    private readonly IKnowledgeUploadService _upload;
    private readonly IKnowledgeIngestionService _ingestion;
    private readonly IPlatformEmbeddingService _platformEmbedding;
    private readonly IPlatformAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public PlatformKnowledgeController(
        IKnowledgeRetrievalSimulator simulator,
        IKnowledgeBaseService articles,
        IKnowledgeUploadService upload,
        IKnowledgeIngestionService ingestion,
        IPlatformEmbeddingService platformEmbedding,
        IPlatformAuditService audit,
        ICurrentUserService currentUser)
    {
        _simulator = simulator;
        _articles = articles;
        _upload = upload;
        _ingestion = ingestion;
        _platformEmbedding = platformEmbedding;
        _audit = audit;
        _currentUser = currentUser;
    }

    // -- Embedding status --------------------------------------------------------------------

    /// <summary>
    /// Which embedding provider platform articles are being indexed with, and whether that is the real
    /// thing. When it is the Simulated stand-in, tenants using a real provider will never find these
    /// articles - so the screen shows this prominently rather than leaving it to be discovered.
    /// </summary>
    [HttpGet("embedding-status")]
    public ActionResult<PlatformEmbeddingStatusDto> GetEmbeddingStatus()
    {
        string? warning = null;

        if (_platformEmbedding.RealProviderRequestedButUnconfigured)
        {
            warning = "The platform is set to use a real embedding provider but its API key is missing, so " +
                      "articles are being embedded by the Simulated stand-in. Tenants using a real provider will " +
                      "not find them. Add the key on the Configuration screen (AiProviders), then re-index.";
        }
        else if (_platformEmbedding.IsSimulated)
        {
            warning = "Platform articles are embedded by the Simulated stand-in, which is fine for local development " +
                      "but means tenants using a real embedding provider will not find them. Set AiProviders:EmbeddingProvider " +
                      "and its API key on the Configuration screen, then re-index.";
        }

        return Ok(new PlatformEmbeddingStatusDto(
            _platformEmbedding.ProviderName, _platformEmbedding.ModelName, _platformEmbedding.IsSimulated,
            _platformEmbedding.RealProviderRequestedButUnconfigured, warning));
    }

    // -- Articles ----------------------------------------------------------------------------

    [HttpGet("articles")]
    public async Task<ActionResult<PagedResult<KnowledgeBaseArticleDto>>> GetArticles(
        [FromQuery] PagedRequest request, [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await _articles.GetPagedAsync(request, status, cancellationToken));

    [HttpGet("articles/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> GetArticle(Guid id, CancellationToken cancellationToken)
        => Ok(await _articles.GetByIdAsync(id, cancellationToken));

    [HttpPost("articles")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Create(
        [FromBody] CreateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken)
    {
        var created = await _articles.CreateAsync(request, cancellationToken);
        await Log(PlatformAuditActions.KnowledgeArticleCreated, created.Title, cancellationToken);
        return CreatedAtAction(nameof(GetArticle), new { id = created.Id }, created);
    }

    [HttpPut("articles/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Update(
        Guid id, [FromBody] UpdateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken)
    {
        var updated = await _articles.UpdateAsync(id, request, cancellationToken);
        await Log(PlatformAuditActions.KnowledgeArticleUpdated, updated.Title, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("articles/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var title = (await _articles.GetByIdAsync(id, cancellationToken)).Title;
        await _articles.DeleteAsync(id, cancellationToken);
        await Log(PlatformAuditActions.KnowledgeArticleDeleted, title, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Publishes a platform article: scans it, chunks it, embeds it with the PLATFORM's provider, and makes
    /// it retrievable by every tenant's support agent. A refusal (content that reads like an instruction to
    /// the AI) comes back as a 400 quoting the sentence; <c>securityReviewed=true</c> acknowledges a
    /// review-level finding.
    /// </summary>
    [HttpPost("articles/{id:guid}/publish")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Publish(
        Guid id, [FromQuery] bool securityReviewed, CancellationToken cancellationToken)
    {
        var published = await _articles.PublishAsync(id, Actor, provider: null, securityReviewed, cancellationToken);
        await Log(PlatformAuditActions.KnowledgeArticlePublished, published.Title, cancellationToken);
        return Ok(published);
    }

    /// <summary>Retires a published article - it leaves every tenant's support answers immediately - with
    /// a required reason. The article stays readable here.</summary>
    [HttpPost("articles/{id:guid}/deprecate")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Deprecate(
        Guid id, [FromBody] DeprecateArticleRequest request, CancellationToken cancellationToken)
    {
        var deprecated = await _articles.DeprecateAsync(id, request.Note, cancellationToken);
        await Log(PlatformAuditActions.KnowledgeArticleDeprecated, $"{deprecated.Title}: {request.Note}", cancellationToken);
        return Ok(deprecated);
    }

    /// <summary>Creates a DRAFT platform article from an uploaded .md, .txt, .html, .docx or .pdf (10 MB).
    /// Nothing is published: read what was extracted first. Multipart: <c>file</c>, optional <c>title</c>,
    /// <c>category</c>, <c>sourceType</c> - any source type is allowed here, unlike a tenant.</summary>
    [HttpPost("articles/upload")]
    [RequestSizeLimit(DocumentTextExtractor.MaxFileBytes + 1024 * 1024)]
    public async Task<ActionResult<UploadKnowledgeArticleResultDto>> Upload(
        IFormFile? file, [FromForm] string? title, [FromForm] string? category, [FromForm] string? sourceType,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            throw new ValidationException(new[] { new FluentValidation.Results.ValidationFailure("file", "Attach a file to upload.") });

        await using var stream = file.OpenReadStream();
        var result = await _upload.UploadAsync(
            file.FileName, file.Length, stream, new UploadKnowledgeArticleRequest(title, category, sourceType), cancellationToken);

        await Log(PlatformAuditActions.KnowledgeArticleCreated, $"{result.Article.Title} (uploaded from {file.FileName})", cancellationToken);
        return CreatedAtAction(nameof(GetArticle), new { id = result.Article.Id }, result);
    }

    [HttpGet("articles/{id:guid}/indexing")]
    public async Task<ActionResult<KnowledgeIngestionJobDto>> GetIndexing(Guid id, CancellationToken cancellationToken)
    {
        var job = await _ingestion.GetLatestJobAsync(id, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Re-indexes every published platform article whose chunks are behind its content - what to
    /// run after fixing the platform's embedding configuration.</summary>
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken cancellationToken)
    {
        await _articles.ReindexAsync(cancellationToken);
        await Log(PlatformAuditActions.KnowledgeReindexed, "Re-indexed stale platform articles", cancellationToken);
        return NoContent();
    }

    // -- Retrieval simulation ----------------------------------------------------------------

    /// <summary>
    /// Runs real retrieval for a question, optionally as a specific tenant, and returns every stage's
    /// breakdown: what was searched, what each leg found, how it fused, whether the reranker ran, and
    /// whether the evidence gate would have let the agent answer. See KnowledgeRetrievalSimulator.
    /// </summary>
    [HttpPost("retrieval/simulate")]
    public async Task<ActionResult<KnowledgeRetrievalResult>> Simulate(
        [FromBody] SimulateRetrievalRequest request, CancellationToken cancellationToken)
        => Ok(await _simulator.SimulateAsync(request, Actor, _currentUser.Email ?? string.Empty, cancellationToken));

    // -- helpers -----------------------------------------------------------------------------

    private Guid Actor => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Task Log(string action, string details, CancellationToken cancellationToken) =>
        _audit.LogAsync(Actor, _currentUser.Email ?? string.Empty, action, details: details, cancellationToken: cancellationToken);
}

public record DeprecateArticleRequest(string Note);

public record PlatformEmbeddingStatusDto(string Provider, string Model, bool IsSimulated, bool KeyMissing, string? Warning);
