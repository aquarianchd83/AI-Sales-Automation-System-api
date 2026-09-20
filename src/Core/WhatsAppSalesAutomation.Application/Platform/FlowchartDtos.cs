using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public record FlowchartDto(
    Guid Id,
    string Title,
    string? Description,
    string? Category,
    string DiagramJson,
    ContentStatus Status,
    int DisplayOrder,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record CreateFlowchartRequest(
    string Title,
    string? Description,
    string? Category,
    string DiagramJson,
    int DisplayOrder = 0);

public record UpdateFlowchartRequest(
    string Title,
    string? Description,
    string? Category,
    string DiagramJson,
    ContentStatus Status,
    int DisplayOrder);
