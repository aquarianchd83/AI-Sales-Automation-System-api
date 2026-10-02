namespace WhatsAppSalesAutomation.Application.Media;

public record MediaAssetDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    DateTime CreatedAt,
    bool IsPublicUrl);

public record AddMediaFromUrlRequest(string Url);
