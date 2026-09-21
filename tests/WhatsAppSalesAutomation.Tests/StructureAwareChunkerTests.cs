using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The chunker, tested against the failure it exists to prevent.
///
/// That failure has one shape: a chunk that is individually confident and collectively wrong.
/// "Refunds are processed in 7 working days" is true; "this does not apply to annual plans" is what
/// makes it true. Separated, the first is a correct-sounding answer given to the wrong tenant, and
/// nothing downstream can detect that it is missing its other half - retrieval scored it well,
/// grounding verification finds it supported, and the citation points at a real article.
///
/// So most of what follows asserts that particular things stay TOGETHER, which is a weaker-looking
/// assertion than a size check and a much more important one.
/// </summary>
public class StructureAwareChunkerTests
{
    private static readonly ITokenCounter Tokens = new HeuristicTokenCounter();

    private static StructureAwareChunker NewChunker() => new(Tokens);

    private static KnowledgeBaseArticle Article(
        string content,
        KnowledgeSourceType sourceType = KnowledgeSourceType.AdminConfiguredArticle) => new()
        {
            Title = "Refund and Cancellation Policy",
            Content = content,
            SourceType = sourceType,
            AuthorityRank = 90,
            LanguageCode = "en",
            CountryCode = "IN"
        };

    // ── The rule that matters most ───────────────────────────────────────────────────────

