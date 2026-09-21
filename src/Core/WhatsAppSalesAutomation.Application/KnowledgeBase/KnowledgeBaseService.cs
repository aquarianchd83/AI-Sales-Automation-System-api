using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public class KnowledgeBaseService : IKnowledgeBaseService
{
    // A single paragraph longer than this is split on word boundaries rather than embedded oversized
    // or truncated - keeps every chunk within a predictable token budget for the AI prompt. Not
    // configurable: it is an implementation detail of chunking, not a business-tunable knob like the
    // AiOptions values are.
    private const int MaxChunkChars = 800;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantContext _tenantContext;
    private readonly IPlanLimitsService _planLimits;
    private readonly IEmbeddingService _embeddings;
    private readonly IEmbeddingProviderCatalog _embeddingCatalog;
    private readonly IActiveAiProviderAccessor _activeProvider;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly IValidator<CreateKnowledgeBaseArticleRequest> _createValidator;
    private readonly IValidator<UpdateKnowledgeBaseArticleRequest> _updateValidator;
    private readonly IValidator<BulkPublishArticlesRequest> _bulkPublishValidator;
    private readonly IKnowledgeIngestionService _ingestion;

    public KnowledgeBaseService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        ITenantContext tenantContext,
        IPlanLimitsService planLimits,
        IEmbeddingService embeddings,
        IEmbeddingProviderCatalog embeddingCatalog,
        IActiveAiProviderAccessor activeProvider,
        ITenantConfigOverrideProvider tenantConfig,
        IValidator<CreateKnowledgeBaseArticleRequest> createValidator,
        IValidator<UpdateKnowledgeBaseArticleRequest> updateValidator,
        IValidator<BulkPublishArticlesRequest> bulkPublishValidator,
        IKnowledgeIngestionService ingestion)
    {
        _context = context;
        _dateTime = dateTime;
        _tenantContext = tenantContext;
        _planLimits = planLimits;
        _embeddings = embeddings;
        _embeddingCatalog = embeddingCatalog;
        _activeProvider = activeProvider;
        _tenantConfig = tenantConfig;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _bulkPublishValidator = bulkPublishValidator;
        _ingestion = ingestion;
    }

    public async Task<PagedResult<KnowledgeBaseArticleDto>> GetPagedAsync(PagedRequest request, string? status = null, CancellationToken cancellationToken = default)
    {
        // Anonymous-type projection - see LeadService.GetPagedAsync's comment for why this matters.
        var query =
            from a in _context.KnowledgeBaseArticles
            select new
            {
                Article = a,
                ChunkCount = _context.KnowledgeBaseChunks.Count(c => c.ArticleId == a.Id),
                Models = _context.KnowledgeBaseArticleModelPublications.Where(p => p.ArticleId == a.Id).ToList(),
                // Every chunk belonging to one article is (re)created together in one ReembedAsync
                // call, so they always share the same EmbeddingProvider/EmbeddingModel (and the same
                // set of KnowledgeBaseChunkEmbeddings rows) at any point in time - the first chunk
                // represents the whole article. Null for a Draft article (no chunks yet) rather than
                // an empty string, same "nothing happened yet" meaning as EmbeddingProvider itself.
                FirstChunk = _context.KnowledgeBaseChunks
                    .Where(c => c.ArticleId == a.Id)
                    .OrderBy(c => c.ChunkIndex)
                    .Select(c => new { c.Id, c.EmbeddingProvider, c.EmbeddingModel })
                    .FirstOrDefault()
            };

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<KnowledgeArticleStatus>(status, ignoreCase: true, out var parsedStatus))
                throw Invalid("status", $"Status must be one of: {string.Join(", ", Enum.GetNames<KnowledgeArticleStatus>())}.");

            query = query.Where(x => x.Article.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x => x.Article.Title.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.Article.UpdatedAt ?? x.Article.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        // One batch query for every article's full multi-provider embedding list, keyed by first-chunk
        // id, rather than one query per article - same "avoid N+1" reasoning as the Models sub-select.
        var firstChunkIds = rows.Where(x => x.FirstChunk is not null).Select(x => x.FirstChunk!.Id).ToList();
        var embeddingsByChunk = firstChunkIds.Count == 0
            ? new List<KnowledgeBaseChunkEmbedding>()
            : await _context.KnowledgeBaseChunkEmbeddings.Where(e => firstChunkIds.Contains(e.ChunkId)).ToListAsync(cancellationToken);

        var items = rows.Select(x => x.Article.ToDto(
            x.ChunkCount,
            x.Models.Select(m => m.ToDto()).ToList(),
            x.FirstChunk?.EmbeddingProvider,
            x.FirstChunk?.EmbeddingModel,
            x.FirstChunk is null
                ? Array.Empty<ArticleEmbeddingProviderDto>()
                : embeddingsByChunk
                    .Where(e => e.ChunkId == x.FirstChunk.Id)
                    .Select(e => new ArticleEmbeddingProviderDto(e.Provider, e.Model, e.CreatedAt))
                    .ToList())).ToList();

        return new PagedResult<KnowledgeBaseArticleDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task<KnowledgeBaseArticleDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var article = await FindOrThrowAsync(id, cancellationToken);
        var chunkCount = await _context.KnowledgeBaseChunks.CountAsync(c => c.ArticleId == id, cancellationToken);
        var models = await _context.KnowledgeBaseArticleModelPublications
            .Where(p => p.ArticleId == id)
            .ToListAsync(cancellationToken);
        // See GetPagedAsync's identical comment - every chunk on one article shares the same
        // EmbeddingProvider/EmbeddingModel (and set of KnowledgeBaseChunkEmbeddings rows), so the
        // first one represents the whole article.
        var firstChunk = await _context.KnowledgeBaseChunks
            .Where(c => c.ArticleId == id)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { c.Id, c.EmbeddingProvider, c.EmbeddingModel })
            .FirstOrDefaultAsync(cancellationToken);
        var embeddedProviders = firstChunk is null
            ? Array.Empty<ArticleEmbeddingProviderDto>()
            : await _context.KnowledgeBaseChunkEmbeddings
                .Where(e => e.ChunkId == firstChunk.Id)
                .Select(e => new ArticleEmbeddingProviderDto(e.Provider, e.Model, e.CreatedAt))
                .ToArrayAsync(cancellationToken);

        return article.ToDto(
            chunkCount, models.Select(m => m.ToDto()).ToList(), firstChunk?.EmbeddingProvider, firstChunk?.EmbeddingModel, embeddedProviders);
    }

    public async Task<KnowledgeBaseArticleDto> CreateAsync(CreateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        if (_tenantContext.TenantId is { } tenantId)
            await _planLimits.EnsureCanCreateKnowledgeBaseArticleAsync(tenantId, cancellationToken);

        var sourceType = Enum.Parse<KnowledgeSourceType>(request.SourceType, ignoreCase: true);
        var isGlobal = _tenantContext.TenantId is null && _tenantContext.IsPlatformSuperAdmin;

        // A tenant may only author the one source type that carries no platform authority. Checked
        // here as well as by TenantStampingSaveChangesInterceptor and the CK_KBArticles_* constraints,
        // because this is the layer that can say WHY - a check constraint can only say no.
        if (!isGlobal && !KnowledgeAuthority.IsAuthorableByTenant(sourceType))
        {
            throw Invalid("sourceType",
                $"{sourceType} is platform knowledge and can only be authored by a PlatformSuperAdmin. " +
                $"Tenant articles are {nameof(KnowledgeSourceType.AdminConfiguredArticle)}.");
        }

        var article = new KnowledgeBaseArticle
        {
            ArticleKey = await GenerateArticleKeyAsync(request.Title, cancellationToken),
            // TenantId deliberately left unset: the stamping interceptor assigns it (and, through
            // it, TenantScope) from the ambient tenant, or leaves it NULL for a SuperAdmin authoring
            // platform knowledge. Setting it here would just be a second opinion on the same thing.
            Title = request.Title.Trim(),
            Category = ParseCategory(request.Category),
            // The pre-Phase-6 free-text category has no home in the enum, so the author's own wording
            // is kept here rather than discarded - SubCategory is free text for exactly this reason.
            SubCategory = request.Category?.Trim(),
            Content = request.Content,
            ContentHash = ComputeContentHash(request.Content),
            SourceType = sourceType,
            AuthorityRank = KnowledgeAuthority.RankFor(sourceType, isGlobal),
            Status = KnowledgeArticleStatus.Draft,
            VersionNumber = 1,
            IsCurrentVersion = true,
            // A Draft is not retrievable regardless, and PublishAsync overwrites this with the real
            // publish time - so "now" here only means "no future-dating was asked for".
            EffectiveFrom = _dateTime.UtcNow
        };
        _context.KnowledgeBaseArticles.Add(article);
        await _context.SaveChangesAsync(cancellationToken);

        return article.ToDto(chunkCount: 0);
    }

    public async Task<KnowledgeBaseArticleDto> UpdateAsync(Guid id, UpdateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var article = await FindOrThrowAsync(id, cancellationToken);

        article.Title = request.Title.Trim();
        article.Category = ParseCategory(request.Category);
        article.SubCategory = request.Category?.Trim();
        article.LastUpdatedAt = _dateTime.UtcNow;

        // Only bump Version (and so only invalidate existing chunks as stale) if Content actually
        // changed - editing just the Category/Title should not force a re-embed.
        if (request.Content != article.Content)
        {
            article.Content = request.Content;
            article.ContentHash = ComputeContentHash(request.Content);
            article.VersionNumber++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var article = await FindOrThrowAsync(id, cancellationToken);
        article.IsDeleted = true;
        article.DeletedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<KnowledgeBaseArticleDto> PublishAsync(Guid id, Guid approvedByUserId, string? provider = null, bool securityReviewed = false, CancellationToken cancellationToken = default)
    {
        // Validated before the article lookup, same ordering as PublishToModelAsync - a bad/keyless
        // provider name should 400 without a wasted round-trip to load the article first.
        IEmbeddingService? targetProvider = null;
        if (provider is not null)
        {
            targetProvider = _embeddingCatalog.AllProviders
                .FirstOrDefault(p => string.Equals(p.ProviderName, provider, StringComparison.OrdinalIgnoreCase));
            if (targetProvider is null)
            {
                throw Invalid("provider",
                    $"Provider must be one of: {string.Join(", ", _embeddingCatalog.AllProviders.Select(p => p.ProviderName))}.");
            }

            if (!targetProvider.IsAvailable)
            {
                throw Invalid("provider",
                    $"Cannot publish to {targetProvider.ProviderName} - no API key is configured for it " +
                    $"(AiProviders:{targetProvider.ProviderName}:ApiKey is empty). Add a key before publishing with this provider.");
            }
        }

        var article = await FindOrThrowAsync(id, cancellationToken);

        if (targetProvider is null)
            await PublishThroughIngestionAsync(article, approvedByUserId, securityReviewed, cancellationToken);
        else
            await EmbedSingleProviderAsync(article, targetProvider, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<KnowledgeBaseArticleDto> PublishToModelAsync(Guid id, string provider, Guid publishedByUserId, CancellationToken cancellationToken = default)
    {
        var parsedProvider = ParseProvider(provider);

        // Refuse to make an article eligible for a chat model that has no API key configured - it
        // would create exactly the misleading state this whole feature exists to prevent: a
        // "Published to Claude" badge that can never actually be true, since RetrieveRelevantChunksAsync
        // only ever filters by whichever provider IS active, and a keyless provider can never become
        // active for real. Checked here (not also on unpublish) - removing a stale publication should
        // always be possible even if the key that made it valid was later removed.
        if (!_activeProvider.HasApiKey(parsedProvider.ToString()))
        {
            throw Invalid("provider",
                $"Cannot publish to {parsedProvider} - no API key is configured for it " +
                $"(AiProviders:{parsedProvider}:ApiKey is empty). Add a key before publishing to this model.");
        }

        var article = await FindOrThrowAsync(id, cancellationToken);

        // Only chunk/embed if this article has never been published at all - toggling a model
        // badge on an already-Published article shouldn't silently trigger a re-embed (that stays
        // an explicit action via PublishAsync/BulkPublishAsync/ReindexAsync).
        if (article.Status != KnowledgeArticleStatus.Published)
            await MarkChunkedAndPublishedAsync(article, publishedByUserId, cancellationToken);

        var existing = await _context.KnowledgeBaseArticleModelPublications
            .FirstOrDefaultAsync(p => p.ArticleId == id && p.Provider == parsedProvider, cancellationToken);

        if (existing is null)
        {
            _context.KnowledgeBaseArticleModelPublications.Add(new KnowledgeBaseArticleModelPublication
            {
                ArticleId = id,
                Provider = parsedProvider,
                PublishedAt = _dateTime.UtcNow,
                PublishedBy = publishedByUserId
            });
        }
        else
        {
            // Idempotent republish - same "safe to call again" spirit as PublishAsync.
            existing.PublishedAt = _dateTime.UtcNow;
            existing.PublishedBy = publishedByUserId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<KnowledgeBaseArticleDto> UnpublishFromModelAsync(Guid id, string provider, CancellationToken cancellationToken = default)
    {
        var parsedProvider = ParseProvider(provider);
        await FindOrThrowAsync(id, cancellationToken);

        var existing = await _context.KnowledgeBaseArticleModelPublications
            .FirstOrDefaultAsync(p => p.ArticleId == id && p.Provider == parsedProvider, cancellationToken);

        if (existing is not null)
        {
            _context.KnowledgeBaseArticleModelPublications.Remove(existing);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<BulkPublishArticlesResultDto> BulkPublishAsync(BulkPublishArticlesRequest request, Guid approvedByUserId, CancellationToken cancellationToken = default)
    {
        await _bulkPublishValidator.ValidateAndThrowAsync(request, cancellationToken);

        // Distinct so a caller repeating an id cannot inflate RequestedCount past what was published.
        var ids = request.Ids.Distinct().ToList();

        // Sequential, not parallel: every iteration shares one IApplicationDbContext (EF Core's
        // DbContext is not thread-safe), and each one calls out to IEmbeddingService per chunk - firing
        // those concurrently would just as likely trip a provider's own rate limit as save any time.
        var notFound = new List<Guid>();
        var failed = new List<Guid>();
        var publishedCount = 0;

        foreach (var id in ids)
        {
            try
            {
                await PublishAsync(id, approvedByUserId, cancellationToken: cancellationToken);
                publishedCount++;
            }
            catch (NotFoundException)
            {
                notFound.Add(id);
            }
            catch (Exception)
            {
                // An embedding provider call failing partway through is a real, expected outcome here
                // (network blip, rate limit) - reported like a not-found id rather than aborting
                // whatever in the batch would otherwise have succeeded.
                failed.Add(id);
            }
        }

        return new BulkPublishArticlesResultDto(ids.Count, publishedCount, notFound, failed);
    }

    public async Task ReindexAsync(CancellationToken cancellationToken = default)
    {
        var published = await _context.KnowledgeBaseArticles
            .Where(a => a.Status == KnowledgeArticleStatus.Published)
            .ToListAsync(cancellationToken);

        foreach (var article in published)
        {
            var isStale = !await _context.KnowledgeBaseChunks
                .AnyAsync(c => c.ArticleId == article.Id && c.EmbeddedFromArticleVersion == article.VersionNumber, cancellationToken);

            if (isStale)
                await ReembedAsync(article, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveRelevantChunksAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<RetrievedChunk>();

        var queryEmbedding = await _embeddings.GetEmbeddingAsync(query, cancellationToken);

        // "Simulated" (local/dev default, no API key) and an unrecognized/misconfigured provider
        // string both fail open - every Published article stays retrievable rather than the model
        // filter below silently returning nothing. Only a real, parseable provider narrows results
        // to articles explicitly published to it - see KnowledgeBaseArticleModelPublication.
        var activeProvider = _activeProvider.ActiveProvider;
        var modelFilterApplies = Enum.TryParse<AiModelProvider>(activeProvider, ignoreCase: true, out var parsedProvider);

        // Loads every chunk belonging to a Published (and, once model filtering applies, eligible)
        // article into memory to score it - the in-application cosine similarity approach this phase
        // deliberately chose over a database-side vector search (see KnowledgeBaseChunk's doc
        // comment). Fine for the corpus sizes a single business's knowledge base realistically has in
        // this phase; revisit if that stops being true.
        var candidates = await (
                from c in _context.KnowledgeBaseChunks
                join a in _context.KnowledgeBaseArticles on c.ArticleId equals a.Id
                // IsActive: a re-index stages its replacement chunks INACTIVE and swaps them in at the end,
                // so without this a stale or half-built staged set would be scored alongside the live one.
                // TenantId != null: chunks are now scoped-or-global, and a GLOBAL article is platform
                // support knowledge - it must never be quoted to a tenant's customers in a sales chat.
                where a.Status == KnowledgeArticleStatus.Published && c.Embedding != null
                    && c.IsActive && c.TenantId != null
                    && (!modelFilterApplies || _context.KnowledgeBaseArticleModelPublications.Any(p => p.ArticleId == a.Id && p.Provider == parsedProvider))
                select c)
            .ToListAsync(cancellationToken);

        // Resolved per call (not once per DI scope) - merges this tenant's Ai:* overrides, if any,
        // over the platform default. See ITenantConfigOverrideProvider's own doc comment.
        var aiOptions = await _tenantConfig.GetAiOptionsAsync(cancellationToken);

        var scored = candidates
            .Select(c => new RetrievedChunk(c.Id, c.ArticleId, c.ChunkText, CosineSimilarity(queryEmbedding, DeserializeEmbedding(c.Embedding!))))
            .Where(r => r.RelevanceScore >= aiOptions.MinRelevanceScore)
            .OrderByDescending(r => r.RelevanceScore)
            .Take(aiOptions.KnowledgeBaseTopN)
            .ToList();

        return scored;
    }

    /// <summary>Publishes for the "first time this article goes live" branch of PublishToModelAsync -
    /// the same pipeline as the explicit Publish action, without a security-review override.</summary>
    private Task MarkChunkedAndPublishedAsync(KnowledgeBaseArticle article, Guid approvedByUserId, CancellationToken cancellationToken) =>
        PublishThroughIngestionAsync(article, approvedByUserId, securityReviewed: false, cancellationToken);

    /// <summary>
    /// The full publish: mark the article Published, then chunk, embed and index it through the Phase 6
    /// ingestion pipeline, inside this request.
    ///
    /// Replaces the earlier in-line chunk-and-embed. The differences that matter: chunks are built
    /// structure-aware instead of by paragraph length, content is scanned for prompt-injection before
    /// anything is embedded, and the old chunks are swapped for the new in one transaction - so a
    /// provider failing half-way now leaves the PREVIOUS index serving instead of a half-embedded
    /// article.
    ///
    /// The article is marked Published first because the pipeline only indexes Approved or Published
    /// content and the database refuses a Published row without an approver - so the approval fields
    /// have to exist before it runs. If indexing then does not complete, every field is put back and the
    /// user gets the reason: an article must never be left saying "Published" with nothing indexed.
    /// </summary>
    private async Task PublishThroughIngestionAsync(
        KnowledgeBaseArticle article, Guid approvedByUserId, bool securityReviewed, CancellationToken cancellationToken)
    {
        var before = (article.Status, article.ApprovedBy, article.ApprovedAt, article.PublishedBy, article.PublishedAt, article.ReviewDueAt, article.EffectiveFrom);

        article.Status = KnowledgeArticleStatus.Published;
        article.ApprovedBy = approvedByUserId;

        // CK_KBArticles_PublishedHasApprover refuses a Published row without BOTH an approver and an
        // approval time.
        var publishedAt = _dateTime.UtcNow;
        article.ApprovedAt = article.ApprovedAt ?? publishedAt;
        article.PublishedBy = approvedByUserId;
        article.PublishedAt = publishedAt;

        // Only defaulted, never overwritten: an article deliberately future-dated by its author must
        // keep that date through a publish, which is the point of an effective window.
        if (article.EffectiveFrom == default)
            article.EffectiveFrom = publishedAt;

        article.ReviewDueAt = publishedAt.AddDays(KnowledgeAuthority.ReviewIntervalDays(article.SourceType));
        await _context.SaveChangesAsync(cancellationToken);

        KnowledgeIngestionJob job;
        try
        {
            job = await _ingestion.IndexNowAsync(article.Id, securityReviewed, cancellationToken);
        }
        catch
        {
            Restore(article, before);
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (job.State == KnowledgeIngestionState.Completed)
            return;

        Restore(article, before);
        await _context.SaveChangesAsync(CancellationToken.None);
        throw Invalid("publish", DescribeUnfinishedIndexing(job));
    }

    private static void Restore(
        KnowledgeBaseArticle article,
        (KnowledgeArticleStatus Status, Guid? ApprovedBy, DateTime? ApprovedAt, Guid? PublishedBy, DateTime? PublishedAt, DateTime? ReviewDueAt, DateTime EffectiveFrom) before)
    {
        article.Status = before.Status;
        article.ApprovedBy = before.ApprovedBy;
        article.ApprovedAt = before.ApprovedAt;
        article.PublishedBy = before.PublishedBy;
        article.PublishedAt = before.PublishedAt;
        article.ReviewDueAt = before.ReviewDueAt;
        article.EffectiveFrom = before.EffectiveFrom;
    }

    /// <summary>A sentence a person can act on for each way indexing can stop short of Completed. The
    /// findings' excerpts are included because "flagged" on a long article is not actionable - the
    /// sentence that tripped the scan is.</summary>
    private static string DescribeUnfinishedIndexing(KnowledgeIngestionJob job)
    {
        string Excerpts()
        {
            if (string.IsNullOrWhiteSpace(job.SecurityFindingsJson))
                return string.Empty;

            try
            {
                var found = JsonSerializer.Deserialize<List<InjectionFinding>>(job.SecurityFindingsJson) ?? new();
                var shown = found.Where(f => f.Severity >= InjectionSeverity.Flag).Take(3).Select(f => "\"" + f.Excerpt.Trim() + "\"");
                var text = string.Join("; ", shown);
                return text.Length == 0 ? string.Empty : " Found: " + text + ".";
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        return job.State switch
        {
            KnowledgeIngestionState.AwaitingApproval =>
                "This article contains wording that needs a security review before it can be published - it reads like an " +
                "instruction to the AI." + Excerpts() + " If it is genuine content, review it and publish again with " +
                "securityReviewed=true.",

            KnowledgeIngestionState.Rejected when job.ReasonCode == "InjectionBlocked" =>
                "This article was not published: it contains text that tries to give the AI instructions or a different " +
                "role, which is never allowed in knowledge content." + Excerpts(),

            KnowledgeIngestionState.Rejected =>
                $"This article could not be published: {job.Detail ?? job.ReasonCode}",

            KnowledgeIngestionState.Failed =>
                $"Indexing failed ({job.ReasonCode}): {job.Detail} The article was not published; you can try again.",

            _ => $"Indexing did not complete (it stopped in state {job.State}). The article was not published."
        };
    }

    /// <summary>The targeted-publish half of PublishAsync's merged behavior - see its own doc comment.
    /// Re-chunks only if needed (no chunks yet, or existing ones are stale), embeds only via
    /// targetProvider, and never touches any other provider's existing KnowledgeBaseChunkEmbeddings
    /// rows for this article. Does not set ApprovedBy or call SaveChangesAsync - the caller (PublishAsync)
    /// owns both, same as ReembedAsync/MarkChunkedAndPublishedAsync's split.</summary>
    private async Task EmbedSingleProviderAsync(KnowledgeBaseArticle article, IEmbeddingService targetProvider, CancellationToken cancellationToken)
    {
        var chunks = await _context.KnowledgeBaseChunks.Where(c => c.ArticleId == article.Id).OrderBy(c => c.ChunkIndex).ToListAsync(cancellationToken);
        // Same staleness check as ReindexAsync: chunks whose EmbeddedFromArticleVersion is behind the
        // article's current Version were split from since-edited Content - see UpdateAsync's own doc
        // comment for why an edit alone does not re-chunk. Re-embedding stale chunk text would
        // silently produce a vector for outdated content, so this re-chunks fresh first, exactly like
        // ReembedAsync would - the difference is only which provider(s) get embedded afterward.
        var isStale = chunks.Count > 0 && !chunks.Any(c => c.EmbeddedFromArticleVersion == article.VersionNumber);

        if (chunks.Count == 0 || isStale)
        {
            if (isStale)
            {
                // Every provider's existing embedding for these chunks is stale too, not just
                // targetProvider's - the chunk text itself is about to change. Same "remove
                // embeddings before their chunks" ordering as ReembedAsync.
                var staleChunkIds = chunks.Select(c => c.Id).ToList();
                var staleEmbeddings = await _context.KnowledgeBaseChunkEmbeddings
                    .Where(e => staleChunkIds.Contains(e.ChunkId))
                    .ToListAsync(cancellationToken);
                _context.KnowledgeBaseChunkEmbeddings.RemoveRange(staleEmbeddings);
                _context.KnowledgeBaseChunks.RemoveRange(chunks);
                chunks.Clear();
            }

            var index = 0;
            foreach (var text in ChunkContent(article.Content))
            {
                var chunk = NewChunk(article, index++, text);
                _context.KnowledgeBaseChunks.Add(chunk);
                chunks.Add(chunk);
            }
            article.Status = KnowledgeArticleStatus.Published;
        }

        // Batch-fetch targetProvider's existing rows for these chunks (an earlier targeted publish,
        // or a full ReembedAsync that included this provider) so this upserts instead of duplicating -
        // same "one row per (chunk, provider)" reasoning as KnowledgeBaseChunkEmbeddingConfiguration's
        // unique index. Brand-new chunks (not yet saved) simply have no matching row here, which is
        // correct.
        var chunkIds = chunks.Select(c => c.Id).ToList();
        var existingByChunkId = (await _context.KnowledgeBaseChunkEmbeddings
                .Where(e => chunkIds.Contains(e.ChunkId) && e.Provider == targetProvider.ProviderName)
                .ToListAsync(cancellationToken))
            .ToDictionary(e => e.ChunkId);

        var activeProviderName = _embeddings.ProviderName;
        foreach (var chunk in chunks)
        {
            var vector = await targetProvider.GetEmbeddingAsync(chunk.ChunkText, cancellationToken);
            if (vector.Length == 0)
                continue; // provider call failed - see IEmbeddingService.GetEmbeddingAsync's own doc comment

            var json = JsonSerializer.Serialize(vector);
            if (existingByChunkId.TryGetValue(chunk.Id, out var existingEmbedding))
            {
                existingEmbedding.Embedding = json;
                existingEmbedding.Model = targetProvider.ModelName;
            }
            else
            {
                _context.KnowledgeBaseChunkEmbeddings.Add(new KnowledgeBaseChunkEmbedding
                {
                    ChunkId = chunk.Id,
                    Provider = targetProvider.ProviderName,
                    Model = targetProvider.ModelName,
                    Embedding = json
                });
            }

            // Also refresh the chunk's own "active provider" copy if this happens to be the one
            // RetrieveRelevantChunksAsync currently reads - see KnowledgeBaseChunkEmbedding's doc
            // comment on why that copy exists.
            if (targetProvider.ProviderName == activeProviderName)
            {
                chunk.Embedding = json;
                chunk.EmbeddingProvider = targetProvider.ProviderName;
                chunk.EmbeddingModel = targetProvider.ModelName;
            }
        }
    }

    /// <summary>Chunks Content, embeds each chunk via every available provider (see
    /// IEmbeddingProviderCatalog), and replaces this article's previous chunks - shared by
    /// PublishAsync (one article) and ReindexAsync (every stale Published article).</summary>
    private async Task ReembedAsync(KnowledgeBaseArticle article, CancellationToken cancellationToken)
    {
        var existingChunkIds = await _context.KnowledgeBaseChunks
            .Where(c => c.ArticleId == article.Id)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        if (existingChunkIds.Count > 0)
        {
            var existingEmbeddings = await _context.KnowledgeBaseChunkEmbeddings
                .Where(e => existingChunkIds.Contains(e.ChunkId))
                .ToListAsync(cancellationToken);
            _context.KnowledgeBaseChunkEmbeddings.RemoveRange(existingEmbeddings);
        }
        _context.KnowledgeBaseChunks.RemoveRange(
            await _context.KnowledgeBaseChunks.Where(c => c.ArticleId == article.Id).ToListAsync(cancellationToken));

        // Only providers with what they need to actually be called (Simulated always; OpenAI/Google
        // only when their ApiKey is configured) - see IEmbeddingService.IsAvailable's own doc comment.
        var availableProviders = _embeddingCatalog.AllProviders.Where(p => p.IsAvailable).ToList();
        var activeProviderName = _embeddings.ProviderName;

        var chunkTexts = ChunkContent(article.Content);
        var index = 0;
        foreach (var text in chunkTexts)
        {
            var chunk = NewChunk(article, index++, text);

            // Embed via every available provider, not just the currently active one, so the admin UI
            // can show which AI models this content has actually been embedded for - see
            // KnowledgeBaseChunkEmbedding's own doc comment. The row matching the active
            // EmbeddingProvider is also copied onto the chunk's own columns, since
            // RetrieveRelevantChunksAsync's cosine similarity reads those directly.
            foreach (var provider in availableProviders)
            {
                var vector = await provider.GetEmbeddingAsync(text, cancellationToken);
                if (vector.Length == 0)
                    continue; // provider call failed - see IEmbeddingService.GetEmbeddingAsync's own doc comment

                var json = JsonSerializer.Serialize(vector);
                _context.KnowledgeBaseChunkEmbeddings.Add(new KnowledgeBaseChunkEmbedding
                {
                    ChunkId = chunk.Id,
                    Provider = provider.ProviderName,
                    Model = provider.ModelName,
                    Embedding = json
                });

                if (provider.ProviderName == activeProviderName)
                {
                    chunk.Embedding = json;
                    chunk.EmbeddingProvider = provider.ProviderName;
                    chunk.EmbeddingModel = provider.ModelName;
                }
            }

            _context.KnowledgeBaseChunks.Add(chunk);
        }
    }

    private static IReadOnlyList<string> ChunkContent(string content)
    {
        var paragraphs = content
            .Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > MaxChunkChars)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }

                chunks.AddRange(SplitLongParagraph(paragraph));
                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 1 > MaxChunkChars)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            current.Append(paragraph).Append('\n');
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        return chunks;
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > MaxChunkChars)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            current.Append(word).Append(' ');
        }

        if (current.Length > 0)
            yield return current.ToString().Trim();
    }

    private static float[] DeserializeEmbedding(string json) =>
        JsonSerializer.Deserialize<float[]>(json) ?? Array.Empty<float>();

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>
    /// Builds a chunk with every field the Phase 6 schema requires, so the two call sites that create
    /// chunks cannot drift apart on what "a complete chunk" means - which they had already started to,
    /// one of them carrying a comment the other did not.
    ///
    /// ContextHeader/EmbeddingInput/SearchText are filled from the article here rather than left to
    /// the ingestion pipeline: this is the legacy sales-RAG path, and a chunk it writes still has to
    /// satisfy the same NOT NULL columns as one the Phase 6 pipeline writes. The header it produces is
    /// the simple breadcrumb form; the structure-aware pipeline builds a richer one.
    /// </summary>
    private static KnowledgeBaseChunk NewChunk(KnowledgeBaseArticle article, int chunkIndex, string text)
    {
        var header = BuildContextHeader(article);

        return new KnowledgeBaseChunk
        {
            ArticleId = article.Id,
            ChunkIndex = chunkIndex,
            ContextHeader = header,
            ChunkText = text,
            EmbeddingInput = header + "\n\n" + text,
            // Keywords appear twice - that repetition IS the 2x keyword field boost, achieved without
            // maintaining a second full-text index. See KnowledgeBaseChunk.SearchText.
            SearchText = string.Join(" ", new[] { header, text, article.Keywords, article.Keywords }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
            // Rough estimate (~4 chars/token in English), not a real tokenizer - good enough for
            // the "roughly how big is this chunk" signal this column exists for.
            TokenCount = text.Length / 4,
            EmbeddedFromArticleVersion = article.VersionNumber,
            IsActive = true,

            // Denormalized from the article so the retrieval filter never needs a join. A snapshot,
            // not a live mirror - see KnowledgeBaseChunk's own doc comment.
            TenantId = article.TenantId,
            AuthorityRank = article.AuthorityRank,
            ProductModule = article.ProductModule,
            SourceType = article.SourceType,
            LanguageCode = article.LanguageCode,
            CountryCode = article.CountryCode,
            ArticleStatus = article.Status,
            EffectiveFrom = article.EffectiveFrom,
            EffectiveTo = article.EffectiveTo,
            IsCurrentArticleVersion = article.IsCurrentVersion
        };
    }

    /// <summary>The breadcrumb + applicability line prepended to every chunk so it reads on its own
    /// once retrieval has torn it out of its article - "Billing > Refunds (en, IN)". Without it a
    /// chunk that says "this does not apply" is indistinguishable from one that says it about
    /// something else entirely.</summary>
    private static string BuildContextHeader(KnowledgeBaseArticle article)
    {
        var breadcrumb = string.Join(" > ", new[]
            {
                article.Category.ToString(),
                article.SubCategory,
                article.Title
            }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var applicability = article.CountryCode is null
            ? article.LanguageCode
            : article.LanguageCode + ", " + article.CountryCode;

        return breadcrumb + " (" + applicability + ")";
    }

    /// <summary>SHA-256 of the content with line endings and trailing whitespace normalized, as
    /// lowercase hex. Normalizing first is what makes the hash useful: the same article pasted from a
    /// Windows editor and a Unix one is the same article, and a duplicate check that said otherwise
    /// would be noise nobody acts on.</summary>
    private static string ComputeContentHash(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Best-effort map from the pre-Phase-6 free-text category onto the enum, falling back to
    /// GettingStarted. The original string is never lost - CreateAsync/UpdateAsync keep it in
    /// SubCategory - so a miss here costs a retrieval-time filter bucket, not the author's words.</summary>
    private static KnowledgeCategory ParseCategory(string? category) =>
        !string.IsNullOrWhiteSpace(category) &&
        Enum.TryParse<KnowledgeCategory>(category.Trim().Replace(" ", string.Empty), ignoreCase: true, out var parsed)
            ? parsed
            : KnowledgeCategory.GettingStarted;

    /// <summary>
    /// A URL-safe slug of the title, made unique within the article's scope.
    ///
    /// ArticleKey is the identity a citation survives on, so it has to be stable and unique per
    /// (TenantId, ArticleKey, LanguageCode) - UX_KBArticles_CurrentVersion enforces the uniqueness,
    /// and hitting that as a 500 on save would be a poor way to find out. The numeric suffix loop is
    /// the boring, predictable way to resolve a collision; querying with IgnoreQueryFilters is
    /// deliberately NOT done here, so a tenant cannot discover another tenant's article keys by
    /// watching which suffixes it gets handed.
    /// </summary>
    private async Task<string> GenerateArticleKeyAsync(string title, CancellationToken cancellationToken)
    {
        var slug = new string(title.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        slug = slug.Trim('-');

        if (slug.Length == 0)
            slug = "article";
        else if (slug.Length > 180)
            slug = slug[..180].TrimEnd('-');   // 200-char column, leaving room for a suffix

        var candidate = slug;
        var suffix = 2;
        while (await _context.KnowledgeBaseArticles.AnyAsync(a => a.ArticleKey == candidate, cancellationToken))
        {
            candidate = slug + "-" + suffix;
            suffix++;
        }

        return candidate;
    }

    private async Task<KnowledgeBaseArticle> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.KnowledgeBaseArticles.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(KnowledgeBaseArticle), id);

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });

    /// <summary>Same "string in, validate, throw Invalid()" shape as the status query-param handling
    /// in GetPagedAsync - provider arrives as a route segment (string), not a request body, so there's
    /// no FluentValidation request class for it.</summary>
    private static AiModelProvider ParseProvider(string provider)
    {
        if (!Enum.TryParse<AiModelProvider>(provider, ignoreCase: true, out var parsed))
            throw Invalid("provider", $"Provider must be one of: {string.Join(", ", Enum.GetNames<AiModelProvider>())}.");

        return parsed;
    }
}
