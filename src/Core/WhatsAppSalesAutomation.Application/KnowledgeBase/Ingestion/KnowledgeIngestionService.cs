using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>Hands a job to whatever runs background work. An interface so the Application layer does
/// not reference Hangfire; the implementation must also put the job's tenant (or platform) scope in
/// place before calling <see cref="IKnowledgeIngestionService.RunAsync"/>.</summary>
public interface IKnowledgeIngestionQueue
{
    void Enqueue(Guid jobId, Guid? tenantId, bool securityReviewed);
}

public interface IKnowledgeIngestionService
{
    /// <summary>Creates (or returns) the indexing job for the article's CURRENT version and queues it.
    /// Idempotent per (article, version): embedding costs money, so a double-click returns the
    /// existing job. A Failed job is re-queued in place and resumes; a Rejected one is returned as-is,
    /// since running it again would change nothing.</summary>
    Task<KnowledgeIngestionJob> QueueIndexingAsync(Guid articleId, bool securityReviewed = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes the article's current version NOW, inside the calling request, and returns the finished
    /// job. Unlike <see cref="QueueIndexingAsync"/> it always re-runs, even over a Completed job: this
    /// is the explicit "publish" action, which has always re-embedded, and a user pressing it after a
    /// provider change expects that to take effect.
    ///
    /// Refuses (Conflict) only when the same version is genuinely mid-run - a state older than
    /// <see cref="StaleRunMinutes"/> is treated as a crashed run and overridden, so a job that died
    /// cannot block publishing forever.
    /// </summary>
    Task<KnowledgeIngestionJob> IndexNowAsync(Guid articleId, bool securityReviewed = false, CancellationToken cancellationToken = default);

    /// <summary>The most recent job for the article's highest version, or null if it was never indexed
    /// by this pipeline. What an admin reads to learn why an article is not live, or what to check.</summary>
    Task<KnowledgeIngestionJobDto?> GetLatestJobAsync(Guid articleId, CancellationToken cancellationToken = default);

    /// <summary>The pipeline itself. Never throws for a content or provider problem - those become the
    /// job's state - so a background-job runner does not retry work whose failure is already recorded.</summary>
    Task RunAsync(Guid jobId, bool securityReviewed = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Chunk, embed, and atomically swap an article's index (§G.6).
///
/// The invariant the whole design serves: retrieval must never see a half-embedded article. So new
/// chunks are built INACTIVE, invisible to every query, and only when every one of them has a vector
/// does a single SaveChanges - one database transaction - switch the old set off and the new set on.
/// A failure at any earlier point leaves the previous index untouched and serving.
///
/// Resumable: staged chunks and the last completed index are kept on failure, so a provider outage
/// at chunk 480 of 500 costs the remaining 20 on retry, not all 500.
/// </summary>
/// <summary>What an admin sees about an article's indexing. The security findings are parsed rather than
/// passed as JSON so a client can show the sentences that tripped the scan.</summary>
public sealed record KnowledgeIngestionJobDto(
    Guid Id,
    Guid ArticleId,
    int ArticleVersionNumber,
    string State,
    string? ReasonCode,
    string? Detail,
    bool RequiresSecurityReview,
    IReadOnlyList<InjectionFinding> SecurityFindings,
    int ChunkCount,
    string? VerificationNotes,
    int Attempts,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed class KnowledgeIngestionService : IKnowledgeIngestionService
{
    /// <summary>A job sitting in a running state for longer than this is assumed to have crashed.</summary>
    public const int StaleRunMinutes = 15;

    private static readonly HashSet<KnowledgeIngestionState> RunningStates = new()
    {
        KnowledgeIngestionState.Validating, KnowledgeIngestionState.Extracting, KnowledgeIngestionState.Cleaning,
        KnowledgeIngestionState.Enriching, KnowledgeIngestionState.Chunking, KnowledgeIngestionState.Embedding,
        KnowledgeIngestionState.Indexing, KnowledgeIngestionState.Verifying
    };

    private readonly IApplicationDbContext _context;
    private readonly IEmbeddingService _embeddings;
    private readonly IEmbeddingProviderCatalog _catalog;
    private readonly IVectorStore _vectors;
    private readonly StructureAwareChunker _chunker;
    private readonly EmbeddingBatcher _batcher;
    private readonly ITokenCounter _tokens;
    private readonly IKnowledgeIngestionQueue _queue;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<KnowledgeIngestionService> _logger;
    private readonly IKnowledgeRetrievalService? _retrieval;
    private readonly IPlatformEmbeddingService? _platformEmbedder;

    /// <summary>The embedder for the run in progress: the platform's for a GLOBAL article, the ambient
    /// tenant's otherwise. Chosen once at the start of each run from the ARTICLE, never from whoever
    /// happens to be in scope - a platform article published by a SuperAdmin, re-indexed by a job, or
    /// simulated as some tenant must all land in the same, platform-owned vector space.</summary>
    private IEmbeddingService _embedder;

    public KnowledgeIngestionService(
        IApplicationDbContext context,
        IEmbeddingService embeddings,
        IEmbeddingProviderCatalog catalog,
        IVectorStore vectors,
        StructureAwareChunker chunker,
        EmbeddingBatcher batcher,
        ITokenCounter tokens,
        IKnowledgeIngestionQueue queue,
        IDateTimeProvider clock,
        ILogger<KnowledgeIngestionService> logger,
        // Optional: the smoke check needs retrieval, but indexing must not depend on it. A null here
        // just skips that one check.
        IKnowledgeRetrievalService? retrieval = null,
        IPlatformEmbeddingService? platformEmbedder = null)
    {
        _context = context;
        _embeddings = embeddings;
        _catalog = catalog;
        _vectors = vectors;
        _chunker = chunker;
        _batcher = batcher;
        _tokens = tokens;
        _queue = queue;
        _clock = clock;
        _logger = logger;
        _retrieval = retrieval;
        _platformEmbedder = platformEmbedder;
        _embedder = embeddings;
    }

    // ── Queueing ─────────────────────────────────────────────────────────────────────────

    public async Task<KnowledgeIngestionJob> QueueIndexingAsync(
        Guid articleId, bool securityReviewed = false, CancellationToken cancellationToken = default)
    {
        var article = await _context.KnowledgeBaseArticles.FirstOrDefaultAsync(a => a.Id == articleId, cancellationToken)
            ?? throw new NotFoundException(nameof(KnowledgeBaseArticle), articleId);

        if (article.Status is not (KnowledgeArticleStatus.Approved or KnowledgeArticleStatus.Published))
        {
            // Draft and InReview are not chunked, by design: unreviewed text must not even sit in the
            // index, let alone be retrievable.
            throw new ConflictException($"Only Approved or Published articles are indexed; this one is {article.Status}.");
        }

        var job = await _context.KnowledgeIngestionJobs
            .FirstOrDefaultAsync(j => j.ArticleId == articleId && j.ArticleVersionNumber == article.VersionNumber, cancellationToken);

        if (job is not null)
        {
            var requeue = job.State == KnowledgeIngestionState.Failed
                          || (job.State == KnowledgeIngestionState.AwaitingApproval && securityReviewed);

            if (!requeue)
                return job;   // Completed, running, queued, rejected: paying again would change nothing.

            job.State = KnowledgeIngestionState.Queued;
            job.ReasonCode = null;
            job.Detail = null;
        }
        else
        {
            job = new KnowledgeIngestionJob
            {
                TenantId = article.TenantId,
                ArticleId = article.Id,
                ArticleVersionNumber = article.VersionNumber,
                State = KnowledgeIngestionState.Queued
            };
            _context.KnowledgeIngestionJobs.Add(job);
        }

        await _context.SaveChangesAsync(cancellationToken);
        _queue.Enqueue(job.Id, article.TenantId, securityReviewed);
        return job;
    }

    public async Task<KnowledgeIngestionJob> IndexNowAsync(
        Guid articleId, bool securityReviewed = false, CancellationToken cancellationToken = default)
    {
        var article = await _context.KnowledgeBaseArticles.FirstOrDefaultAsync(a => a.Id == articleId, cancellationToken)
            ?? throw new NotFoundException(nameof(KnowledgeBaseArticle), articleId);

        if (article.Status is not (KnowledgeArticleStatus.Approved or KnowledgeArticleStatus.Published))
            throw new ConflictException($"Only Approved or Published articles are indexed; this one is {article.Status}.");

        var job = await _context.KnowledgeIngestionJobs
            .FirstOrDefaultAsync(j => j.ArticleId == articleId && j.ArticleVersionNumber == article.VersionNumber, cancellationToken);

        if (job is null)
        {
            job = new KnowledgeIngestionJob
            {
                TenantId = article.TenantId,
                ArticleId = article.Id,
                ArticleVersionNumber = article.VersionNumber
            };
            _context.KnowledgeIngestionJobs.Add(job);
        }
        else
        {
            var lastTouched = job.UpdatedAt ?? job.CreatedAt;
            if (RunningStates.Contains(job.State) && _clock.UtcNow - lastTouched < TimeSpan.FromMinutes(StaleRunMinutes))
                throw new ConflictException("This article is already being indexed. Try again in a moment.");

            // A fresh run over the same version. LastCompletedChunkIndex is left alone: a Failed job
            // resumes from it, and for a Completed one it is only informational.
            job.State = KnowledgeIngestionState.Queued;
            job.ReasonCode = null;
            job.Detail = null;
            job.RequiresSecurityReview = false;
            job.SecurityFindingsJson = null;
            job.VerificationNotes = null;
            job.CompletedAt = null;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await RunAsync(job.Id, securityReviewed, cancellationToken);

        // RunAsync mutates this same tracked instance, so it already reflects the outcome.
        return job;
    }

    public async Task<KnowledgeIngestionJobDto?> GetLatestJobAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var job = await _context.KnowledgeIngestionJobs.AsNoTracking()
            .Where(j => j.ArticleId == articleId)
            .OrderByDescending(j => j.ArticleVersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
            return null;

        IReadOnlyList<InjectionFinding> findings = Array.Empty<InjectionFinding>();
        if (!string.IsNullOrWhiteSpace(job.SecurityFindingsJson))
        {
            try
            {
                findings = JsonSerializer.Deserialize<List<InjectionFinding>>(job.SecurityFindingsJson) ?? new List<InjectionFinding>();
            }
            catch (JsonException)
            {
                // Findings are diagnostic; unreadable ones must not break the status read.
            }
        }

        return new KnowledgeIngestionJobDto(
            job.Id, job.ArticleId, job.ArticleVersionNumber, job.State.ToString(), job.ReasonCode, job.Detail,
            job.RequiresSecurityReview, findings, job.ChunkCount, job.VerificationNotes, job.Attempts,
            job.StartedAt, job.CompletedAt);
    }

    // ── The pipeline ─────────────────────────────────────────────────────────────────────

    public async Task RunAsync(Guid jobId, bool securityReviewed = false, CancellationToken cancellationToken = default)
    {
        var job = await _context.KnowledgeIngestionJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            _logger.LogWarning("Ingestion job {JobId} not found; nothing to do.", jobId);
            return;
        }

        try
        {
            await RunPipelineAsync(job, securityReviewed, cancellationToken);
        }
        catch (EmbeddingProviderUnavailableException ex)
        {
            // Resumable: staged chunks are deliberately left in place.
            await FinishAsync(job, KnowledgeIngestionState.Failed, "EmbeddingProviderFailed", ex.Message, cancellationToken);
            _logger.LogWarning(ex, "Ingestion job {JobId} stopped: embedding provider unavailable. Resumable.", jobId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await FinishAsync(job, KnowledgeIngestionState.Failed, "Unexpected", ex.Message, cancellationToken);
            _logger.LogError(ex, "Ingestion job {JobId} failed unexpectedly.", jobId);
        }
    }

    private async Task RunPipelineAsync(KnowledgeIngestionJob job, bool securityReviewed, CancellationToken ct)
    {
        job.Attempts++;
        job.StartedAt ??= _clock.UtcNow;
        await SetStateAsync(job, KnowledgeIngestionState.Validating, ct);

        var article = await _context.KnowledgeBaseArticles.FirstOrDefaultAsync(a => a.Id == job.ArticleId, ct);
        _embedder = article is { TenantId: null } && _platformEmbedder is not null ? _platformEmbedder : _embeddings;
        if (article is null)
        {
            await FinishAsync(job, KnowledgeIngestionState.Rejected, "ArticleGone", "The article no longer exists.", ct);
            return;
        }

        if (article.VersionNumber != job.ArticleVersionNumber)
        {
            // The article was edited after this job was queued. Indexing the old version would
            // publish text that is no longer the article's text.
            await FinishAsync(job, KnowledgeIngestionState.Rejected, "SupersededVersion",
                $"Job is for version {job.ArticleVersionNumber}; the article is now at {article.VersionNumber}.", ct);
            return;
        }

        if (article.Status is not (KnowledgeArticleStatus.Approved or KnowledgeArticleStatus.Published))
        {
            await FinishAsync(job, KnowledgeIngestionState.Rejected, "NotIndexable", $"Article status is {article.Status}.", ct);
            return;
        }

        // ── Cleaning and the injection gate ──
        await SetStateAsync(job, KnowledgeIngestionState.Cleaning, ct);
        var sanitized = ContentSanitizer.Sanitize(article.Content);

        if (sanitized.IsBlocked)
        {
            job.SecurityFindingsJson = JsonSerializer.Serialize(sanitized.Findings);
            await FinishAsync(job, KnowledgeIngestionState.Rejected, "InjectionBlocked",
                "The content contains a tool-invocation or role-assumption pattern.", ct);
            return;
        }

        if (sanitized.RequiresSecurityReview && !securityReviewed)
        {
            job.RequiresSecurityReview = true;
            job.SecurityFindingsJson = JsonSerializer.Serialize(sanitized.Findings);
            await SetStateAsync(job, KnowledgeIngestionState.AwaitingApproval, ct);
            return;
        }

        // ── Chunking ──
        await SetStateAsync(job, KnowledgeIngestionState.Chunking, ct);
        var drafts = _chunker.Chunk(article, sanitized.Content);

        if (drafts.Count == 0)
        {
            await FinishAsync(job, KnowledgeIngestionState.Rejected, "NoContent", "Nothing was left to chunk after cleaning.", ct);
            return;
        }

        var metrics = StructureAwareChunker.Measure(drafts, ChunkingParameters.For(article.SourceType));
        job.MetricsJson = JsonSerializer.Serialize(metrics);
        job.ChunkCount = drafts.Count;
        job.EmbeddingProvider = _embedder.ProviderName;
        job.EmbeddingModel = _embedder.ModelName;

        // ── Staging (inactive) ──
        var staged = await StageChunksAsync(article, drafts, ct);

        // ── Embedding ──
        await SetStateAsync(job, KnowledgeIngestionState.Embedding, ct);
        await EmbedPendingAsync(job, article, staged, ct);

        // ── Atomic swap ──
        await SetStateAsync(job, KnowledgeIngestionState.Indexing, ct);
        await SwapAsync(article, staged, ct);

        // ── Verification ──
        await SetStateAsync(job, KnowledgeIngestionState.Verifying, ct);
        job.VerificationNotes = await VerifyAsync(article, ct);

        job.CompletedAt = _clock.UtcNow;
        await FinishAsync(job, KnowledgeIngestionState.Completed, null, null, ct);
    }

    /// <summary>
    /// Builds the inactive chunk set for this article version, reusing chunks a previous, interrupted
    /// run already embedded. A staged chunk is reusable only if it is identical in every way that
    /// matters to its vector: same index, same EmbeddingInput, same provider and model. Anything less
    /// would reuse a vector that describes different text.
    /// </summary>
    private async Task<List<KnowledgeBaseChunk>> StageChunksAsync(
        KnowledgeBaseArticle article, IReadOnlyList<ChunkDraft> drafts, CancellationToken ct)
    {
        var existingStaged = await _context.KnowledgeBaseChunks
            .Where(c => c.ArticleId == article.Id && !c.IsActive && c.EmbeddedFromArticleVersion == article.VersionNumber)
            .ToListAsync(ct);

        var byIndex = existingStaged.GroupBy(c => c.ChunkIndex).ToDictionary(g => g.Key, g => g.First());

        // A fresh chunker run mints new group ids, so a reused chunk's group id has to win, and the
        // rest of its group has to adopt it - otherwise one atomic group would be split across two ids
        // and the "pull in the whole group" rule would pull in only part of it.
        var groupRemap = new Dictionary<Guid, Guid?>();
        var reused = new HashSet<Guid>();
        var result = new List<KnowledgeBaseChunk>();

        foreach (var draft in drafts)
        {
            if (byIndex.TryGetValue(draft.Index, out var candidate)
                && candidate.Embedding is not null
                && candidate.EmbeddingInput == draft.EmbeddingInput
                && candidate.EmbeddingProvider == _embedder.ProviderName
                && candidate.EmbeddingModel == _embedder.ModelName)
            {
                reused.Add(candidate.Id);
                if (draft.AtomicGroupId is { } fresh)
                    groupRemap[fresh] = candidate.AtomicGroupId;

                result.Add(candidate);
            }
        }

        var staleStaged = existingStaged.Where(c => !reused.Contains(c.Id)).ToList();
        if (staleStaged.Count > 0)
        {
            var staleIds = staleStaged.Select(c => c.Id).ToList();
            _context.KnowledgeBaseChunkEmbeddings.RemoveRange(
                await _context.KnowledgeBaseChunkEmbeddings.Where(e => staleIds.Contains(e.ChunkId)).ToListAsync(ct));
            _context.KnowledgeBaseChunks.RemoveRange(staleStaged);
        }

        var reusedIndexes = result.Select(c => c.ChunkIndex).ToHashSet();
        foreach (var draft in drafts.Where(d => !reusedIndexes.Contains(d.Index)))
        {
            var groupId = draft.AtomicGroupId is { } g && groupRemap.TryGetValue(g, out var mapped) && mapped is not null
                ? mapped
                : draft.AtomicGroupId;

            var chunk = ToEntity(article, draft, groupId);
            _context.KnowledgeBaseChunks.Add(chunk);
            result.Add(chunk);
        }

        await _context.SaveChangesAsync(ct);
        return result.OrderBy(c => c.ChunkIndex).ToList();
    }

    private async Task EmbedPendingAsync(
        KnowledgeIngestionJob job, KnowledgeBaseArticle article, List<KnowledgeBaseChunk> chunks, CancellationToken ct)
    {
        var pending = chunks.Where(c => c.Embedding is null).ToList();
        if (pending.Count == 0)
            return;

        var batches = EmbeddingBatcher.PlanBatches(pending.Select(c => _tokens.Count(c.EmbeddingInput)).ToList());
        // A platform article is embedded in ONE space, the platform's. The "every other available
        // provider" fan-out exists so a tenant on a different provider still has vectors to search; for
        // platform content that job is done by embedding the QUESTION in the platform's space instead,
        // so fanning out here would only cost the platform money for vectors nobody reads.
        var others = article.TenantId is null
            ? new List<IEmbeddingService>()
            : _catalog.AllProviders.Where(p => p.IsAvailable && p.ProviderName != _embedder.ProviderName).ToList();

        foreach (var batch in batches)
        {
            var members = batch.Select(i => pending[i]).ToList();
            var texts = members.Select(c => c.EmbeddingInput).ToList();

            var vectors = await _batcher.EmbedBatchAsync(_embedder, texts, ct);

            // The store writes the active provider's vector to wherever retrieval will read it.
            await _vectors.UpsertAsync(
                members.Select((c, i) => new ChunkVector(c.Id, vectors[i], _embedder.ProviderName, _embedder.ModelName)).ToList(), ct);

            for (var i = 0; i < members.Count; i++)
            {
                _context.KnowledgeBaseChunkEmbeddings.Add(NewEmbeddingRow(members[i], _embedder, vectors[i]));
            }

            // Every OTHER available provider is embedded too, so a tenant whose active provider
            // differs from this one still has vectors in its own space to search. Best effort: the
            // active provider is what this job is FOR, and another provider having a bad day must not
            // fail an index that is otherwise complete.
            foreach (var other in others)
            {
                try
                {
                    var otherVectors = await _batcher.EmbedBatchAsync(other, texts, ct);
                    for (var i = 0; i < members.Count; i++)
                        _context.KnowledgeBaseChunkEmbeddings.Add(NewEmbeddingRow(members[i], other, otherVectors[i]));
                }
                catch (EmbeddingProviderUnavailableException ex)
                {
                    _logger.LogWarning(ex, "Secondary embedding provider {Provider} skipped for job {JobId}.", other.ProviderName, job.Id);
                }
            }

            job.LastCompletedChunkIndex = members.Max(c => c.ChunkIndex);
            await _context.SaveChangesAsync(ct);
        }
    }

    /// <summary>The single transaction. Old active chunks are removed and staged ones activated in one
    /// SaveChanges, which EF wraps in one database transaction - so a concurrent reader sees the old
    /// index or the new one, never a mixture and never neither.</summary>
    private async Task SwapAsync(KnowledgeBaseArticle article, List<KnowledgeBaseChunk> staged, CancellationToken ct)
    {
        var stagedIds = staged.Select(c => c.Id).ToHashSet();

        var old = await _context.KnowledgeBaseChunks
            .Where(c => c.ArticleId == article.Id && !stagedIds.Contains(c.Id))
            .ToListAsync(ct);

        if (old.Count > 0)
        {
            var oldIds = old.Select(c => c.Id).ToList();
            _context.KnowledgeBaseChunkEmbeddings.RemoveRange(
                await _context.KnowledgeBaseChunkEmbeddings.Where(e => oldIds.Contains(e.ChunkId)).ToListAsync(ct));
            _context.KnowledgeBaseChunks.RemoveRange(old);
        }

        foreach (var chunk in staged)
        {
            chunk.IsActive = true;
            chunk.ArticleStatus = article.Status;
            chunk.IsCurrentArticleVersion = article.IsCurrentVersion;
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>Post-index checks (§G.7). Findings are warnings for a human, not failures - the index
    /// is built and correct, and whether a duplicate matters is an editorial decision.</summary>
    private async Task<string?> VerifyAsync(KnowledgeBaseArticle article, CancellationToken ct)
    {
        var notes = new List<string>();

        var duplicate = await _context.KnowledgeBaseArticles
            .Where(a => a.Id != article.Id && a.ContentHash == article.ContentHash && a.IsCurrentVersion)
            .Select(a => new { a.Id, a.ArticleKey })
            .FirstOrDefaultAsync(ct);

        if (duplicate is not null)
            notes.Add($"Duplicate content: identical to article '{duplicate.ArticleKey}' ({duplicate.Id}).");

        var smoke = await SmokeRetrievalAsync(article, ct);
        if (smoke is not null)
            notes.Add(smoke);

        // A platform article has no tenant in scope while it is indexed, and every provider's
        // credentials are resolved per tenant - so a GLOBAL article is embedded by whatever a
        // tenantless scope can reach, which today is only the Simulated stand-in. Its vectors are then
        // in a space no real tenant's queries share, and it will silently never be retrieved for them.
        // Not a failure (local development legitimately runs Simulated everywhere), but it must never
        // be quiet: this is the note an admin reads to find out why platform articles do not surface.
        if (article.TenantId is null && _embedder.ProviderName == "Simulated")
        {
            notes.Add("GLOBAL article embedded with the Simulated provider: tenants using a real embedding " +
                      "provider will not retrieve it until platform-level embedding credentials exist.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// G.7: ask retrieval for the article by its own title. If it is not in the top three, the
    /// chunking or the embedding is wrong - the article was indexed and will still never be found.
    ///
    /// Only for Published articles, because only those are retrievable at all; an Approved article
    /// is indexed warm and correctly invisible. Asks with the article's own country, language and
    /// version bounds, so an India-only article is not reported missing merely because the check
    /// forgot to be in India. Returns a warning, never a failure: the index is built, and whether a
    /// weak title match matters is an editorial call.
    /// </summary>
    private async Task<string?> SmokeRetrievalAsync(KnowledgeBaseArticle article, CancellationToken ct)
    {
        if (_retrieval is null || article.Status != KnowledgeArticleStatus.Published)
            return null;

        try
        {
            var result = await _retrieval.RetrieveAsync(
                new KnowledgeRetrievalRequest(
                    article.Title, Array.Empty<string>(), null, article.ProductModule,
                    article.CountryCode, article.LanguageCode, article.AppliesToVersionMin, BypassCache: true),
                ct);

            var topThree = result.Evidence.Select(e => e.ArticleId).Distinct().Take(3).ToList();
            return topThree.Contains(article.Id)
                ? null
                : "Smoke retrieval: searching for this article by its own title did not return it in the top 3. " +
                  "Check its chunking and embedding.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken retrieval stack must not turn a finished index into a failed job.
            _logger.LogWarning(ex, "Smoke retrieval check could not run for article {ArticleId}.", article.Id);
            return "Smoke retrieval could not run: " + ex.Message;
        }
    }

    private KnowledgeBaseChunk ToEntity(KnowledgeBaseArticle article, ChunkDraft draft, Guid? groupId) => new()
    {
        TenantId = article.TenantId,
        ArticleId = article.Id,
        ChunkIndex = draft.Index,
        ContextHeader = draft.ContextHeader,
        ChunkText = draft.Body,
        EmbeddingInput = draft.EmbeddingInput,
        // Keywords twice: that repetition IS the 2x field boost. See KnowledgeBaseChunk.SearchText.
        SearchText = string.Join(" ", new[] { draft.ContextHeader, draft.Body, article.Keywords, article.Keywords }
            .Where(p => !string.IsNullOrWhiteSpace(p))),
        TokenCount = draft.TokenCount,
        AtomicGroupId = groupId,
        AtomicGroupSequence = draft.AtomicGroupSequence,
        AtomicGroupTotal = draft.AtomicGroupTotal,
        EmbeddedFromArticleVersion = article.VersionNumber,
        EmbeddingProvider = null,
        IsActive = false,   // staged: invisible to every query until the swap
        AuthorityRank = article.AuthorityRank,
        ProductModule = article.ProductModule,
        SourceType = article.SourceType,
        LanguageCode = article.LanguageCode,
        CountryCode = article.CountryCode,
        VersionMinNumeric = SemanticVersion.ToNumeric(article.AppliesToVersionMin),
        VersionMaxNumeric = SemanticVersion.ToNumeric(article.AppliesToVersionMax),
        ArticleStatus = article.Status,
        EffectiveFrom = article.EffectiveFrom,
        EffectiveTo = article.EffectiveTo,
        IsCurrentArticleVersion = article.IsCurrentVersion
    };

    private static KnowledgeBaseChunkEmbedding NewEmbeddingRow(KnowledgeBaseChunk chunk, IEmbeddingService provider, float[] vector) => new()
    {
        TenantId = chunk.TenantId,
        ChunkId = chunk.Id,
        Provider = provider.ProviderName,
        Model = provider.ModelName,
        Embedding = JsonSerializer.Serialize(vector)
    };

    private async Task SetStateAsync(KnowledgeIngestionJob job, KnowledgeIngestionState state, CancellationToken ct)
    {
        job.State = state;
        await _context.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(
        KnowledgeIngestionJob job, KnowledgeIngestionState state, string? reasonCode, string? detail, CancellationToken ct)
    {
        job.State = state;
        job.ReasonCode = reasonCode;
        job.Detail = detail is { Length: > 2000 } ? detail[..2000] : detail;
        await _context.SaveChangesAsync(ct);
    }
}
