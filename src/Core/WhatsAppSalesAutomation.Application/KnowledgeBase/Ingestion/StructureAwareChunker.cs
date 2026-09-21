using System.Text;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>One chunk the chunker produced, before it becomes a <see cref="KnowledgeBaseChunk"/>.</summary>
public sealed record ChunkDraft(
    int Index,
    string ContextHeader,
    string Body,
    int TokenCount,
    Guid? AtomicGroupId,
    int? AtomicGroupSequence,
    int? AtomicGroupTotal)
{
    /// <summary>Exactly what gets embedded - header and body together, so the vector carries the
    /// context that makes the body interpretable. See KnowledgeBaseChunk.EmbeddingInput.</summary>
    public string EmbeddingInput => ContextHeader + "\n\n" + Body;
}

/// <summary>Quality signals recorded per ingestion (§H.8). Not decoration: a median chunk size well
/// away from target, or a sudden rise in split atomic groups, is how a chunker misconfiguration or an
/// article being written badly shows up before it shows up as bad answers.</summary>
public sealed record ChunkQualityMetrics(
    int ChunkCount,
    int MedianTokens,
    int OrphanChunks,
    int OversizedChunks,
    int SplitAtomicGroups)
{
    public double OrphanRatio => ChunkCount == 0 ? 0 : (double)OrphanChunks / ChunkCount;

    public double OversizedRatio => ChunkCount == 0 ? 0 : (double)OversizedChunks / ChunkCount;
}

/// <summary>
/// Splits an article into chunks that can each be read alone and still give a correct, complete
/// answer (§H).
///
/// The governing rule is not size, it is indivisibility. Fixed-size chunking is fine for a sales FAQ
/// and dangerous for a billing rule: cutting between "refunds are processed in 7 working days" and
/// "this does not apply to annual plans" produces two chunks that are each individually confident and
/// one of which is wrong. Everything here - atomic units, section boundaries, the group rule - exists
/// to make that particular cut impossible rather than merely unlikely.
/// </summary>
public sealed class StructureAwareChunker
{
    private readonly ITokenCounter _tokens;

    public StructureAwareChunker(ITokenCounter tokens)
    {
        _tokens = tokens;
    }

    /// <param name="contentOverride">Chunk this text instead of <c>article.Content</c>. Ingestion passes
    /// the sanitized text, so the stored article stays exactly as the author wrote it while what gets
    /// embedded has had its injection tokens neutralized and invisible characters removed.</param>
    public IReadOnlyList<ChunkDraft> Chunk(KnowledgeBaseArticle article, string? contentOverride = null)
    {
        var content = contentOverride ?? article.Content;
        var parameters = ChunkingParameters.For(article.SourceType);
        var sections = MarkdownStructure.Parse(content);
        var drafts = new List<ChunkDraft>();

        // An FAQ is one question and one answer, and is never split or merged (§H.7). Doing this
        // before the general algorithm - rather than by tuning sizes until it usually happens - is
        // what makes "usually" into "always".
        if (article.SourceType == KnowledgeSourceType.ApprovedFaq)
            return ChunkFaq(article, parameters, content);

        foreach (var section in sections)
        {
            var header = BuildContextHeader(article, section.Breadcrumb);
            var packed = PackSection(section, header, parameters);
            drafts.AddRange(packed);
        }

        // Section-local merge of undersized chunks. Done across the whole list afterwards rather than
        // inside PackSection because a section that produced exactly one small chunk - "Summary" is
        // the usual one - has nothing to merge with until its neighbours exist.
        var merged = MergeSmallChunks(drafts, parameters);

        return Renumber(merged);
    }

    /// <summary>
    /// §H.4 step 3, within one section. Section boundaries are never crossed: two different H2
    /// sections mixed into one chunk degrade the retrieval quality of both, because the resulting
    /// vector sits between two topics and is close to neither.
    /// </summary>
    private List<ChunkDraft> PackSection(MarkdownSection section, string header, ChunkingParameters parameters)
    {
        var results = new List<ChunkDraft>();
        var current = new StringBuilder();

        void Flush()
        {
            var body = current.ToString().Trim();
            if (body.Length > 0)
                results.Add(NewDraft(header, body, null, null, null));

            current.Clear();
        }

        foreach (var block in section.Blocks)
        {
            var blockTokens = _tokens.Count(block.Text);
            var currentTokens = _tokens.Count(current.ToString());

            if (currentTokens + blockTokens <= parameters.TargetTokens)
            {
                Append(current, block.Text);
                continue;
            }

            // Too big to keep whole even on its own. Split it at its own natural sub-boundaries and
            // bind the parts into one atomic group, so retrieval that finds any part pulls in all of
            // them - see §M.3. This is the ONLY place an atomic unit is ever divided.
            if (blockTokens > parameters.MaxTokens)
            {
                Flush();

                var parts = MarkdownStructure.SplitOversizedAtomic(block, _tokens, parameters.MaxTokens);
                var groupId = parts.Count > 1 ? Guid.NewGuid() : (Guid?)null;

                for (var i = 0; i < parts.Count; i++)
                {
                    var marker = groupId is null
                        ? string.Empty
                        // Stated in the body, not only in metadata: the model reads the body, and a
                        // part that does not say it is a part reads as a complete rule.
                        : $"[Part {i + 1} of {parts.Count} - continues from the previous part]\n";

                    results.Add(NewDraft(
                        header,
                        marker + parts[i],
                        groupId,
                        groupId is null ? null : i + 1,
                        groupId is null ? null : parts.Count));
                }

                continue;
            }

            // Ordinary overflow: close the current chunk and start a new one, carrying overlap.
            Flush();

            // Overlap applies only WITHIN a section (§H.6). Crossing a section boundary with overlap
            // would mix two topics, so PackSection is per-section and this can never leak across one.
            var overlap = results.Count > 0 && parameters.OverlapTokens > 0
                ? TakeTrailingTokens(results[^1].Body, parameters.OverlapTokens)
                : string.Empty;

            if (overlap.Length > 0)
                Append(current, overlap);

            Append(current, block.Text);
        }

        Flush();
        return results;
    }

