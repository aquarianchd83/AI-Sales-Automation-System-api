using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

/// <summary>
/// The hybrid retrieval pipeline (§K): normalize, expand, search by vector and by keyword under one
/// hard filter, fuse by rank, boost by metadata, rerank, then pull in whole atomic groups.
///
/// Two properties matter more than any tuning:
///
/// 1. Everything eligibility-related happens in the stores, in SQL or LINQ, BEFORE candidates exist.
///    Nothing here filters a candidate set after the fact, because a chunk another tenant owns that
///    was fetched and then dropped has already been in this process's memory.
///
/// 2. When the evidence is thin the answer is "not enough", not "the best available". The gate result
///    is part of the output, and callers act on it.
/// </summary>
public sealed class KnowledgeRetrievalService : IKnowledgeRetrievalService
{
    private static readonly TimeSpan EmbeddingTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(60);
    private const int RerankDocumentMaxChars = 4000;

    private readonly IApplicationDbContext _context;
    private readonly IEmbeddingService _embeddings;
    private readonly IPlatformEmbeddingService? _platform;
    private readonly IVectorStore _vectors;
    private readonly IKeywordSearchStore _keywords;
    private readonly IReranker _reranker;
    private readonly IRetrievalCache _cache;
    private readonly ITenantContext _tenant;
    private readonly ITokenCounter _tokens;
    private readonly IDateTimeProvider _clock;
    private readonly SupportRagOptions _options;
    private readonly ILogger<KnowledgeRetrievalService> _logger;

    public KnowledgeRetrievalService(
        IApplicationDbContext context,
        IEmbeddingService embeddings,
        IVectorStore vectors,
        IKeywordSearchStore keywords,
        IReranker reranker,
        IRetrievalCache cache,
        ITenantContext tenant,
        ITokenCounter tokens,
        IDateTimeProvider clock,
        IOptions<SupportRagOptions> options,
        ILogger<KnowledgeRetrievalService> logger,
        // Optional so retrieval still works, tenant-only, where no platform embedder is registered.
        IPlatformEmbeddingService? platform = null)
    {
        _context = context;
        _embeddings = embeddings;
        _vectors = vectors;
        _keywords = keywords;
        _reranker = reranker;
        _cache = cache;
        _tenant = tenant;
        _tokens = tokens;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _platform = platform;
    }

    public async Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // The floors are safety limits, not tuning. Refusing to run beats running with a gate that
        // has been configured into answering from whatever retrieval returned.
        var problems = _options.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException("SupportRag options are invalid: " + string.Join(" ", problems));

        var normalized = QueryNormalizer.Normalize(request.RawQuery, _tokens);
        var module = request.DetectedModule ?? (request.DetectedIntent is { } i ? SupportIntentRules.DefaultModule(i) : null);
        var queries = QueryExpander.Expand(normalized, request.ConversationContext, request.DetectedIntent, module);

        if (queries.Count == 0)
            return Empty(normalized, queries, stopwatch, "EmptyQuery");

        var tenantId = _tenant.TenantId;
        var resultKey = RetrievalCacheKeys.Result(tenantId, Fingerprint(request, normalized, module));
        if (!request.BypassCache && _cache.TryGet<KnowledgeRetrievalResult>(resultKey, out var cached) && cached is not null)
            return cached with { Diagnostics = cached.Diagnostics with { ResultCacheHit = true, RetrievalLatencyMs = (int)stopwatch.ElapsedMilliseconds } };

        var filter = new RetrievalFilter
        {
            TenantId = tenantId,
            NowUtc = _clock.UtcNow,
            TenantCountryCode = request.TenantCountry?.Trim().ToUpperInvariant() is { Length: > 0 } c ? c : null,
            TicketLanguageCode = string.IsNullOrWhiteSpace(request.TicketLanguage) ? "en" : request.TicketLanguage.Trim().ToLowerInvariant(),
            TenantVersionNumeric = SemanticVersion.ToNumeric(request.TenantPlatformVersion),
            EmbeddingProvider = _embeddings.ProviderName,
            EmbeddingModel = _embeddings.ModelName
        };

