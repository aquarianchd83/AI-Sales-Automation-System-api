using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

public record AnnouncementDto(
    Guid Id,
    string Title,
    string Body,
    AnnouncementSeverity Severity,
    bool IsActive,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc,
    DateTime CreatedAt);

public record CreateAnnouncementRequest(
    string Title,
    string Body,
    AnnouncementSeverity Severity = AnnouncementSeverity.Info,
    DateTime? StartsAtUtc = null,
    DateTime? EndsAtUtc = null);

public record UpdateAnnouncementRequest(
    string Title,
    string Body,
    AnnouncementSeverity Severity,
    bool IsActive,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);
