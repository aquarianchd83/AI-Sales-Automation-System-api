using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Customers;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customerService;

    public CustomersController(ICustomerService customerService)
    {
        _customerService = customerService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<CustomerDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _customerService.GetPagedAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CustomerDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _customerService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CustomerDto>> Create([FromBody] CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        var result = await _customerService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, [FromBody] UpdateCustomerRequest request, CancellationToken cancellationToken)
        => Ok(await _customerService.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _customerService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Soft-deletes several customers at once. POST rather than DELETE-with-body: request bodies on
    /// DELETE have no defined semantics in the HTTP spec and are dropped by some proxies and clients.
    /// Returns 200 with a per-id breakdown, since a batch can partially succeed.
    /// </summary>
    [HttpPost("bulk-delete")]
    public async Task<ActionResult<BulkDeleteCustomersResultDto>> BulkDelete(
        [FromBody] BulkDeleteCustomersRequest request,
        CancellationToken cancellationToken)
        => Ok(await _customerService.BulkDeleteAsync(request, cancellationToken));

    [HttpPost("{id:guid}/tags")]
    public async Task<ActionResult<CustomerDto>> AddTags(Guid id, [FromBody] AddCustomerTagsRequest request, CancellationToken cancellationToken)
        => Ok(await _customerService.AddTagsAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}/tags/{tagName}")]
    public async Task<ActionResult<CustomerDto>> RemoveTag(Guid id, string tagName, CancellationToken cancellationToken)
        => Ok(await _customerService.RemoveTagAsync(id, tagName, cancellationToken));

    /// <summary>
    /// Records consent. Until this is called a customer stays PendingOptIn and, once Phase 3
    /// enforces the send gate, cannot be messaged at all.
    /// </summary>
    [HttpPost("{id:guid}/opt-in")]
    public async Task<ActionResult<CustomerDto>> OptIn(Guid id, [FromBody] OptInCustomerRequest request, CancellationToken cancellationToken)
        => Ok(await _customerService.OptInAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/opt-out")]
    public async Task<ActionResult<CustomerDto>> OptOut(Guid id, CancellationToken cancellationToken)
        => Ok(await _customerService.OptOutAsync(id, cancellationToken));

    [HttpPost("import")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<CustomerImportResultDto>> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _customerService.ImportAsync(stream, file.FileName, cancellationToken);
        return Ok(result);
    }
}
