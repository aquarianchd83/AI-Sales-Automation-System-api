using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Assets;

public sealed record SniffedImage(string ContentType, string Extension, int? Width, int? Height);

/// <summary>
/// Decides what a file is from its first bytes, never from the name or the type the browser claims: a program renamed to .jpg is refused.
/// Only JPEG, PNG and WebP are accepted. Dimensions are read where the format makes that cheap.
/// </summary>
public static class ImageSniffer
{
    public static SniffedImage? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return new SniffedImage("image/png", "png", ReadInt32BigEndian(bytes, 16), ReadInt32BigEndian(bytes, 20));

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            var (w, h) = JpegSize(bytes);
            return new SniffedImage("image/jpeg", "jpg", w, h);
        }

        if (bytes.Length >= 30 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            // VP8X (extended) stores the canvas size as 24-bit little-endian values minus one.
            if (bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == 'X')
                return new SniffedImage("image/webp", "webp", 1 + (bytes[24] | bytes[25] << 8 | bytes[26] << 16), 1 + (bytes[27] | bytes[28] << 8 | bytes[29] << 16));
            return new SniffedImage("image/webp", "webp", null, null);
        }

        return null;
    }

    private static int? ReadInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
        return value is > 0 and < 100_000 ? value : null;
    }

    private static (int? Width, int? Height) JpegSize(ReadOnlySpan<byte> bytes)
    {
        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = bytes[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            var isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                var height = bytes[i + 5] << 8 | bytes[i + 6];
                var width = bytes[i + 7] << 8 | bytes[i + 8];
                return (width > 0 ? width : null, height > 0 ? height : null);
            }

            var length = bytes[i + 2] << 8 | bytes[i + 3];
            if (length < 2)
                break;
            i += 2 + length;
        }

        return (null, null);
    }

    public static string Checksum(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>
/// Ranks a tenant's own pictures for a scene by the words they share with the scene's picture note. Plain word overlap: it finds "reception"
/// for a scene about the reception desk, it does not understand what a picture shows - which is why the tenant always makes the final choice.
/// </summary>
public static class AssetMatcher
{
    private static readonly Regex Word = new("[\\p{L}\\p{N}]+", RegexOptions.Compiled);

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "for", "with", "is", "are", "by", "as", "from", "this", "that", "it", "its", "our", "your", "we", "you",
        "shot", "scene", "image", "picture", "photo", "showing", "shows", "show", "view"
    };

    public static IReadOnlyList<string> Tokens(string? text) =>
        Word.Matches((text ?? string.Empty).ToLowerInvariant()).Select(m => Stem(m.Value)).Where(t => t.Length > 2 && !Stop.Contains(t)).Distinct().ToList();

    /// <summary>The words to look for a scene: its picture note, else its on-screen text, else the start of what is said.</summary>
    public static string QueryFor(ContentScene scene)
    {
        foreach (var candidate in new[] { scene.VisualHint, scene.OnScreenText, scene.Narration })
        {
            var tokens = Tokens(candidate);
            if (tokens.Count > 0)
                return string.Join(' ', tokens.Take(6));
        }

        return string.Empty;
    }

    public static (double Score, string Reason) Score(IReadOnlyList<string> queryTokens, StudioAsset asset)
    {
        if (queryTokens.Count == 0)
            return (0, "No words to match on yet.");
        var have = Tokens(string.Join(' ', asset.Name, asset.Description, asset.Tags)).ToHashSet();
        var hits = queryTokens.Where(have.Contains).ToList();
        if (hits.Count == 0)
            return (0, "No shared words.");
        return (Math.Round((double)hits.Count / queryTokens.Count, 3), "Matches: " + string.Join(", ", hits));
    }

    /// <summary>"reception, Front Desk, reception" -> "reception, front desk"; at most 10 tags of 30 characters.</summary>
    public static string? NormaliseTags(string? tags)
    {
        var list = (tags ?? string.Empty).Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Select(t => t.Length > 30 ? t[..30] : t).Distinct().Take(10).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private static string Stem(string word) => word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal)
        ? word[..^3] + "y"
        : word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;

    public static string NameFromFile(string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension((fileName ?? string.Empty).Replace('\\', '/').Split('/').Last()).Replace('_', ' ').Replace('-', ' ').Trim();
        var cleaned = new StringBuilder();
        foreach (var c in name)
            if (!char.IsControl(c))
                cleaned.Append(c);
        var result = cleaned.ToString().Trim();
        return result.Length == 0 ? "Picture" : result.Length > 200 ? result[..200] : result;
    }
}
