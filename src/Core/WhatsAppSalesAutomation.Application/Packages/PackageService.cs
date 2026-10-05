using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Packages;

namespace WhatsAppSalesAutomation.Application.Packages;

public class PackageService : IPackageService
{
    private readonly IApplicationDbContext _context;
    private readonly IValidator<SavePackageRequest> _validator;
    private readonly IValidator<RecordPackageSaleRequest> _saleValidator;
    private readonly IDateTimeProvider _clock;

    public PackageService(
        IApplicationDbContext context,
        IValidator<SavePackageRequest> validator,
        IValidator<RecordPackageSaleRequest> saleValidator,
        IDateTimeProvider clock)
    {
        _context = context;
        _validator = validator;
        _saleValidator = saleValidator;
        _clock = clock;
    }

    public async Task<PagedResult<PackageDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.SalesPackages.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(p => p.Name.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PackageDto>(rows.Select(ToDto).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<PackageDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var package = await _context.SalesPackages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(SalesPackage), id);
        return ToDto(package);
    }

    public async Task<PackageDto> CreateAsync(SavePackageRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var name = request.Name.Trim();
        await EnsureNameIsFreeAsync(name, excludingId: null, cancellationToken);

        var package = new SalesPackage();
        Apply(package, request, name);

        _context.SalesPackages.Add(package);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(package);
    }

    public async Task<PackageDto> UpdateAsync(Guid id, SavePackageRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var package = await _context.SalesPackages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(SalesPackage), id);

        var name = request.Name.Trim();
        if (!string.Equals(name, package.Name, StringComparison.OrdinalIgnoreCase))
            await EnsureNameIsFreeAsync(name, excludingId: id, cancellationToken);

        Apply(package, request, name);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(package);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var package = await _context.SalesPackages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(SalesPackage), id);

        // Sales are what the Revenue report is built from, so a package that has any stays (deactivate it instead).
        var sales = await _context.PackageSales.CountAsync(x => x.PackageId == id, cancellationToken);
        if (sales > 0)
            throw new ConflictException(
                $"Package '{package.Name}' has {sales} recorded sale(s) and cannot be deleted - mark it inactive instead.");

        _context.SalesPackages.Remove(package);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PackageSaleDto> RecordSaleAsync(RecordPackageSaleRequest request, CancellationToken cancellationToken = default)
    {
        await _saleValidator.ValidateAndThrowAsync(request, cancellationToken);

        var package = await _context.SalesPackages.FirstOrDefaultAsync(p => p.Id == request.PackageId, cancellationToken)
            ?? throw new NotFoundException(nameof(SalesPackage), request.PackageId);

        var now = _clock.UtcNow;
        var soldAt = request.SoldAt?.ToUniversalTime() ?? now;
        if (soldAt > now.AddDays(1))
            throw SaleDateError("A sale cannot be dated in the future.");
        if (soldAt < now.AddYears(-5))
            throw SaleDateError("A sale cannot be dated more than five years back.");

        string? customerName = null;
        if (request.CustomerId is { } customerId)
        {
            var customer = await _context.Customers
                .Where(c => c.Id == customerId)
                .Select(c => new { c.FirstName, c.LastName, c.PhoneNumberE164 })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Customer", customerId);
            customerName = CustomerDisplayName(customer.FirstName, customer.LastName, customer.PhoneNumberE164);
        }

        var sale = new PackageSale
        {
            PackageId = package.Id,
            CustomerId = request.CustomerId,
            Amount = request.Amount ?? package.Price,
            SoldAt = soldAt,
        };

        _context.PackageSales.Add(sale);
        await _context.SaveChangesAsync(cancellationToken);

        return new PackageSaleDto(sale.Id, package.Id, package.Name, sale.CustomerId, customerName, sale.Amount, sale.SoldAt);
    }

    public async Task<PagedResult<PackageSaleDto>> GetSalesPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.PackageSales.AsQueryable();
        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(s => s.SoldAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(s => new { s.Id, s.PackageId, s.CustomerId, s.Amount, s.SoldAt })
            .ToListAsync(cancellationToken);

        var packageIds = rows.Select(r => r.PackageId).Distinct().ToList();
        var packageNames = await _context.SalesPackages
            .Where(p => packageIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var customerIds = rows.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).Distinct().ToList();
        var customers = await _context.Customers
            .Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.FirstName, c.LastName, c.PhoneNumberE164 })
            .ToListAsync(cancellationToken);
        var customerNames = customers.ToDictionary(c => c.Id, c => CustomerDisplayName(c.FirstName, c.LastName, c.PhoneNumberE164));

        var items = rows.Select(r => new PackageSaleDto(
            r.Id,
            r.PackageId,
            packageNames.GetValueOrDefault(r.PackageId, "(deleted package)"),
            r.CustomerId,
            r.CustomerId is { } cid ? customerNames.GetValueOrDefault(cid) : null,
            r.Amount,
            r.SoldAt)).ToList();

        return new PagedResult<PackageSaleDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task DeleteSaleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sale = await _context.PackageSales.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PackageSale), id);

        _context.PackageSales.Remove(sale);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A FluentValidation error carrying its message, so the 400 response says what is wrong.</summary>
    private static ValidationException SaleDateError(string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(nameof(RecordPackageSaleRequest.SoldAt), message) });

    private static string CustomerDisplayName(string? first, string? last, string phone)
    {
        var name = $"{first} {last}".Trim();
        return name.Length > 0 ? name : phone;
    }

    public async Task<PackageSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        // Summed client-side: SQLite (used by the tests) cannot SUM a decimal expression, and a tenant's
        // package list is small enough that loading two columns is cheaper than being clever.
        var rows = await _context.SalesPackages
            .Where(p => p.IsActive)
            .Select(p => new { p.Price, p.ExpectedSales })
            .ToListAsync(cancellationToken);

        return new PackageSummaryDto(
            rows.Count,
            rows.Sum(r => r.ExpectedSales),
            rows.Sum(r => r.Price * r.ExpectedSales));
    }

    private static void Apply(SalesPackage package, SavePackageRequest request, string name)
    {
        package.Name = name;
        package.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        package.Price = request.Price;
        package.DurationValue = request.DurationValue;
        package.DurationUnit = request.DurationUnit;
        package.ExpectedSales = request.ExpectedSales;
        package.IsActive = request.IsActive;

        var features = (request.Features ?? Array.Empty<string>())
            .Select(f => f?.Trim() ?? string.Empty)
            .Where(f => f.Length > 0)
            .ToList();
        package.FeaturesText = features.Count == 0 ? null : string.Join('\n', features);
    }

    private static PackageDto ToDto(SalesPackage p) => new(
        p.Id,
        p.Name,
        p.Description,
        p.Price,
        p.DurationValue,
        p.DurationUnit,
        string.IsNullOrEmpty(p.FeaturesText)
            ? Array.Empty<string>()
            : p.FeaturesText.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        p.ExpectedSales,
        p.Price * p.ExpectedSales,
        p.IsActive,
        p.CreatedAt);

    private async Task EnsureNameIsFreeAsync(string name, Guid? excludingId, CancellationToken cancellationToken)
    {
        var taken = await _context.SalesPackages
            .AnyAsync(p => p.Name.ToLower() == name.ToLower() && (excludingId == null || p.Id != excludingId), cancellationToken);

        if (taken)
            throw new ConflictException($"A package named '{name}' already exists.");
    }
}
