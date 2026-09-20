using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class FaqService : IFaqService
{
    private readonly IApplicationDbContext _context;
    private readonly IPlatformAuditService _auditService;

    public FaqService(IApplicationDbContext context, IPlatformAuditService auditService)
    {
        _context = context;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<FaqEntryDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.FaqEntries
            .OrderBy(f => f.DisplayOrder).ThenByDescending(f => f.CreatedAt)
            .Select(f => new FaqEntryDto(f.Id, f.Question, f.Answer, f.Category, f.Status, f.DisplayOrder, f.CreatedAt, f.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FaqEntryDto>> GetPublishedAsync(CancellationToken cancellationToken = default)
    {
        return await _context.FaqEntries
            .Where(f => f.Status == ContentStatus.Published)
            .OrderBy(f => f.DisplayOrder).ThenByDescending(f => f.CreatedAt)
            .Select(f => new FaqEntryDto(f.Id, f.Question, f.Answer, f.Category, f.Status, f.DisplayOrder, f.CreatedAt, f.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<FaqEntryDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await _context.FaqEntries.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(FaqEntry), id);

        return ToDto(entry);
    }

    public async Task<FaqEntryDto> CreateAsync(CreateFaqEntryRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var entry = new FaqEntry
        {
            Question = request.Question.Trim(),
            Answer = request.Answer.Trim(),
            Category = request.Category?.Trim(),
            DisplayOrder = request.DisplayOrder,
            Status = ContentStatus.Draft,
            CreatedByUserId = actorUserId
        };

        _context.FaqEntries.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.FaqEntryCreated, details: entry.Question, cancellationToken: cancellationToken);

        return ToDto(entry);
    }

    public async Task<FaqEntryDto> UpdateAsync(Guid id, UpdateFaqEntryRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var entry = await _context.FaqEntries.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(FaqEntry), id);

        entry.Question = request.Question.Trim();
        entry.Answer = request.Answer.Trim();
        entry.Category = request.Category?.Trim();
        entry.Status = request.Status;
        entry.DisplayOrder = request.DisplayOrder;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.FaqEntryUpdated, details: entry.Question, cancellationToken: cancellationToken);

        return ToDto(entry);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var entry = await _context.FaqEntries.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(FaqEntry), id);

        _context.FaqEntries.Remove(entry);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.FaqEntryDeleted, details: entry.Question, cancellationToken: cancellationToken);
    }

    private static FaqEntryDto ToDto(FaqEntry f) =>
        new(f.Id, f.Question, f.Answer, f.Category, f.Status, f.DisplayOrder, f.CreatedAt, f.UpdatedAt);
}
