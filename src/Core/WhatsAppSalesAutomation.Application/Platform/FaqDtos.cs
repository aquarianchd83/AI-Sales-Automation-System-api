using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public record FaqEntryDto(
    Guid Id,
    string Question,
    string Answer,
    string? Category,
    ContentStatus Status,
    int DisplayOrder,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record CreateFaqEntryRequest(
    string Question,
    string Answer,
    string? Category,
    int DisplayOrder = 0);

public record UpdateFaqEntryRequest(
    string Question,
    string Answer,
    string? Category,
    ContentStatus Status,
    int DisplayOrder);
