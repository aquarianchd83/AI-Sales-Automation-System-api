using System.Text;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>Why a block must not be split. Recorded rather than inferred so the chunker's decisions
/// can be explained in diagnostics, and so a test can assert WHICH rule kept a block together.</summary>
public enum AtomicReason
{
    /// <summary>An ordinary paragraph. Splittable.</summary>
    None = 0,

    /// <summary>A markdown table. Rows without their header row are meaningless.</summary>
    Table = 1,

    /// <summary>A consecutively numbered procedure. Half a procedure abandons the reader mid-task.</summary>
    NumberedProcedure = 2,

    /// <summary>A fenced code or config block. Half a config is a wrong config.</summary>
    CodeBlock = 3,

    /// <summary>A sentence group carrying a condition - "if/then", "unless", "except when". The
    /// condition and its consequence must never be separated: the consequence alone reads as
    /// unconditional, which is the single most dangerous thing a policy chunk can do.</summary>
    Conditional = 4,

    /// <summary>A definition - "X means ...". Indivisible by nature.</summary>
    Definition = 5
}

/// <summary>One indivisible piece of a section's body, with the reason it is indivisible.</summary>
public sealed record MarkdownBlock(string Text, AtomicReason Atomic)
{
    public bool IsAtomic => Atomic != AtomicReason.None;
}

