using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Bookings;
using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The demos, visits and appointments customers agreed with the AI, newest first. The dashboard's "Demos scheduled" tile counts them (totalCount of a
/// one-row page) and opens this list.</summary>
[ApiController]
[Route("api/v1/bookings")]
[Authorize]
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookings;

    public BookingsController(IBookingService bookings) => _bookings = bookings;

    [HttpGet]
    public async Task<ActionResult<PagedResult<BookingDto>>> GetPaged([FromQuery] BookingQuery query, CancellationToken cancellationToken)
        => Ok(await _bookings.GetPagedAsync(query, cancellationToken));

    /// <summary>Marks a booking Pending, Done or Reschedule.</summary>
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<BookingDto>> SetStatus(Guid id, [FromBody] SetBookingStatusRequest request, CancellationToken cancellationToken)
        => Ok(await _bookings.SetStatusAsync(id, request, cancellationToken));
}
