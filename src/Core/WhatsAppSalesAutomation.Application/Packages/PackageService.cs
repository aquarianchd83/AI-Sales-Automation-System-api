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

    public PackageService(IApplicationDbContext context, IValidator<SavePackageRequest> validator)
    {
        _context = context;
        _validator = validator;
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

        _context.SalesPackages.Remove(package);
        await _context.SaveChangesAsync(cancellationToken);
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