    [Fact]
    public void A_rule_and_its_condition_are_never_separated()
    {
        // The document's own worked example. The chunker must not put the general statement in one
        // chunk and the exception that limits it in another.
        var article = Article(@"
## Rule

Refunds are processed within 7 working days of an approved request. This does not apply to annual
plans, for which a pro-rata calculation is used instead.
", KnowledgeSourceType.RefundCancellationPolicy);

        var chunks = NewChunker().Chunk(article);

        var carrying = chunks.Where(c => c.Body.Contains("7 working days")).ToList();
        Assert.Single(carrying);
        Assert.Contains("does not apply to annual", carrying[0].Body);
    }

    [Fact]
    public void Conditional_prose_is_marked_atomic_so_packing_cannot_break_it()
    {
        var blocks = MarkdownStructure.Parse("Refunds take 7 days unless the plan is annual.")[0].Blocks;

        Assert.Equal(AtomicReason.Conditional, blocks[0].Atomic);
    }

    [Theory]
    [InlineData("This does not apply to annual plans.")]
    [InlineData("Refunds are issued only if the request is within 30 days.")]
    [InlineData("Except when the account is suspended, credits roll over.")]
    [InlineData("यह नियम annual plans पर लागू नहीं होता।")]
    [InlineData("अगर plan annual है तो pro-rata लागू होगा।")]
    public void Conditional_language_is_recognised_in_both_languages(string text)
    {
        Assert.Equal(AtomicReason.Conditional, MarkdownStructure.Parse(text)[0].Blocks[0].Atomic);
    }

    [Fact]
    public void Ordinary_prose_is_not_treated_as_atomic()
    {
        // The detector is deliberately broad, but not so broad that everything becomes indivisible -
        // that would defeat packing entirely and produce one chunk per paragraph.
        var blocks = MarkdownStructure.Parse("We notify the customer by WhatsApp when the refund completes.")[0].Blocks;

        Assert.Equal(AtomicReason.None, blocks[0].Atomic);
    }

    // ── Atomic structures ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_table_stays_whole()
    {
        var article = Article(@"
## Plans

| Plan   | Refund window | Notes        |
|--------|---------------|--------------|
| Starter| 7 days        | Full refund  |
| Growth | 14 days       | Pro-rata     |
| Scale  | 30 days       | Pro-rata     |
");

        var chunks = NewChunker().Chunk(article);
        var withTable = chunks.Where(c => c.Body.Contains("Starter")).ToList();

        Assert.Single(withTable);
        // Rows without the header row are meaningless - "7 days" in which column?
        Assert.Contains("Refund window", withTable[0].Body);
        Assert.Contains("Scale", withTable[0].Body);
    }

    [Fact]
    public void A_numbered_procedure_stays_whole_even_with_blank_lines_between_steps()
    {
        var article = Article(@"
## Steps

1. Open the Billing screen.

2. Choose the invoice.

3. Press Request refund.
");

        var chunks = NewChunker().Chunk(article);
        var withSteps = chunks.Where(c => c.Body.Contains("Open the Billing")).ToList();

        Assert.Single(withSteps);
        Assert.Contains("Press Request refund", withSteps[0].Body);
    }

    [Fact]
    public void A_fenced_code_block_stays_whole_and_its_hashes_are_not_headings()
    {
        var article = Article(@"
## Configuration

```
# This comment is not a heading
AiProviders:Provider=OpenAI
# Neither is this one
AiProviders:EmbeddingProvider=OpenAI
```
");

        var sections = MarkdownStructure.Parse(article.Content);

        // Two sections would mean the '#' comments inside the fence were read as headings, which
        // would shred a config file into one section per comment line.
        Assert.Single(sections);
        Assert.Equal(AtomicReason.CodeBlock, sections[0].Blocks[0].Atomic);
    }

    // ── Section boundaries ───────────────────────────────────────────────────────────────

    [Fact]
    public void Two_sections_are_never_packed_into_one_chunk()
    {
        var article = Article(@"
## Refunds

Short text about refunds.

## Cancellations

Short text about cancellations.
");

        var chunks = NewChunker().Chunk(article);

        // Both are far below the target size, so a size-driven chunker would happily merge them -
        // and the resulting vector would sit between two topics and be close to neither.
        Assert.DoesNotContain(chunks, c => c.Body.Contains("about refunds") && c.Body.Contains("about cancellations"));
    }

    [Fact]
    public void The_context_header_carries_the_section_breadcrumb()
    {
        var article = Article(@"
# Refund Policy

## Rule

### Standard refunds

Refunds are processed within 7 working days.
");

        var chunk = NewChunker().Chunk(article).Single(c => c.Body.Contains("7 working days"));

        Assert.Contains("Refund Policy > Rule > Standard refunds", chunk.ContextHeader);
        Assert.Contains("RefundCancellationPolicy", StructureAwareChunker.BuildContextHeader(
            Article("x", KnowledgeSourceType.RefundCancellationPolicy), "Rule"));
    }

    [Fact]
    public void The_context_header_is_part_of_what_gets_embedded()
    {
        var article = Article("## Rule\n\nRefunds take 7 working days.");
        var chunk = NewChunker().Chunk(article).First();

        // A chunk body reading "7 working days" is indistinguishable from any other policy's seven
        // days unless the header is in the vector too, not only in the prompt.
        Assert.StartsWith(chunk.ContextHeader, chunk.EmbeddingInput);
        Assert.Contains(chunk.Body, chunk.EmbeddingInput);
        Assert.Contains("Country=IN", chunk.EmbeddingInput);
    }

    [Fact]
    public void Content_before_the_first_heading_is_not_discarded()
    {
        var article = Article("Refunds take 7 working days.\n\n## Details\n\nMore text.");

        var chunks = NewChunker().Chunk(article);

        Assert.Contains(chunks, c => c.Body.Contains("7 working days"));
    }

    // ── Atomic groups ────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_oversized_table_is_split_into_a_group_with_the_header_on_every_part()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 400)
            .Select(i => $"| Plan {i} | {i} days | A reasonably long note about plan number {i} |"));

        var article = Article($@"
## Plans

| Plan | Refund window | Notes |
|------|---------------|-------|
{rows}
", KnowledgeSourceType.KnownIssue);   // smallest MaxTokens, so the split is forced

        var chunks = NewChunker().Chunk(article);
        var parts = chunks.Where(c => c.AtomicGroupId is not null).ToList();

        Assert.True(parts.Count > 1, "the table should have been split into a group");
        Assert.Single(parts.Select(p => p.AtomicGroupId).Distinct());

        foreach (var part in parts)
        {
            // Every part must be readable on its own, which for a table means carrying the header.
            Assert.Contains("Refund window", part.Body);
            Assert.Equal(parts.Count, part.AtomicGroupTotal);
        }

        Assert.Equal(Enumerable.Range(1, parts.Count), parts.Select(p => p.AtomicGroupSequence!.Value));
    }

    [Fact]
    public void Split_parts_say_in_their_body_that_they_are_parts()
    {
        var steps = string.Join("\n", Enumerable.Range(1, 300)
            .Select(i => $"{i}. Do the {i}th thing, which takes a little explaining and some words."));

        var article = Article($"## Steps\n\n{steps}", KnowledgeSourceType.KnownIssue);

        var parts = NewChunker().Chunk(article).Where(c => c.AtomicGroupId is not null).ToList();

        Assert.True(parts.Count > 1);
        // Metadata alone is not enough: the model reads the body, and a part that does not announce
        // itself as a part reads as a complete procedure.
        Assert.All(parts, p => Assert.Contains("Part ", p.Body));
    }

    [Fact]
    public void A_block_that_fits_is_never_given_a_group()
    {
        var article = Article("## Rule\n\nRefunds take 7 working days unless the plan is annual.");

        Assert.All(NewChunker().Chunk(article), c => Assert.Null(c.AtomicGroupId));
    }

    // ── FAQ special case ─────────────────────────────────────────────────────────────────

    [Fact]
    public void An_faq_is_exactly_one_chunk()
    {
        var article = Article(@"
## How do I request a refund?

Open Billing, choose the invoice, and press Request refund. If your plan is annual, a pro-rata
amount is calculated. Refunds are processed within 7 working days.
", KnowledgeSourceType.ApprovedFaq);

        var chunks = NewChunker().Chunk(article);

        Assert.Single(chunks);
        Assert.Contains("pro-rata", chunks[0].Body);
    }

    // ── Sizing and merging ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_tiny_section_is_merged_into_its_neighbour_rather_than_left_alone()
    {
        var article = Article(@"
## Summary

Refunds in 7 days.

Some more text in the very same section to give the merge something to attach to.
");

        var chunks = NewChunker().Chunk(article);

        // Both blocks are from one section and both are small, so they belong in one chunk - a
        // two-line chunk is too thin to answer anything on its own.
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunks_from_one_section_overlap_but_chunks_across_sections_do_not()
    {
        var filler = string.Join(" ", Enumerable.Repeat("policy detail sentence about refunds", 120));
        var article = Article($@"
## Refunds

{filler}

{filler}

## Cancellations

Cancellations are immediate.
", KnowledgeSourceType.KnownIssue);

        var chunks = NewChunker().Chunk(article);
        var cancellations = chunks.Single(c => c.Body.Contains("Cancellations are immediate"));

        // Overlap only applies within a section (§H.6): carrying refund text into the cancellation
        // chunk would blur both.
        Assert.DoesNotContain("policy detail sentence", cancellations.Body);
    }

    [Fact]
    public void Per_source_type_sizing_is_applied()
    {
        Assert.Equal(350, ChunkingParameters.For(KnowledgeSourceType.ApprovedFaq).TargetTokens);
        Assert.Equal(700, ChunkingParameters.For(KnowledgeSourceType.PlatformPolicy).TargetTokens);
        Assert.Equal(600, ChunkingParameters.For(KnowledgeSourceType.RefundCancellationPolicy).TargetTokens);
        Assert.Equal(500, ChunkingParameters.For(KnowledgeSourceType.AdminConfiguredArticle).TargetTokens);
    }

    [Fact]
    public void Chunks_are_indexed_contiguously_from_zero()
    {
        var article = Article(string.Join("\n\n", Enumerable.Range(1, 6)
            .Select(i => $"## Section {i}\n\nSome text for section {i}.")));

        var chunks = NewChunker().Chunk(article);

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void Quality_metrics_report_what_they_measure()
    {
        var article = Article(string.Join("\n\n", Enumerable.Range(1, 5)
            .Select(i => $"## Section {i}\n\n{string.Join(" ", Enumerable.Repeat("word", 400))}")));

        var chunks = NewChunker().Chunk(article);
        var metrics = StructureAwareChunker.Measure(chunks, ChunkingParameters.For(article.SourceType));

        Assert.Equal(chunks.Count, metrics.ChunkCount);
        Assert.True(metrics.MedianTokens > 0);
        Assert.InRange(metrics.OrphanRatio, 0, 1);
    }

    [Fact]
    public void An_empty_article_produces_no_chunks_rather_than_one_empty_one()
    {
        Assert.Empty(NewChunker().Chunk(Article("   \n\n  ")));
    }

    // ── Token counting ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Devanagari_is_not_counted_at_the_english_ratio()
    {
        // The reason ITokenCounter exists at all. chars/4 undercounts Devanagari by roughly 2x, and
        // an undercount produces chunks that overflow the real budget rather than an obviously wrong
        // number - the failure arrives at the model, not at the chunker.
        const string hindi = "यह नियम annual plans पर लागू नहीं होता है और इसके लिए pro-rata गणना होगी";
        const string english = "This rule does not apply to annual plans and a pro-rata calculation is used";

        var hindiTokens = Tokens.Count(hindi);
        var englishTokens = Tokens.Count(english);

        Assert.True(hindiTokens > englishTokens,
            $"expected Hindi ({hindiTokens}) to cost more tokens than comparable English ({englishTokens})");
        Assert.True(hindiTokens > hindi.Length / 4);
    }

    [Fact]
    public void Non_empty_text_never_costs_zero_tokens()
    {
        // A zero-cost block would pack into a chunk without limit.
        Assert.True(Tokens.Count(".") >= 1);
        Assert.Equal(0, Tokens.Count(""));
        Assert.Equal(0, Tokens.Count(null));
    }
}
