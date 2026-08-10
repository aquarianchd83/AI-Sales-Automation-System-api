using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Customers;

public class CustomerService : ICustomerService
{
    private static readonly Regex PhoneNumberRegex = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);

    private readonly IApplicationDbContext _context;
    private readonly ICustomerImportService _importService;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<CreateCustomerRequest> _createValidator;
    private readonly IValidator<UpdateCustomerRequest> _updateValidator;

    public CustomerService(
        IApplicationDbContext context,
        ICustomerImportService importService,
        IDateTimeProvider dateTime,
        IValidator<CreateCustomerRequest> createValidator,
        IValidator<UpdateCustomerRequest> updateValidator)
    {
        _context = context;
        _importService = importService;
        _dateTime = dateTime;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<PagedResult<CustomerDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.Customers.Include(c => c.Tags).AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(c =>
                c.PhoneNumberE164.Contains(search) ||
                (c.FirstName != null && c.FirstName.Contains(search)) ||
                (c.LastName != null && c.LastName.Contains(search)) ||
                (c.Email != null && c.Email.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CustomerDto>(items.Select(c => c.ToDto()).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<CustomerDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        return customer.ToDto();
    }

    public async Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var exists = await _context.Customers.AnyAsync(c => c.PhoneNumberE164 == request.PhoneNumberE164, cancellationToken);
        if (exists)
            throw new ConflictException($"A customer with phone number '{request.PhoneNumberE164}' already exists.");

        var customer = new Customer
        {
            PhoneNumberE164 = request.PhoneNumberE164,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Source = request.Source,
            PreferredLanguage = request.PreferredLanguage,
            AssignedAgentId = request.AssignedAgentId,
            OptInStatus = OptInStatus.PendingOptIn
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task<CustomerDto> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        customer.FirstName = request.FirstName;
        customer.LastName = request.LastName;
        customer.Email = request.Email;
        customer.Source = request.Source;
        customer.PreferredLanguage = request.PreferredLanguage;
        customer.AssignedAgentId = request.AssignedAgentId;

        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        customer.IsDeleted = true;
        customer.DeletedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomerDto> AddTagsAsync(Guid id, AddCustomerTagsRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        var requestedNames = request.TagNames
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in requestedNames)
        {
            var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower(), cancellationToken);
            if (tag is null)
            {
                tag = new CustomerTag { Name = tagName };
                _context.CustomerTags.Add(tag);
            }

            if (customer.Tags.All(t => t.Id != tag.Id))
                customer.Tags.Add(tag);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task<CustomerDto> OptOutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        customer.OptInStatus = OptInStatus.OptedOut;
        customer.OptOutTimestamp = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task<CustomerImportResultDto> ImportAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var rows = await _importService.ParseAsync(fileStream, fileName, cancellationToken);
        var errors = new List<CustomerImportRowError>();
        var imported = 0;
        var skipped = 0;

        // Note: this checks/inserts row-by-row for correctness and simplicity. For very large
        // imports (10k+ rows) this is a good candidate to batch-load existing phone numbers and
        // tags up front - flagged here rather than optimized prematurely.
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(row.PhoneNumber) || !PhoneNumberRegex.IsMatch(row.PhoneNumber))
            {
                errors.Add(new CustomerImportRowError(row.RowNumber, $"Invalid or missing phone number '{row.PhoneNumber}'. Expected E.164 format, e.g. +15551234567."));
                continue;
            }

            var exists = await _context.Customers.AnyAsync(c => c.PhoneNumberE164 == row.PhoneNumber, cancellationToken);
            if (exists)
            {
                skipped++;
                continue;
            }

            var customer = new Customer
            {
                PhoneNumberE164 = row.PhoneNumber,
                FirstName = row.FirstName,
                LastName = row.LastName,
                Email = row.Email,
                Source = "Import",
                OptInStatus = OptInStatus.PendingOptIn
            };

            foreach (var tagName in row.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower(), cancellationToken);
                if (tag is null)
                {
                    tag = new CustomerTag { Name = tagName };
                    _context.CustomerTags.Add(tag);
                }

                customer.Tags.Add(tag);
            }

            _context.Customers.Add(customer);
            imported++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new CustomerImportResultDto(rows.Count, imported, skipped, errors.Count, errors);
    }
}
