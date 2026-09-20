using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class FlowchartService : IFlowchartService
{
    private readonly IApplicationDbContext _context;
    private readonly IPlatformAuditService _auditService;

    public FlowchartService(IApplicationDbContext context, IPlatformAuditService auditService)
    {
        _context = context;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<FlowchartDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Flowcharts
            .OrderBy(f => f.DisplayOrder).ThenByDescending(f => f.CreatedAt)
            .Select(f => new FlowchartDto(f.Id, f.Title, f.Description, f.Category, f.DiagramJson, f.Status, f.DisplayOrder, f.CreatedAt, f.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FlowchartDto>> GetPublishedAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Flowcharts
            .Where(f => f.Status == ContentStatus.Published)
            .OrderBy(f => f.DisplayOrder).ThenByDescending(f => f.CreatedAt)
            .Select(f => new FlowchartDto(f.Id, f.Title, f.Description, f.Category, f.DiagramJson, f.Status, f.DisplayOrder, f.CreatedAt, f.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<FlowchartDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var flowchart = await _context.Flowcharts.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Flowchart), id);

        return ToDto(flowchart);
    }

    public async Task<FlowchartDto> CreateAsync(CreateFlowchartRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var flowchart = new Flowchart
        {
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Category = request.Category?.Trim(),
            DiagramJson = request.DiagramJson,
            DisplayOrder = request.DisplayOrder,
            Status = ContentStatus.Draft,
            CreatedByUserId = actorUserId
        };

        _context.Flowcharts.Add(flowchart);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.FlowchartCreated, details: flowchart.Title, cancellationToken: cancellationToken);

        return ToDto(flowchart);
    }

    public async Task<FlowchartDto> UpdateAsync(Guid id, UpdateFlowchartRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var flowchart = await _context.Flowcharts.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Flowchart), id);

        flowchart.Title = request.Title.Trim();
        flowchart.Description = request.Description?.Trim();
        flowchart.Category = request.Category?.Trim();
        flowchart.DiagramJson = request.DiagramJson;
        flowchart.Status = request.Status;
        flowchart.DisplayOrder = request.DisplayOrder;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.FlowchartUpdated, details: flowchart.Title, cancellationToken: cancellationToken);

        return ToDto(flowchart);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var flowchart = await _context.Flowcharts.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Flowchart), id);

        _context.Flowcharts.Remove(flowchart);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.FlowchartDeleted, details: flowchart.Title, cancellationToken: cancellationToken);
    }

    private static FlowchartDto ToDto(Flowchart f) =>
        new(f.Id, f.Title, f.Description, f.Category, f.DiagramJson, f.Status, f.DisplayOrder, f.CreatedAt, f.UpdatedAt);
}
