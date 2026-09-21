using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>What ingestion should do about what it found in a document (§G.4b).</summary>
public enum InjectionSeverity
{
    /// <summary>Nothing suspicious.</summary>
    None = 0,

    /// <summary>Role or delimiter tokens. Kept, but escaped so they cannot read as tokens.</summary>
    Neutralize = 1,

    /// <summary>Instruction-override language. Ingestion proceeds, the article is marked as needing
    /// security review, and auto-approval is off.</summary>
    Flag = 2,

    /// <summary>Tool invocation or role assumption. Ingestion fails and the article is rejected.</summary>
    Block = 3
}

/// <summary>One thing the scan found, kept so a reviewer can see WHAT tripped it rather than only
/// that something did - a bare "flagged" on a 40-page document is not actionable.</summary>
public sealed record InjectionFinding(InjectionSeverity Severity, string Pattern, string Excerpt);

public sealed record SanitizedContent(
    string Content,
    InjectionSeverity Severity,
    IReadOnlyList<InjectionFinding> Findings)
{
    public bool IsBlocked => Severity == InjectionSeverity.Block;

    public bool RequiresSecurityReview => Severity >= InjectionSeverity.Flag;
}

/// <summary>
/// Cleans document text and neutralizes prompt-injection attempts in it (§G.4).
///
/// This is the FIRST of four layers, and on its own it is the weakest of them. A pattern list can
/// always be evaded; what actually stops an injected instruction from doing harm is the structural
/// framing that presents retrieved text as data (§N), and the allow-listed tool registry that means
/// the model has no dangerous capability to be talked into using (§O). This layer exists to catch
/// the obvious and to make the non-obvious visible to a human reviewer - not to be relied on.
///
/// The cleaning half is not cosmetic either. Zero-width characters both degrade embeddings and are
/// the standard way to hide an instruction from a human reviewer while leaving it perfectly legible
/// to a model, so removing them is a security step as much as a formatting one.
/// </summary>
public static class ContentSanitizer
{
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    /// <summary>Blocked outright: an attempt to make the document act, or to reassign the model's
    /// role. There is no legitimate reason for a knowledge article to contain either.</summary>
    private static readonly (string Name, Regex Pattern)[] BlockPatterns =
    {
        ("role-assumption", new Regex(@"you\s+are\s+(now\s+)?(an?\s+)?(admin|superadmin|developer|system|root)", Flags)),
        ("tool-invocation", new Regex(@"\b(call|invoke|execute|run)\s+(the\s+)?(tool|function|api|command)\b", Flags)),
        ("privileged-action", new Regex(@"\b(approve|issue|process)\s+(the\s+)?(refund|request|payment)\b", Flags)),
        ("credit-grant", new Regex(@"\bgrant\s+.{0,40}\bcredits?\b", Flags))
    };

    /// <summary>Flagged for human review: instruction-override language. Some of these can appear
    /// innocently in a document ABOUT prompts, which is exactly why this is review rather than
    /// rejection.</summary>
    private static readonly (string Name, Regex Pattern)[] FlagPatterns =
    {
        ("instruction-override", new Regex(@"ignore\s+(all\s+)?(previous|prior|above)\s+(instructions?|prompts?|rules?)", Flags)),
        ("policy-disregard", new Regex(@"disregard\s+(the\s+)?(policy|rules|guidelines|instructions)", Flags)),
        ("hidden-payload", new Regex(@"[A-Za-z0-9+/]{200,}={0,2}", RegexOptions.Compiled))
    };

    /// <summary>Neutralized in place: chat-format delimiters and role tags. Escaped rather than
    /// removed, so a document that legitimately discusses them keeps its meaning while the tokens
    /// stop being tokens.</summary>
    private static readonly (string Name, Regex Pattern)[] NeutralizePatterns =
    {
        ("role-tag", new Regex(@"</?\s*(system|assistant|user|human)\s*>", Flags)),
        ("inst-delimiter", new Regex(@"\[/?INST\]", Flags)),
        ("chatml-delimiter", new Regex(@"<\|\s*im_(start|end)\s*\|>", Flags)),
        ("system-prompt-marker", new Regex(@"\bsystem\s*(prompt|message)\s*:", Flags))
    };

    /// <summary>
    /// Zero-width space/ZWNJ/ZWJ, the bidi marks and overrides, soft hyphen, BOM, and the line and
    /// paragraph separators. Every one of them is invisible to a human reviewer and meaningful to a
    /// tokenizer, which is exactly the asymmetry an injected instruction hides in.
    ///
    /// Written as explicit code-point comparisons rather than a regex character class on purpose: a
    /// class built from these characters is a class whose own source is invisible, so a mistyped
    /// range cannot be seen in review and silently stops stripping part of the set. A comparison per
    /// code point is verbose and legible, and there is a test per range below.
    /// </summary>
    private static bool IsInvisible(char ch) => ch switch
    {
        '\u00AD' => true,                                  // soft hyphen
        '\uFEFF' => true,                                  // BOM / zero-width no-break space
        '\u2028' or '\u2029' => true,                      // line and paragraph separators
        >= '\u200B' and <= '\u200F' => true,              // zero-width space .. right-to-left mark
        >= '\u202A' and <= '\u202E' => true,              // bidi embedding and override controls
        >= '\u2066' and <= '\u2069' => true,              // bidi isolates
        _ => false
    };