        // The filter for the platform's own embedding space, or null when there is nothing separate to
        // search: no platform embedder, or the tenant already embeds in the platform's space (in which
        // case the tenant's own leg already covers GLOBAL chunks and a second leg would find the same ones).
        var platformFilter = _platform is not null &&
                             (!string.Equals(_platform.ProviderName, _embeddings.ProviderName, StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(_platform.ModelName, _embeddings.ModelName, StringComparison.OrdinalIgnoreCase))
            ? filter with { EmbeddingProvider = _platform.ProviderName, EmbeddingModel = _platform.ModelName, GlobalOnly = true }
            : null;

        // ── Both legs, for every query ──
        var lists = new List<RankedList>();
        var vectorScores = new Dictionary<Guid, double>();
        var keywordScores = new Dictionary<Guid, double>();
        var embeddingCacheHit = true;
        var (vectorWeight, keywordWeight) = _options.FusionWeightsFor(filter.TicketLanguageCode);

        foreach (var query in queries)
        {
            var (vector, hit) = await EmbedAsync(tenantId, query.Text, cancellationToken);
            embeddingCacheHit &= hit;

            var vectorHits = new List<VectorHit>();

            if (vector.Length > 0)
            {
                vectorHits.AddRange(await _vectors.SearchAsync(vector, filter, _options.VectorTopN, cancellationToken));
            }
            else
            {
                // An empty vector is the provider failing. That leg is skipped, not zero-scored: the
                // keyword leg still runs, and a degraded answer is honest where an invented one is not.
                _logger.LogWarning("No embedding for query {Query}; vector leg skipped.", query.Name);
            }

            // Platform-authored knowledge lives in the PLATFORM's embedding space, which is only the
            // same as the tenant's if they happen to use the same model. When it is not, the question
            // has to be embedded a second time, in the platform's space, to be comparable with it.
            // That second embedding is the platform's cost, not the tenant's.
            if (platformFilter is not null)
            {
                var (platformVector, platformHit) = await EmbedPlatformAsync(tenantId, query.Text, cancellationToken);
                embeddingCacheHit &= platformHit;

                if (platformVector.Length > 0)
                    vectorHits.AddRange(await _vectors.SearchAsync(platformVector, platformFilter, _options.VectorTopN, cancellationToken));
                else
                    _logger.LogWarning("No platform embedding for query {Query}; platform vector leg skipped.", query.Name);
            }

            if (vectorHits.Count > 0)
            {
                // ONE ranked vector list per query, best similarity first, however many spaces fed it.
                // Two separate lists would each count toward the fusion ceiling although a chunk can
                // only ever appear in one of them, deflating every normalized score.
                var ranked = vectorHits.OrderByDescending(h => h.Similarity).ThenBy(h => h.ChunkId).ToList();
                lists.Add(new RankedList("vector:" + query.Name, query.Weight * vectorWeight, ranked.Select(h => h.ChunkId).ToList()));
                foreach (var h in ranked)
                    vectorScores[h.ChunkId] = Math.Max(vectorScores.GetValueOrDefault(h.ChunkId), h.Similarity);
            }

            if (_options.KeywordTopN > 0)
            {
                var hits = await _keywords.SearchAsync(query.Text, filter, _options.KeywordTopN, cancellationToken);
                lists.Add(new RankedList("keyword:" + query.Name, query.Weight * keywordWeight, hits.Select(h => h.ChunkId).ToList()));
                foreach (var h in hits)
                    keywordScores[h.ChunkId] = Math.Max(keywordScores.GetValueOrDefault(h.ChunkId), h.Score);
            }
        }

        var fused = RankFusion.Fuse(lists, _options.RrfK).Take(_options.FusionTopN).ToList();
        var maxPossible = RankFusion.MaxPossible(lists, _options.RrfK);
        var candidateCount = vectorScores.Keys.Union(keywordScores.Keys).Count();

        if (fused.Count == 0)
        {
            return Cache(resultKey, Build(
                normalized, queries, candidateCount, vectorScores.Count, keywordScores.Count, 0, 0,
                Array.Empty<RetrievedEvidence>(), new EvidenceGateResult(false, 0, 0, "NoEvidence"),
                RetrievalMode.FusionOnly, embeddingCacheHit, stopwatch, 0, 0));
        }

        // ── Hydrate the fused set with what boosting and citing need ──
        var ids = fused.Select(f => f.ChunkId).ToList();
        var rows = await (
                from chunk in _context.KnowledgeBaseChunks
                join article in _context.KnowledgeBaseArticles on chunk.ArticleId equals article.Id
                where ids.Contains(chunk.Id)
                select new HydratedRow
                {
                    Id = chunk.Id, ArticleId = chunk.ArticleId, ContextHeader = chunk.ContextHeader, ChunkText = chunk.ChunkText,
                    AuthorityRank = chunk.AuthorityRank, ProductModule = chunk.ProductModule, SourceType = chunk.SourceType,
                    AtomicGroupId = chunk.AtomicGroupId, ArticleKey = article.ArticleKey, VersionNumber = article.VersionNumber,
                    Title = article.Title, PublishedAt = article.PublishedAt
                })
            .ToListAsync(cancellationToken);

        var byId = rows.ToDictionary(r => r.Id);
        var now = _clock.UtcNow;

        // ── Metadata boosting (fusion order preserved as FusionRank for churn) ──
        var candidates = fused
            .Where(f => byId.ContainsKey(f.ChunkId))
            .Select((f, index) =>
            {
                var r = byId[f.ChunkId];
                var normalizedPre = maxPossible <= 0 ? 0 : f.Score / maxPossible;
                var boosted = normalizedPre * MetadataBooster.Multiplier(r.AuthorityRank, r.ProductModule, module, r.PublishedAt, now);
                // Deliberately NOT clamped to 1.0 here. The boosts exist to separate candidates, and a
                // cap flattens exactly that: two strong chunks that both exceed it become an exact tie,
                // broken by chunk id - i.e. at random. It is clamped only where a bounded value is
                // needed, in the rerank blend and the gate, below.
                return new Candidate(r.Id, index + 1, f.Score, boosted);
            })
            .OrderByDescending(c => c.PreRank)
            .ToList();

        // ── Rerank, or fall back to the stricter fusion-only mode ──
        var rerankQuery = queries.FirstOrDefault(q => q.Name == "Q2-canonical")?.Text ?? queries[0].Text;
        var documents = candidates.Select(c =>
        {
            var r = byId[c.ChunkId];
            var text = r.ContextHeader + "\n" + r.ChunkText;
            return text.Length > RerankDocumentMaxChars ? text[..RerankDocumentMaxChars] : text;
        }).ToList();

        var rerankScores = await _reranker.RerankAsync(rerankQuery, documents, cancellationToken);
        var mode = rerankScores is null ? RetrievalMode.FusionOnly : RetrievalMode.Reranked;

        var scored = candidates.Select((c, i) =>
        {
            var rerank = rerankScores?[i] ?? 0;
            var final = rerankScores is null ? c.PreRank : MetadataBooster.Final(rerank, Math.Min(1.0, c.PreRank));
            return c with { Rerank = rerank, Final = final };
        }).OrderByDescending(c => c.Final).ThenByDescending(c => byId[c.ChunkId].AuthorityRank).ThenBy(c => c.ChunkId).ToList();

        var selected = scored.Take(_options.RerankTopN).ToList();

        // ── The gate looks at evidence strength, not at the blended ordering score ──
        var gate = EvidenceGate.Evaluate(
            selected.Select(c => (Score: mode == RetrievalMode.Reranked ? c.Rerank : Math.Min(1.0, c.PreRank), byId[c.ChunkId].AuthorityRank))
                .OrderByDescending(x => x.Score).ToList(),
            _options, mode);

        var evidence = selected.Select((c, i) => ToEvidence(byId[c.ChunkId], c, i + 1, vectorScores, keywordScores, isExpansion: false)).ToList();

        // ── Atomic groups: any chunk of a group brings the whole group ──
        evidence = await ExpandGroupsAsync(evidence, cancellationToken);

        var churn = selected.Count == 0 ? 0 : selected.Select((c, i) => Math.Abs(c.FusionRank - (i + 1))).Average();
        var spread = selected.Count > 1 ? selected[0].Final - selected[1].Final : 0;

        return Cache(resultKey, Build(
            normalized, queries, candidateCount, vectorScores.Count, keywordScores.Count, fused.Count, selected.Count,
            evidence, gate, mode, embeddingCacheHit, stopwatch, churn, spread));
    }

