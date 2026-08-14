using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.MessageTemplates;

public class MessageTemplateService : IMessageTemplateService
{
    private readonly IApplicationDbContext _context;
    private readonly IValidator<CreateMessageTemplateRequest> _createValidator;
    private readonly IValidator<UpdateMessageTemplateRequest> _updateValidator;
    private readonly IValidator<ReviewMessageTemplateRequest> _reviewValidator;

    public MessageTemplateService(
        IApplicationDbContext context,
        IValidator<CreateMessageTemplateRequest> createValidator,
        IValidator<UpdateMessageTemplateRequest> updateValidator,
        IValidator<ReviewMessageTemplateRequest> reviewValidator)
    {
        _context = context;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _reviewValidator = reviewValidator;
    }

    public async Task<PagedResult<MessageTemplateDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.MessageTemplates.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(t => t.Name.Contains(search) || t.WhatsAppTemplateName.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<MessageTemplateDto>(items.Select(ToDto).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<MessageTemplateDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await FindOrThrowAsync(id, cancellationToken));

    public async Task<MessageTemplateDto> CreateAsync(CreateMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var exists = await _context.MessageTemplates.AnyAsync(
            t => t.WhatsAppTemplateName == request.WhatsAppTemplateName && t.Language == request.Language,
            cancellationToken);
        if (exists)
            throw new ConflictException($"A template named '{request.WhatsAppTemplateName}' already exists for language '{request.Language}'.");

        var template = new MessageTemplate
        {
            Name = request.Name,
            Language = request.Language,
            Category = Enum.Parse<TemplateCategory>(request.Category, ignoreCase: true),
            WhatsAppTemplateName = request.WhatsAppTemplateName,
            BodyText = request.BodyText,
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending
        };

        _context.MessageTemplates.Add(template);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(template);
    }

    public async Task<MessageTemplateDto> UpdateAsync(Guid id, UpdateMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var template = await FindOrThrowAsync(id, cancellationToken);

        // Editing the wording of an already-approved template is exactly what Meta requires a fresh
        // review for - a change to Approved content silently staying Approved would let an
        // unreviewed message go out under an approved template's name.
        if (template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved && request.BodyText != template.BodyText)
            template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;

        template.BodyText = request.BodyText;
        template.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(template);
    }

    public async Task<MessageTemplateDto> ReviewAsync(Guid id, ReviewMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _reviewValidator.ValidateAndThrowAsync(request, cancellationToken);

        var template = await FindOrThrowAsync(id, cancellationToken);
        template.WhatsAppTemplateStatus = Enum.Parse<WhatsAppTemplateStatus>(request.Status, ignoreCase: true);

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(template);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var template = await FindOrThrowAsync(id, cancellationToken);

        var inUse = await _context.CampaignSteps.AnyAsync(s => s.MessageTemplateId == id, cancellationToken);
        if (inUse)
            throw new ConflictException($"Template '{template.Name}' is referenced by one or more campaign steps and cannot be deleted.");

        _context.MessageTemplates.Remove(template);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<MessageTemplate> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(MessageTemplate), id);

    private static MessageTemplateDto ToDto(MessageTemplate t) => new(
        t.Id, t.Name, t.Language, t.Category.ToString(), t.WhatsAppTemplateName,
        t.WhatsAppTemplateStatus.ToString(), t.BodyText, t.IsActive, t.CreatedAt);
}
