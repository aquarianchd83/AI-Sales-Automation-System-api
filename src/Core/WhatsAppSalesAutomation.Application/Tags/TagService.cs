using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Tags;

public class TagService : ITagService
{
    private readonly IApplicationDbContext _context;
    private readonly IValidator<CreateTagRequest> _createValidator;
    private readonly IValidator<UpdateTagRequest> _updateValidator;

    public TagService(
        IApplicationDbContext context,
        IValidator<CreateTagRequest> createValidator,
        IValidator<UpdateTagRequest> updateValidator)
    {
        _context = context;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<PagedResult<TagDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.CustomerTags.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(t => t.Name.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Projecting the count in SQL rather than loading Customers - a popular tag could otherwise
        // drag thousands of rows into memory just to call .Count on them.
        var items = await query
            .OrderBy(t => t.Name)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(t => new TagDto(t.Id, t.Name, t.Customers.Count, t.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<TagDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task<TagDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CustomerTags
            .Where(t => t.Id == id)
            .Select(t => new TagDto(t.Id, t.Name, t.Customers.Count, t.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(CustomerTag), id);
    }

    public async Task<TagDto> CreateAsync(CreateTagRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var name = request.Name.Trim();
        await EnsureNameIsFreeAsync(name, excludingTagId: null, cancellationToken);

        var tag = new CustomerTag { Name = name };

        _context.CustomerTags.Add(tag);
        await _context.SaveChangesAsync(cancellationToken);

        return new TagDto(tag.Id, tag.Name, 0, tag.CreatedAt);
    }

    public async Task<TagDto> UpdateAsync(Guid id, UpdateTagRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(CustomerTag), id);

        var name = request.Name.Trim();

        // Renaming a tag re-labels it everywhere it is applied - the join table holds ids, not names.
        if (!string.Equals(name, tag.Name, StringComparison.OrdinalIgnoreCase))
            await EnsureNameIsFreeAsync(name, excludingTagId: id, cancellationToken);

        tag.Name = name;
        await _context.SaveChangesAsync(cancellationToken);

        var customerCount = await _context.Customers.CountAsync(c => c.Tags.Any(t => t.Id == id), cancellationToken);

        return new TagDto(tag.Id, tag.Name, customerCount, tag.CreatedAt);
    }

    public async Task DeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(CustomerTag), id);

        if (!force)
        {
            var inUse = await _context.Customers.CountAsync(c => c.Tags.Any(t => t.Id == id), cancellationToken);
            if (inUse > 0)
                throw new ConflictException(
                    $"Tag '{tag.Name}' is applied to {inUse} customer(s). Re-send with force=true to delete it and remove those assignments.");
        }

        // CustomerTagMap rows go with it via the cascade configured on the join table.
        _context.CustomerTags.Remove(tag);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureNameIsFreeAsync(string name, Guid? excludingTagId, CancellationToken cancellationToken)
    {
        var taken = await _context.CustomerTags
            .AnyAsync(t => t.Name.ToLower() == name.ToLower() && (excludingTagId == null || t.Id != excludingTagId), cancellationToken);

        if (taken)
            throw new ConflictException($"A tag named '{name}' already exists.");
    }
}
