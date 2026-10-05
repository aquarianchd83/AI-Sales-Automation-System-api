using WhatsAppSalesAutomation.Application.Common.Exceptions;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>What Meta accepts above a template's body: an image (JPEG or PNG, at most 5 MB) or a video (MP4 or
/// 3GPP, at most 16 MB). Meta fixes which of the two a template has when it creates it, so a template on Meta can
/// swap its file only for another of the same kind.</summary>
public static class TemplateHeaderMedia
{
    public const string Image = "IMAGE";
    public const string Video = "VIDEO";

    public const long MaxImageBytes = 5 * 1024 * 1024;
    public const long MaxVideoBytes = 16 * 1024 * 1024;

    private static readonly string[] ImageContentTypes = { "image/jpeg", "image/png" };
    private static readonly string[] VideoContentTypes = { "video/mp4", "video/3gpp" };

    /// <summary>Meta's header format for a file of this type: <see cref="Image"/>, <see cref="Video"/>, or null when Meta takes neither.</summary>
    public static string? FormatOf(string contentType)
    {
        if (ImageContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return Image;
        if (VideoContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return Video;
        return null;
    }

    /// <summary>Throws a <see cref="ConflictException"/> when Meta would not take this file as a template header.</summary>
    public static void EnsureUsable(string fileName, string contentType, long sizeBytes)
    {
        var format = FormatOf(contentType)
            ?? throw new ConflictException($"'{fileName}' can't go above a template message: Meta accepts a JPEG or PNG image, or an MP4 or 3GPP video.");

        if (format == Image && sizeBytes > MaxImageBytes)
            throw new ConflictException($"'{fileName}' is too large for a template image: Meta allows at most 5 MB.");
        if (format == Video && sizeBytes > MaxVideoBytes)
            throw new ConflictException($"'{fileName}' is too large for a template video: Meta allows at most 16 MB.");
    }

    /// <summary>Throws when a template already on Meta would swap its header for a file of the other kind. A null
    /// <paramref name="currentContentType"/> (the current file is gone) lets the swap through.</summary>
    public static void EnsureSameKind(string templateName, string? currentContentType, string newContentType)
    {
        if (currentContentType is null || FormatOf(currentContentType) == FormatOf(newContentType))
            return;

        var current = Describe(currentContentType);
        throw new ConflictException(
            $"'{templateName}' is on Meta with a{(current == "image" ? "n" : "")} {current} above the message, and Meta fixes that when a template is created. " +
            $"Swap it for another {current}, or create a new template to use a {Describe(newContentType)}.");
    }

    /// <summary>"image" or "video", for messages.</summary>
    public static string Describe(string? contentType) =>
        contentType is not null && FormatOf(contentType) == Video ? "video" : "image";
}
