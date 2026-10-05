using FluentValidation;
using FluentValidation.Results;

namespace WhatsAppSalesAutomation.Application.Media;

/// <summary>Rules for the still frame stored beside a video: a small picture, never a second video or a document.</summary>
public static class MediaThumbnails
{
    public const long MaxSizeBytes = 2 * 1024 * 1024;

    private static readonly string[] AllowedContentTypes = { "image/jpeg", "image/png", "image/webp" };

    public static void Check(long sizeBytes, string contentType)
    {
        if (sizeBytes <= 0)
            throw Invalid("The thumbnail is empty.");
        if (sizeBytes > MaxSizeBytes)
            throw Invalid($"The thumbnail exceeds the {MaxSizeBytes / (1024 * 1024)} MB limit.");
        if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw Invalid("The thumbnail must be a JPEG, PNG or WebP picture.");
    }

    /// <summary>"promo.mp4" -> "promo-thumb.jpg", so the stored file is recognisable beside its video.</summary>
    public static string FileNameFor(string videoFileName, string contentType)
    {
        var extension = contentType.ToLowerInvariant() switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        return $"{Path.GetFileNameWithoutExtension(videoFileName)}-thumb{extension}";
    }

    private static ValidationException Invalid(string message) => new(new[] { new ValidationFailure("thumbnail", message) });
}
