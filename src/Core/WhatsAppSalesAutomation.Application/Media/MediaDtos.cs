namespace WhatsAppSalesAutomation.Application.Media;

public record MediaAssetDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    DateTime CreatedAt,
    bool IsPublicUrl,
    string? PreviewUrl = null,
    string? ThumbnailUrl = null);

public record AddMediaFromUrlRequest(string Url);
