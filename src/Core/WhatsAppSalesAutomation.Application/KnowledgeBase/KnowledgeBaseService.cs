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
    private readonly AiOptions _aiOptions;
    private readonly IValidator<CreateKnowledgeBaseArticleRequest> _createValidator;
    private readonly IValidator<UpdateKnowledgeBaseArticleRequest> _updateValidator;
    private readonly IValidator<BulkPublishArticlesRequest> _bulkPublishValidator;

    public KnowledgeBaseService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IEmbeddingService embeddings,
        IOptions<AiOptions> aiOptions,
        IValidator<CreateKnowledgeBaseArticleRequest> createValidator,
        IValidator<UpdateKnowledgeBaseArticleRequest> updateValidator,
        IValidator<BulkPublishArticlesRequest> bulkPublishValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _embeddings = embeddings;
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
            select new { Article = a, ChunkCount = _context.KnowledgeBaseChunks.Count(c => c.ArticleId == a.Id) };

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

        var items = rows.Select(x => x.Article.ToDto(x.ChunkCount)).ToList();

        return new PagedResult<KnowledgeBaseArticleDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task<KnowledgeBaseArticleDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var article = await FindOrThrowAsync(id, cancellationToken);
        var chunkCount = await _context.KnowledgeBaseChunks.CountAsync(c => c.ArticleId == id, cancellationToken);

        return article.ToDto(chunkCount);
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

    public async Task<KnowledgeBaseArticleDto> PublishAsync(Guid id, Guid approvedByUserId, CancellationToken cancellationToken = default)
    {
        var article = await FindOrThrowAsync(id, cancellationToken);

        await ReembedAsync(article, cancellationToken);

        article.Status = KnowledgeBaseArticleStatus.Published;
        article.ApprovedBy = approvedByUserId;

        await _context.SaveChangesAsync(cancellationToken);

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
                await PublishAsync(id, approvedByUserId, cancellationToken);
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

        // Loads every chunk belonging to a Published article into memory to score it - the
        // in-application cosine similarity approach this phase deliberately chose over a database-side
        // vector search (see KnowledgeBaseChunk's doc comment). Fine for the corpus sizes a single
        // business's knowledge base realistically has in this phase; revisit if that stops being true.
        var candidates = await (
                from c in _context.KnowledgeBaseChunks
                join a in _context.KnowledgeBaseArticles on c.ArticleId equals a.Id
                where a.Status == KnowledgeBaseArticleStatus.Published && c.Embedding != null
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

    /// <summary>Chunks Content, embeds each chunk, and replaces this article's previous chunks -
    /// shared by PublishAsync (one article) and ReindexAsync (every stale Published article).</summary>
    private async Task ReembedAsync(KnowledgeBaseArticle article, CancellationToken cancellationToken)
    {
        var existingChunks = await _context.KnowledgeBaseChunks.Where(c => c.ArticleId == article.Id).ToListAsync(cancellationToken);
        _context.KnowledgeBaseChunks.RemoveRange(existingChunks);

        var chunkTexts = ChunkContent(article.Content);
        var index = 0;
        foreach (var text in chunkTexts)
        {
            var embedding = await _embeddings.GetEmbeddingAsync(text, cancellationToken);
            _context.KnowledgeBaseChunks.Add(new KnowledgeBaseChunk
            {
                ArticleId = article.Id,
                ChunkIndex = index++,
                ChunkText = text,
                Embedding = JsonSerializer.Serialize(embedding),
                // Rough estimate (~4 chars/token in English), not a real tokenizer - good enough for
                // the "roughly how big is this chunk" signal this column exists for.
                TokenCount = text.Length / 4,
                EmbeddedFromArticleVersion = article.Version
            });
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
}
