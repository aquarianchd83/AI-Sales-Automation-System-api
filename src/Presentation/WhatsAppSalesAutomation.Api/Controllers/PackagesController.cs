using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Packages;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The packages a tenant sells to its own customers, and the revenue they are projected to bring in.</summary>
[ApiController]
[Route("api/v1/packages")]
[Authorize]
public class PackagesController : ControllerBase
{
    private readonly IPackageService _packageService;

    public PackagesController(IPackageService packageService)
    {
        _packageService = packageService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PackageDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _packageService.GetPagedAsync(request, cancellationToken));

    [HttpGet("summary")]
    public async Task<ActionResult<PackageSummaryDto>> GetSummary(CancellationToken cancellationToken)
        => Ok(await _packageService.GetSummaryAsync(cancellationToken));

    /// <summary>Recent sales, newest first.</summary>
    [HttpGet("sales")]
    public async Task<ActionResult<PagedResult<PackageSaleDto>>> GetSales([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _packageService.GetSalesPagedAsync(request, cancellationToken));

    /// <summary>Records that a customer bought a package - the data the Revenue report is built from.</summary>
    [HttpPost("sales")]
    public async Task<ActionResult<PackageSaleDto>> RecordSale([FromBody] RecordPackageSaleRequest request, CancellationToken cancellationToken)
    {
        var result = await _packageService.RecordSaleAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpDelete("sales/{id:guid}")]
    public async Task<IActionResult> DeleteSale(Guid id, CancellationToken cancellationToken)
    {
        await _packageService.DeleteSaleAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PackageDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _packageService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<PackageDto>> Create([FromBody] SavePackageRequest request, CancellationToken cancellationToken)
    {
        var result = await _packageService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PackageDto>> Update(Guid id, [FromBody] SavePackageRequest request, CancellationToken cancellationToken)
        => Ok(await _packageService.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _packageService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