    /// <summary>§H.7 - one FAQ entry is exactly one chunk, never split and never merged. An answer
    /// that exceeds MaxTokens is not an FAQ, it is an article; it is still emitted whole rather than
    /// butchered, and the oversized count in the metrics is what surfaces it.</summary>
    private IReadOnlyList<ChunkDraft> ChunkFaq(KnowledgeBaseArticle article, ChunkingParameters parameters, string content)
    {
        var header = BuildContextHeader(article, article.Title);
        var body = content.Trim();

        return new[] { NewDraft(header, body, null, null, null) with { Index = 0 } };
    }

    /// <summary>
    /// §H.4 step 4. Merges an undersized chunk into its neighbour when both came from the same
    /// section, so a two-line "Summary" is not left as a chunk too thin to answer anything.
    ///
    /// Chunks belonging to an atomic group are never merged: their group membership is what
    /// guarantees they travel together, and merging would change the part numbering the body text
    /// already states.
    /// </summary>
    private List<ChunkDraft> MergeSmallChunks(List<ChunkDraft> drafts, ChunkingParameters parameters)
    {
        var merged = new List<ChunkDraft>();

        foreach (var draft in drafts)
        {
            var previous = merged.Count > 0 ? merged[^1] : null;

            var mergeable = previous is not null
                && previous.AtomicGroupId is null
                && draft.AtomicGroupId is null
                && draft.TokenCount < parameters.MinTokens
                // Same section - the context header IS the section identity here.
                && previous.ContextHeader == draft.ContextHeader
                && previous.TokenCount + draft.TokenCount <= parameters.MaxTokens;

            if (mergeable)
            {
                var body = previous!.Body + "\n\n" + draft.Body;
                merged[^1] = previous with { Body = body, TokenCount = _tokens.Count(body) };
                continue;
            }

            merged.Add(draft);
        }

        return merged;
    }

    /// <summary>
    /// The self-describing preamble repeated on every chunk (§H.2).
    ///
    /// Without it, a chunk reading "7 working days" is meaningless once retrieval has torn it out of
    /// its article - and worse, indistinguishable from a chunk about a different policy's 7 working
    /// days. It is included in the embedding input as well as the prompt, so it improves retrieval
    /// and generation for the same cost.
    /// </summary>
    public static string BuildContextHeader(KnowledgeBaseArticle article, string? sectionBreadcrumb)
    {
        var builder = new StringBuilder();
        builder.Append("Article: ").Append(article.Title).Append('\n');
        builder.Append("Source : ").Append(article.SourceType)
               .Append(" (authority ").Append(article.AuthorityRank).Append(")\n");

        var applies = new List<string>
        {
            "Country=" + (article.CountryCode ?? "All"),
            "Language=" + article.LanguageCode
        };

        if (article.AppliesToVersionMin is { Length: > 0 } || article.AppliesToVersionMax is { Length: > 0 })
            applies.Add("Version=" + (article.AppliesToVersionMin ?? "any") + ".." + (article.AppliesToVersionMax ?? "any"));

        builder.Append("Applies: ").Append(string.Join(" | ", applies)).Append('\n');

        if (!string.IsNullOrWhiteSpace(sectionBreadcrumb))
            builder.Append("Section: ").Append(sectionBreadcrumb);

        return builder.ToString().TrimEnd();
    }

    /// <summary>§H.8 quality signals over a finished chunk set.</summary>
    public static ChunkQualityMetrics Measure(IReadOnlyList<ChunkDraft> drafts, ChunkingParameters parameters)
    {
        if (drafts.Count == 0)
            return new ChunkQualityMetrics(0, 0, 0, 0, 0);

        var sorted = drafts.Select(d => d.TokenCount).OrderBy(t => t).ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

        return new ChunkQualityMetrics(
            drafts.Count,
            median,
            drafts.Count(d => d.TokenCount < parameters.MinTokens),
            drafts.Count(d => d.TokenCount > parameters.MaxTokens),
            drafts.Where(d => d.AtomicGroupId is not null).Select(d => d.AtomicGroupId).Distinct().Count());
    }

    private ChunkDraft NewDraft(string header, string body, Guid? groupId, int? sequence, int? total) =>
        new(0, header, body, _tokens.Count(body), groupId, sequence, total);

    private static List<ChunkDraft> Renumber(List<ChunkDraft> drafts) =>
        drafts.Select((d, i) => d with { Index = i }).ToList();

    private static void Append(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
            builder.Append("\n\n");

        builder.Append(text);
    }

    /// <summary>
    /// The tail of the previous chunk, for overlap. Cut at a whitespace boundary so the overlap never
    /// begins mid-word - a fragment like "ly not apply to annual plans" is worse than no overlap,
    /// because it embeds as noise while still costing tokens.
    /// </summary>
    private string TakeTrailingTokens(string text, int overlapTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || overlapTokens <= 0)
            return string.Empty;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var taken = new List<string>();

        for (var i = words.Length - 1; i >= 0; i--)
        {
            taken.Insert(0, words[i]);
            if (_tokens.Count(string.Join(" ", taken)) >= overlapTokens)
                break;
        }

        return string.Join(" ", taken);
    }
}