    private static string RemoveInvisible(string text)
    {
        // Almost every document has none of these, so the scan avoids allocating in the common case.
        var needsWork = false;
        foreach (var ch in text)
        {
            if (!IsInvisible(ch))
                continue;

            needsWork = true;
            break;
        }

        if (!needsWork)
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!IsInvisible(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static readonly Regex HtmlComments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ExcessiveNewlines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TrailingWhitespace = new(@"[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static SanitizedContent Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new SanitizedContent(string.Empty, InjectionSeverity.None, Array.Empty<InjectionFinding>());

        var text = Clean(raw);

        // Detection runs AFTER cleaning, on purpose. An instruction with a zero-width space
        // inserted mid-word ("ig<ZWSP>nore previous instructions") defeats
        // every pattern above while reading normally to a model; stripping the invisible
        // characters first is what makes the patterns meet the text a model would actually see.
        var findings = new List<InjectionFinding>();

        foreach (var (name, pattern) in BlockPatterns)
            Collect(findings, InjectionSeverity.Block, name, pattern, text);

        foreach (var (name, pattern) in FlagPatterns)
            Collect(findings, InjectionSeverity.Flag, name, pattern, text);

        foreach (var (name, pattern) in NeutralizePatterns)
        {
            Collect(findings, InjectionSeverity.Neutralize, name, pattern, text);
            text = pattern.Replace(text, match => Escape(match.Value));
        }

        var severity = findings.Count == 0
            ? InjectionSeverity.None
            : findings.Max(f => f.Severity);

        return new SanitizedContent(text, severity, findings);
    }

    /// <summary>§G.4a. Ordinary cleaning, all of which also happens to make the injection patterns
    /// above meet text that has not been obfuscated.</summary>
    public static string Clean(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        text = RemoveInvisible(text);
        text = HtmlComments.Replace(text, string.Empty);

        // NFC so that visually identical text compares and embeds identically - "é" written as one
        // code point and as "e" plus a combining accent are the same word to a reader and two
        // different strings to a hash, which would defeat duplicate detection.
        text = text.Normalize(NormalizationForm.FormC);

        text = text
            .Replace('‘', '\'').Replace('’', '\'')
            .Replace('“', '"').Replace('”', '"');

        text = TrailingWhitespace.Replace(text, string.Empty);
        text = ExcessiveNewlines.Replace(text, "\n\n");

        return text.Trim();
    }

    /// <summary>
    /// Removes a header or footer line that repeats throughout a document - the page furniture a PDF
    /// extractor leaves behind. Repetition is the signal: a line appearing on every page is almost
    /// never content, and left in place it appears in most chunks, where it both wastes tokens and
    /// drags every chunk's embedding toward the same meaningless centre.
    /// </summary>
    public static string RemoveRepeatingBoilerplate(string text, int minimumOccurrences = 3)
    {
        var lines = text.Split('\n');
        if (lines.Length < minimumOccurrences * 2)
            return text;

        var counts = lines
            .Select(l => l.Trim())
            .Where(l => l.Length is > 0 and < 120)
            .GroupBy(l => l, StringComparer.Ordinal)
            .Where(g => g.Count() >= minimumOccurrences)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (counts.Count == 0)
            return text;

        // A markdown table separator repeats legitimately, as does a horizontal rule. Removing those
        // would corrupt the very tables the chunker treats as atomic.
        counts.RemoveWhere(line => line.All(c => c is '-' or '|' or ' ' or ':' or '_' or '*' or '='));

        return string.Join("\n", lines.Where(l => !counts.Contains(l.Trim())));
    }

    private static void Collect(
        List<InjectionFinding> findings, InjectionSeverity severity, string name, Regex pattern, string text)
    {
        foreach (Match match in pattern.Matches(text))
        {
            // A short window around the hit, so a reviewer sees the sentence rather than the whole
            // document or a bare pattern name.
            var start = Math.Max(0, match.Index - 40);
            var length = Math.Min(text.Length - start, match.Length + 80);

            findings.Add(new InjectionFinding(severity, name, text.Substring(start, length).Replace('\n', ' ')));
        }
    }

    /// <summary>Breaks a delimiter token without losing a character of it, by inserting a marker a
    /// human still reads correctly - "&lt;system&gt;" becomes "&lt;​system&gt;"... which would
    /// reintroduce an invisible character. So the escape is visible instead: each angle bracket and
    /// pipe is backslash-escaped, which no chat format treats as a delimiter.</summary>
    private static string Escape(string token)
    {
        var builder = new StringBuilder(token.Length * 2);

        foreach (var ch in token)
        {
            if (ch is '<' or '>' or '|' or '[' or ']')
                builder.Append('\\');

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>SHA-256 of cleaned content, lowercase hex - the value stored in
    /// KnowledgeBaseArticle.ContentHash. Computed from the CLEANED text so that two uploads
    /// differing only in line endings or invisible characters hash identically, which is what makes
    /// duplicate detection and the "skip re-embedding on a metadata-only edit" check trustworthy.</summary>
    public static string ComputeHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Clean(content)));
        return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
    }
}