    // ── Group expansion ──────────────────────────────────────────────────────────────────

    private async Task<List<RetrievedEvidence>> ExpandGroupsAsync(
        List<RetrievedEvidence> evidence, CancellationToken cancellationToken)
    {
        var groupIds = evidence.Where(e => e.AtomicGroupId is not null).Select(e => e.AtomicGroupId!.Value).Distinct().ToList();
        if (groupIds.Count == 0)
            return evidence;

        var have = evidence.Select(e => e.ChunkId).ToHashSet();

        // Only chunks that are themselves live: a group member left behind by a half-finished reindex
        // must not be dragged in.
        var members = await _context.KnowledgeBaseChunks.AsNoTracking()
            .Where(c => c.AtomicGroupId != null && groupIds.Contains(c.AtomicGroupId.Value)
                        && c.IsActive && c.IsCurrentArticleVersion && c.ArticleStatus == KnowledgeArticleStatus.Published)
            .OrderBy(c => c.AtomicGroupId).ThenBy(c => c.AtomicGroupSequence)
            .ToListAsync(cancellationToken);

        var result = new List<RetrievedEvidence>(evidence);
        var rank = evidence.Count;

        foreach (var member in members.Where(m => !have.Contains(m.Id)))
        {
            var anchor = evidence.First(e => e.AtomicGroupId == member.AtomicGroupId);
            result.Add(anchor with
            {
                ChunkId = member.Id,
                ContextHeader = member.ContextHeader,
                ChunkText = member.ChunkText,
                // Scores are zeroed rather than inherited: this chunk was included by rule, not
                // earned by relevance, and a downstream reader must not mistake it for a strong hit.
                VectorScore = 0, KeywordScore = 0, FusedScore = 0, RerankScore = 0,
                Rank = ++rank,
                IsGroupExpansion = true
            });
        }

        // Keep a group's parts together and in sequence, directly after the chunk that brought it in.
        return result
            .OrderBy(e => e.AtomicGroupId is null ? e.Rank : result.Where(x => x.AtomicGroupId == e.AtomicGroupId).Min(x => x.Rank))
            .ThenBy(e => members.FirstOrDefault(m => m.Id == e.ChunkId)?.AtomicGroupSequence ?? 0)
            .ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private async Task<(float[] Vector, bool CacheHit)> EmbedAsync(Guid? tenantId, string text, CancellationToken cancellationToken)
    {
        var key = RetrievalCacheKeys.Embedding(tenantId, _embeddings.ProviderName, _embeddings.ModelName, text);
        if (_cache.TryGet<float[]>(key, out var cached) && cached is { Length: > 0 })
            return (cached, true);

        var vector = await _embeddings.GetEmbeddingAsync(text, cancellationToken);
        vector = EmbeddingBatcher.Normalize(vector);

        // Never cache a failure: an empty vector cached for 30 minutes would turn one transient
        // provider error into half an hour of no vector search.
        if (vector.Length > 0)
            _cache.Set(key, vector, EmbeddingTtl);

        return (vector, false);
    }

    /// <summary>Same as <see cref="EmbedAsync"/>, for the platform's embedder. Cached under the tenant like
    /// every other key: a question can contain a tenant's private detail, so its embedding is not shared
    /// across tenants even though the model is the platform's.</summary>
    private async Task<(float[] Vector, bool CacheHit)> EmbedPlatformAsync(Guid? tenantId, string text, CancellationToken cancellationToken)
    {
        var key = RetrievalCacheKeys.Embedding(tenantId, "platform:" + _platform!.ProviderName, _platform.ModelName, text);
        if (_cache.TryGet<float[]>(key, out var cached) && cached is { Length: > 0 })
            return (cached, true);

        var vector = EmbeddingBatcher.Normalize(await _platform.GetEmbeddingAsync(text, cancellationToken));

        if (vector.Length > 0)
            _cache.Set(key, vector, EmbeddingTtl);

        return (vector, false);
    }

    private static string Fingerprint(KnowledgeRetrievalRequest r, string normalized, ProductModule? module) =>
        string.Join("", normalized, string.Join("", r.ConversationContext), r.DetectedIntent, module,
            r.TenantCountry, r.TicketLanguage, r.TenantPlatformVersion, r.SubscriptionPlanCode);

    private KnowledgeRetrievalResult Cache(string key, KnowledgeRetrievalResult result)
    {
        _cache.Set(key, result, ResultTtl);
        return result;
    }

    private static RetrievedEvidence ToEvidence(
        HydratedRow row, Candidate c, int rank, Dictionary<Guid, double> vectorScores, Dictionary<Guid, double> keywordScores, bool isExpansion) =>
        new(
            row.Id, row.ArticleId, row.ArticleKey, row.VersionNumber, row.Title, row.SourceType, row.AuthorityRank,
            row.ContextHeader, row.ChunkText, vectorScores.GetValueOrDefault(c.ChunkId), keywordScores.GetValueOrDefault(c.ChunkId),
            c.Fused, c.Rerank, rank, row.AtomicGroupId, isExpansion);

    private KnowledgeRetrievalResult Empty(string normalized, IReadOnlyList<ExpandedQuery> queries, Stopwatch stopwatch, string reason) =>
        new(Array.Empty<RetrievedEvidence>(), new RetrievalDiagnostics(
            normalized, queries.Select(q => q.Text).ToList(), 0, 0, 0, 0, 0, 0, 0, false, reason,
            (int)stopwatch.ElapsedMilliseconds, false, false, RetrievalMode.FusionOnly,
            _vectors.ProviderName, _keywords.ProviderName, _reranker.ProviderName, 0, 0));

    private KnowledgeRetrievalResult Build(
        string normalized, IReadOnlyList<ExpandedQuery> queries, int candidates, int vectorHits, int keywordHits,
        int fusedCount, int rerankedCount, IReadOnlyList<RetrievedEvidence> evidence, EvidenceGateResult gate,
        RetrievalMode mode, bool embeddingCacheHit, Stopwatch stopwatch, double churn, double spread) =>
        new(evidence, new RetrievalDiagnostics(
            normalized, queries.Select(q => q.Text).ToList(), candidates, vectorHits, keywordHits, fusedCount, rerankedCount,
            gate.TopScore, gate.SupportingCount, gate.Passed, gate.FailureReason, (int)stopwatch.ElapsedMilliseconds,
            embeddingCacheHit, false, mode, _vectors.ProviderName, _keywords.ProviderName, _reranker.ProviderName, churn, spread));

    private sealed record Candidate(Guid ChunkId, int FusionRank, double Fused, double PreRank)
    {
        public double Rerank { get; init; }
        public double Final { get; init; }
    }

    /// <summary>A chunk joined to the article fields boosting and citation need.</summary>
    private sealed class HydratedRow
    {
        public Guid Id { get; init; }
        public Guid ArticleId { get; init; }
        public string ContextHeader { get; init; } = string.Empty;
        public string ChunkText { get; init; } = string.Empty;
        public int AuthorityRank { get; init; }
        public ProductModule? ProductModule { get; init; }
        public KnowledgeSourceType SourceType { get; init; }
        public Guid? AtomicGroupId { get; init; }
        public string ArticleKey { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public string Title { get; init; } = string.Empty;
        public DateTime? PublishedAt { get; init; }
    }
}
