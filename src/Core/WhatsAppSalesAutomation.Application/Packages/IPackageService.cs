using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Packages;

/// <summary>CRUD over the packages a tenant sells, plus the revenue projection across them.</summary>
public interface IPackageService
{
    Task<PagedResult<PackageDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<PackageDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PackageDto> CreateAsync(SavePackageRequest request, CancellationToken cancellationToken = default);

    Task<PackageDto> UpdateAsync(Guid id, SavePackageRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PackageSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that a customer bought a package. Feeds the Revenue report.</summary>
    Task<PackageSaleDto> RecordSaleAsync(RecordPackageSaleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Most recent sales first.</summary>
    Task<PagedResult<PackageSaleDto>> GetSalesPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a sale recorded by mistake.</summary>
    Task DeleteSaleAsync(Guid id, CancellationToken cancellationToken = default);
}
