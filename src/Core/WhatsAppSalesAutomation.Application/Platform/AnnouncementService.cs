using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

public class AnnouncementService : IAnnouncementService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IPlatformAuditService _auditService;

    public AnnouncementService(IApplicationDbContext context, IDateTimeProvider dateTime, IPlatformAuditService auditService)
    {
        _context = context;
        _dateTime = dateTime;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<AnnouncementDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Announcements
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AnnouncementDto(a.Id, a.Title, a.Body, a.Severity, a.IsActive, a.StartsAtUtc, a.EndsAtUtc, a.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AnnouncementDto>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        return await _context.Announcements
            .Where(a => a.IsActive && (a.StartsAtUtc == null || a.StartsAtUtc <= now) && (a.EndsAtUtc == null || a.EndsAtUtc >= now))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AnnouncementDto(a.Id, a.Title, a.Body, a.Severity, a.IsActive, a.StartsAtUtc, a.EndsAtUtc, a.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<AnnouncementDto> CreateAsync(CreateAnnouncementRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var announcement = new Announcement
        {
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            Severity = request.Severity,
            IsActive = true,
            StartsAtUtc = request.StartsAtUtc,
            EndsAtUtc = request.EndsAtUtc,
            CreatedByUserId = actorUserId
        };

        _context.Announcements.Add(announcement);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.AnnouncementCreated, details: announcement.Title, cancellationToken: cancellationToken);

        return ToDto(announcement);
    }

    public async Task<AnnouncementDto> UpdateAsync(Guid id, UpdateAnnouncementRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var announcement = await _context.Announcements.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Announcement), id);

        announcement.Title = request.Title.Trim();
        announcement.Body = request.Body.Trim();
        announcement.Severity = request.Severity;
        announcement.IsActive = request.IsActive;
        announcement.StartsAtUtc = request.StartsAtUtc;
        announcement.EndsAtUtc = request.EndsAtUtc;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.AnnouncementUpdated, details: announcement.Title, cancellationToken: cancellationToken);

        return ToDto(announcement);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var announcement = await _context.Announcements.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Announcement), id);

        _context.Announcements.Remove(announcement);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.AnnouncementDeleted, details: announcement.Title, cancellationToken: cancellationToken);
    }

    private static AnnouncementDto ToDto(Announcement a) =>
        new(a.Id, a.Title, a.Body, a.Severity, a.IsActive, a.StartsAtUtc, a.EndsAtUtc, a.CreatedAt);
}
