using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
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
    private readonly IEmbeddingService _embeddings;
    private readonly IEmbeddingProviderCatalog _embeddingCatalog;
    private readonly IActiveAiProviderAccessor _activeProvider;
    private readonly AiOptions _aiOptions;
    private readonly IValidator<CreateKnowledgeBaseArticleRequest> _createValidator;
    private readonly IValidator<UpdateKnowledgeBaseArticleRequest> _updateValidator;
    private readonly IValidator<BulkPublishArticlesRequest> _bulkPublishValidator;

    public KnowledgeBaseService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IEmbeddingService embeddings,
        IEmbeddingProviderCatalog embeddingCatalog,
        IActiveAiProviderAccessor activeProvider,
        IOptions<AiOptions> aiOptions,
        IValidator<CreateKnowledgeBaseArticleRequest> createValidator,
        IValidator<UpdateKnowledgeBaseArticleRequest> updateValidator,
        IValidator<BulkPublishArticlesRequest> bulkPublishValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _embeddings = embeddings;
        _embeddingCatalog = embeddingCatalog;
        _activeProvider = activeProvider;
        _aiOptions = aiOptions.Value;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _bulkPublishValidator = bulkPublishValidator;
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
            if (!Enum.TryParse<KnowledgeBaseArticleStatus>(status, ignoreCase: true, out var parsedStatus))
                throw Invalid("status", $"Status must be one of: {string.Join(", ", Enum.GetNames<KnowledgeBaseArticleStatus>())}.");

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

        var article = new KnowledgeBaseArticle
        {
            Title = request.Title.Trim(),
            Category = request.Category?.Trim(),
            Content = request.Content,
            SourceType = Enum.Parse<KnowledgeBaseSourceType>(request.SourceType, ignoreCase: true),
            Status = KnowledgeBaseArticleStatus.Draft,
            Version = 1
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
        article.Category = request.Category?.Trim();

        // Only bump Version (and so only invalidate existing chunks as stale) if Content actually
        // changed - editing just the Category/Title should not force a re-embed.
        if (request.Content != article.Content)
        {
            article.Content = request.Content;
            article.Version++;
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

    public async Task<KnowledgeBaseArticleDto> PublishAsync(Guid id, Guid approvedByUserId, string? provider = null, CancellationToken cancellationToken = default)
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
            await MarkChunkedAndPublishedAsync(article, approvedByUserId, cancellationToken);
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
        if (article.Status != KnowledgeBaseArticleStatus.Published)
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
            .Where(a => a.Status == KnowledgeBaseArticleStatus.Published)
            .ToListAsync(cancellationToken);

        foreach (var article in published)
        {
            var isStale = !await _context.KnowledgeBaseChunks
                .AnyAsync(c => c.ArticleId == article.Id && c.EmbeddedFromArticleVersion == article.Version, cancellationToken);

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
                where a.Status == KnowledgeBaseArticleStatus.Published && c.Embedding != null
                    && (!modelFilterApplies || _context.KnowledgeBaseArticleModelPublications.Any(p => p.ArticleId == a.Id && p.Provider == parsedProvider))
                select c)
            .ToListAsync(cancellationToken);

        var scored = candidates
            .Select(c => new RetrievedChunk(c.Id, c.ArticleId, c.ChunkText, CosineSimilarity(queryEmbedding, DeserializeEmbedding(c.Embedding!))))
            .Where(r => r.RelevanceScore >= _aiOptions.MinRelevanceScore)
            .OrderByDescending(r => r.RelevanceScore)
            .Take(_aiOptions.KnowledgeBaseTopN)
            .ToList();

        return scored;
    }

    /// <summary>Chunks/embeds (via ReembedAsync) and sets Status = Published - shared by PublishAsync
    /// and PublishToModelAsync's "first time this article goes live" branch.</summary>
    private async Task MarkChunkedAndPublishedAsync(KnowledgeBaseArticle article, Guid approvedByUserId, CancellationToken cancellationToken)
    {
        await ReembedAsync(article, cancellationToken);

        article.Status = KnowledgeBaseArticleStatus.Published;
        article.ApprovedBy = approvedByUserId;
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
        var isStale = chunks.Count > 0 && !chunks.Any(c => c.EmbeddedFromArticleVersion == article.Version);

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
                var chunk = new KnowledgeBaseChunk
                {
                    ArticleId = article.Id,
                    ChunkIndex = index++,
                    ChunkText = text,
                    TokenCount = text.Length / 4,
                    EmbeddedFromArticleVersion = article.Version
                };
                _context.KnowledgeBaseChunks.Add(chunk);
                chunks.Add(chunk);
            }
            article.Status = KnowledgeBaseArticleStatus.Published;
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
            var chunk = new KnowledgeBaseChunk
            {
                ArticleId = article.Id,
                ChunkIndex = index++,
                ChunkText = text,
                // Rough estimate (~4 chars/token in English), not a real tokenizer - good enough for
                // the "roughly how big is this chunk" signal this column exists for.
                TokenCount = text.Length / 4,
                EmbeddedFromArticleVersion = article.Version
            };

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