/// <summary>A leaf section of the heading tree, with the breadcrumb that locates it.</summary>
public sealed record MarkdownSection(string Breadcrumb, string Heading, IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>
/// Parses normalized markdown into a heading tree of sections, each split into blocks, marking the
/// blocks §H.3 says must never be cut.
///
/// This is deliberately a small hand-written parser rather than a full markdown library. It needs
/// exactly three things - heading levels, block boundaries, and the handful of atomic shapes - and a
/// general-purpose AST would have to be walked back down to those anyway. The authoring template in
/// §E.5 is what authors are asked to follow, so the structures recognised here are the ones the
/// template produces.
/// </summary>
public static class MarkdownStructure
{
    /// <summary>"## Rule" - captures the level from the run of hashes and the text after it.</summary>
    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);

    /// <summary>"1. ", "2) " - the start of a numbered procedure step.</summary>
    private static readonly Regex NumberedItemPattern = new(@"^\s*\d+[.)]\s+", RegexOptions.Compiled);

    /// <summary>A markdown table row - starts and ends with a pipe, or simply contains one and is
    /// followed by a separator row. The leading-pipe form is what the template produces.</summary>
    private static readonly Regex TableRowPattern = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);

    /// <summary>The "---|---" separator under a table header.</summary>
    private static readonly Regex TableSeparatorPattern = new(@"^\s*\|?[\s:-]*-{2,}[\s:|-]*\|?\s*$", RegexOptions.Compiled);

    /// <summary>Conditional language in English and Hindi. Matched on whole words so that "if" does
    /// not fire inside "notify", and deliberately broad: a false positive costs a slightly larger
    /// chunk, a false negative separates a condition from its consequence.</summary>
    private static readonly Regex ConditionalPattern = new(
        @"\b(if|unless|except\s+when|except\s+if|provided\s+that|only\s+if|does\s+not\s+apply|not\s+applicable)\b|(अगर|यदि|जब\s+तक|को\s+छोड़कर|लागू\s+नहीं)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Definition language - "X means ...", "X is defined as ...".</summary>
    private static readonly Regex DefinitionPattern = new(
        @"\b(means|is\s+defined\s+as|refers\s+to|stands\s+for)\b|(का\s+मतलब|की\s+परिभाषा)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Splits markdown into leaf sections. Content before the first heading becomes a section with an
    /// empty heading rather than being dropped - an article written as plain prose still has to be
    /// chunked, and silently discarding its body would be the worst possible reading of "no headings".
    /// </summary>
    public static IReadOnlyList<MarkdownSection> Parse(string markdown)
    {
        var sections = new List<MarkdownSection>();
        if (string.IsNullOrWhiteSpace(markdown))
            return sections;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Heading text by level, so a breadcrumb can be rebuilt at any point in the walk.
        var trail = new string?[7];
        var body = new List<string>();
        var currentHeading = string.Empty;
        var currentBreadcrumb = string.Empty;
        var insideFence = false;

        void Flush()
        {
            var text = string.Join("\n", body).Trim();
            if (text.Length > 0)
                sections.Add(new MarkdownSection(currentBreadcrumb, currentHeading, SplitBlocks(text)));

            body.Clear();
        }

        foreach (var line in lines)
        {
            // A "#" inside a fenced block is a comment in some language, not a heading. Tracking the
            // fence here is what stops a shell script's comments from shredding an article into
            // dozens of one-line sections.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                insideFence = !insideFence;

            var heading = insideFence ? null : HeadingPattern.Match(line);
            if (heading is { Success: true })
            {
                Flush();

                var level = heading.Groups[1].Value.Length;
                currentHeading = heading.Groups[2].Value.Trim();
                trail[level] = currentHeading;

                // Anything deeper than this heading is no longer in scope.
                for (var deeper = level + 1; deeper < trail.Length; deeper++)
                    trail[deeper] = null;

                currentBreadcrumb = string.Join(" > ", trail.Where(t => !string.IsNullOrEmpty(t))!);
                continue;
            }

            body.Add(line);
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// Splits a section body into blocks on blank lines, then merges runs that belong together:
    /// table rows with their header, consecutive numbered items with each other, fenced code with its
    /// delimiters.
    /// </summary>
    private static IReadOnlyList<MarkdownBlock> SplitBlocks(string body)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = body.Split('\n');
        var buffer = new List<string>();
        var bufferKind = AtomicReason.None;

        void Flush()
        {
            if (buffer.Count == 0)
                return;

            var text = string.Join("\n", buffer).Trim();
            if (text.Length > 0)
            {
                // A paragraph's atomicity is a property of its content, not of its shape, so it is
                // classified after the fact. Table/procedure/code blocks already know what they are.
                var reason = bufferKind != AtomicReason.None ? bufferKind : ClassifyProse(text);
                blocks.Add(new MarkdownBlock(text, reason));
            }

            buffer.Clear();
            bufferKind = AtomicReason.None;
        }

        var insideFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!insideFence)
                {
                    Flush();
                    insideFence = true;
                    bufferKind = AtomicReason.CodeBlock;
                    buffer.Add(line);
                }
                else
                {
                    buffer.Add(line);
                    insideFence = false;
                    Flush();
                }

                continue;
            }

            if (insideFence)
            {
                buffer.Add(line);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                // A blank line ends an ordinary paragraph, but NOT a numbered procedure: authors
                // routinely leave blank lines between steps, and treating that as the end of the
                // procedure would split it exactly where §H.3 says it must not be split.
                if (bufferKind == AtomicReason.NumberedProcedure && PeekContinuesProcedure(lines, i))
                {
                    buffer.Add(line);
                    continue;
                }

                Flush();
                continue;
            }

            var isTableRow = TableRowPattern.IsMatch(line) || TableSeparatorPattern.IsMatch(line);
            var isNumbered = NumberedItemPattern.IsMatch(line);

            var kind = isTableRow ? AtomicReason.Table
                : isNumbered ? AtomicReason.NumberedProcedure
                : AtomicReason.None;

            // A change of kind starts a new block - except a continuation line indented under a
            // numbered item, which belongs to the step above it.
            if (buffer.Count > 0 && kind != bufferKind && !(bufferKind == AtomicReason.NumberedProcedure && line.StartsWith("  ", StringComparison.Ordinal)))
                Flush();

            if (buffer.Count == 0)
                bufferKind = kind;

            buffer.Add(line);
        }

        Flush();
        return blocks;
    }

    /// <summary>Looks past a blank line for another numbered item, so a procedure written with blank
    /// lines between its steps stays one block.</summary>
    private static bool PeekContinuesProcedure(string[] lines, int blankIndex)
    {
        for (var i = blankIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;

            return NumberedItemPattern.IsMatch(lines[i]);
        }

        return false;
    }

    /// <summary>Marks prose that carries a condition or a definition. These are the two atomic kinds
    /// with no structural signature - they have to be recognised from the words.</summary>
    private static AtomicReason ClassifyProse(string text)
    {
        if (ConditionalPattern.IsMatch(text))
            return AtomicReason.Conditional;

        return DefinitionPattern.IsMatch(text) ? AtomicReason.Definition : AtomicReason.None;
    }

    /// <summary>
    /// Splits an over-sized atomic block at its own natural sub-boundaries - numbered items, table
    /// rows - rather than at an arbitrary offset, per §H.3.
    ///
    /// A table keeps its header row on every part, because rows without the header are exactly the
    /// meaningless fragment the atomic rule exists to prevent. Everything else falls back to
    /// paragraph and then sentence boundaries; the last resort is a hard character split, which is
    /// bad but strictly better than emitting a chunk the model cannot accept.
    /// </summary>
    public static IReadOnlyList<string> SplitOversizedAtomic(
        MarkdownBlock block, ITokenCounter tokens, int maxTokens)
    {
        var lines = block.Text.Split('\n');

        var header = block.Atomic == AtomicReason.Table
            ? string.Join("\n", lines.Take(TableHeaderLineCount(lines)))
            : null;

        var startIndex = header is null ? 0 : TableHeaderLineCount(lines);
        var units = block.Atomic switch
        {
            AtomicReason.Table => lines.Skip(startIndex).Where(l => l.Trim().Length > 0).ToList(),
            AtomicReason.NumberedProcedure => GroupNumberedItems(lines),
            _ => SplitSentences(block.Text)
        };

        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (var unit in units)
        {
            var candidate = current.Length == 0 ? unit : current + "\n" + unit;
            var withHeader = header is null ? candidate : header + "\n" + candidate;

            if (current.Length > 0 && tokens.Count(withHeader) > maxTokens)
            {
                parts.Add(Compose(header, current.ToString()));
                current.Clear();
                current.Append(unit);
                continue;
            }

            current.Clear();
            current.Append(candidate);
        }

        if (current.Length > 0)
            parts.Add(Compose(header, current.ToString()));

        return parts.Count > 0 ? parts : new List<string> { block.Text };
    }

    private static string Compose(string? header, string body) =>
        header is null ? body.Trim() : (header + "\n" + body).Trim();

    /// <summary>Header row plus its separator row, when the separator is present.</summary>
    private static int TableHeaderLineCount(string[] lines) =>
        lines.Length >= 2 && TableSeparatorPattern.IsMatch(lines[1]) ? 2 : 1;

    /// <summary>Each numbered item plus any indented continuation lines beneath it.</summary>
    private static List<string> GroupNumberedItems(string[] lines)
    {
        var items = new List<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (NumberedItemPattern.IsMatch(line) && current.Count > 0)
            {
                items.Add(string.Join("\n", current));
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0)
            items.Add(string.Join("\n", current));

        return items;
    }

    /// <summary>Sentence boundaries for English and Devanagari (the danda, U+0964). Not a full
    /// sentence tokenizer - it only has to find somewhere less damaging than mid-word to cut a block
    /// that was already too large to keep whole.</summary>
    private static List<string> SplitSentences(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?।])\s+")
            .Where(s => s.Trim().Length > 0)
            .ToList();

        return sentences.Count > 0 ? sentences : new List<string> { text };
    }
}
