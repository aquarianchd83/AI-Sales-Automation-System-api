using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Bookings;

/// <summary>One agreed demo, visit or appointment, for the tenant's list.</summary>
/// <param name="Status">"Pending", "Done" or "Reschedule".</param>
/// <param name="NewSlotUtc">The new time picked when it was marked Reschedule, if one was.</param>
public record BookingDto(
    Guid Id, DateTime CreatedAtUtc, string Kind, string Summary, string? CustomerName, string? CustomerPhone, Guid? ConversationId, bool IsTest, string Status,
    DateTime? NewSlotUtc = null);

public record BookingQuery : PagedRequest
{
    /// <summary>Include what the tenant booked while trying its own AI in Test chat. Left out, only real customers' bookings are listed and counted.</summary>
    public bool IncludeTests { get; init; }

    /// <summary>Only bookings in this status: "Pending", "Done" or "Reschedule". Left out: every status.</summary>
    public string? Status { get; init; }

    /// <summary>Only bookings still to happen or to be moved - anything not Done. This is what the dashboard's "Demos scheduled" tile counts.</summary>
    public bool Outstanding { get; init; }
}

/// <param name="NewSlotLocal">With Reschedule, optionally the new time as the tenant's own clock reads it, "yyyy-MM-ddTHH:mm" (no zone: the tenant's time zone is used,
/// so the browser's never matters). Must be in the future. Not allowed with any other status.</param>
public record SetBookingStatusRequest(string Status, string? NewSlotLocal = null);

public interface IBookingService
{
    /// <summary>The tenant's bookings, newest first.</summary>
    Task<PagedResult<BookingDto>> GetPagedAsync(BookingQuery query, CancellationToken cancellationToken = default);

    /// <summary>Marks a booking Pending, Done or Reschedule - with a new time when it is Reschedule and one is given. Anyone on the tenant's team can; a booking of
    /// another tenant is not found.</summary>
    Task<BookingDto> SetStatusAsync(Guid id, SetBookingStatusRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What the business's next step is called on the list and in the notice.</summary>
public static class BookingKinds
{
    public static string Of(ConversationGoal goal) => goal switch
    {
        ConversationGoal.Demo => "Demo",
        ConversationGoal.Appointment => "Appointment",
        ConversationGoal.SiteVisit => "Site visit",
        ConversationGoal.Consultation => "Consultation",
        ConversationGoal.Registration => "Registration",
        ConversationGoal.Purchase => "Purchase",
        _ => "Booking"
    };
}

public class BookingService : IBookingService
{
    private static readonly string[] SlotFormats = { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss" };

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider? _dateTime;

    public BookingService(IApplicationDbContext context, IDateTimeProvider? dateTime = null)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<PagedResult<BookingDto>> GetPagedAsync(BookingQuery query, CancellationToken cancellationToken = default)
    {
        var bookings = _context.NextStepBookings.AsNoTracking().AsQueryable();
        if (!query.IncludeTests)
            bookings = bookings.Where(b => !b.IsTest);
        if (query.Outstanding)
            bookings = bookings.Where(b => b.Status != BookingStatus.Done);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<BookingStatus>(query.Status, ignoreCase: true, out var wanted) || !Enum.IsDefined(wanted))
                throw Invalid(nameof(query.Status), "Choose Pending, Done or Reschedule.");
            bookings = bookings.Where(b => b.Status == wanted);
        }

        var total = await bookings.CountAsync(cancellationToken);
        var page = await (
                from b in bookings
                join c in _context.Customers.AsNoTracking() on b.CustomerId equals c.Id into customers
                from c in customers.DefaultIfEmpty()
                orderby b.CreatedAt descending
                select new { b.Id, b.CreatedAt, b.Kind, b.Summary, b.ConversationId, b.IsTest, b.Status, b.NewSlotUtc, FirstName = c.FirstName, LastName = c.LastName, Phone = c.PhoneNumberE164 })
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var items = page.Select(r =>
        {
            var name = string.Join(" ", new[] { r.FirstName, r.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new BookingDto(r.Id, r.CreatedAt, r.Kind, r.Summary, name.Length == 0 ? null : name, r.Phone, r.ConversationId, r.IsTest, r.Status.ToString(), r.NewSlotUtc);
        }).ToList();

        return new PagedResult<BookingDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<BookingDto> SetStatusAsync(Guid id, SetBookingStatusRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<BookingStatus>(request.Status, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            throw Invalid(nameof(request.Status), "Choose Pending, Done or Reschedule.");

        // The tenant filter applies to this read, so another tenant's booking is simply not found.
        var booking = await _context.NextStepBookings.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(NextStepBooking), id);

        var newSlot = await ResolveSlotAsync(booking, status, request.NewSlotLocal, cancellationToken);

        booking.Status = status;
        // A new time belongs to Reschedule only: any other status, or Reschedule with no time picked, leaves none behind.
        booking.NewSlotUtc = status == BookingStatus.Reschedule ? newSlot : null;
        await _context.SaveChangesAsync(cancellationToken);

        var customer = booking.CustomerId is { } customerId
            ? await _context.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.FirstName, c.LastName, c.PhoneNumberE164 }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var name = customer is null ? string.Empty : string.Join(" ", new[] { customer.FirstName, customer.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new BookingDto(
            booking.Id, booking.CreatedAt, booking.Kind, booking.Summary, name.Length == 0 ? null : name, customer?.PhoneNumberE164, booking.ConversationId, booking.IsTest,
            booking.Status.ToString(), booking.NewSlotUtc);
    }

    /// <summary>The new time as UTC. It arrives as the tenant's own wall clock, so it is read in the tenant's time zone - which is why "11:00" means 11:00 where the
    /// tenant is, whatever the browser's zone. Null when none was given.</summary>
    private async Task<DateTime?> ResolveSlotAsync(NextStepBooking booking, BookingStatus status, string? local, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(local))
            return null;
        if (status != BookingStatus.Reschedule)
            throw Invalid(nameof(SetBookingStatusRequest.NewSlotLocal), "A new time only goes with Reschedule.");
        if (!DateTime.TryParseExact(local.Trim(), SlotFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var wallClock))
            throw Invalid(nameof(SetBookingStatusRequest.NewSlotLocal), "Enter the new date and time.");

        var zoneId = await _context.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == booking.TenantId).Select(t => t.Timezone).FirstOrDefaultAsync(cancellationToken);
        var zone = Tenancy.TimeZoneCatalog.Resolve(zoneId);
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
            throw Invalid(nameof(SetBookingStatusRequest.NewSlotLocal), "That time does not exist on that day in your time zone (the clocks change). Pick another.");

        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        if (utc <= (_dateTime?.UtcNow ?? DateTime.UtcNow))
            throw Invalid(nameof(SetBookingStatusRequest.NewSlotLocal), "Pick a time in the future.");
        return utc;
    }

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}

/// <summary>Saves a booking the AI confirmed and tells the business about it. The live chat and Test chat both use it, so a test shows exactly what a real one does.</summary>
public interface IBookingRecorder
{
    /// <param name="conversationId">Null in Test chat, which has no conversation.</param>
    /// <param name="customerId">Null in Test chat.</param>
    /// <param name="summary">What the AI reported was agreed.</param>
    /// <param name="isTest">Made in Test chat: saved and announced, but marked so it is not counted as a customer's booking.</param>
    /// <returns>True when a new booking was saved; false when this very booking was already known. Never throws.</returns>
    Task<bool> RecordAsync(Guid tenantId, Guid? conversationId, Guid? customerId, string summary, bool isTest, CancellationToken cancellationToken = default);
}

public class BookingRecorder : IBookingRecorder
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantNotifier _notifier;
    private readonly ILogger<BookingRecorder> _logger;

    public BookingRecorder(IApplicationDbContext context, ITenantNotifier notifier, ILogger<BookingRecorder> logger)
    {
        _context = context;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<bool> RecordAsync(Guid tenantId, Guid? conversationId, Guid? customerId, string summary, bool isTest, CancellationToken cancellationToken = default)
    {
        try
        {
            summary = summary.Trim();
            if (summary.Length == 0)
                return false;
            if (summary.Length > 400)
                summary = summary[..400];

            // The same booking confirmed again in the same conversation is one booking. A test has no conversation, and each one is worth seeing.
            if (!isTest && conversationId is { } conversation
                && await _context.NextStepBookings.IgnoreQueryFilters().AnyAsync(b => b.TenantId == tenantId && b.ConversationId == conversation && b.Summary == summary, cancellationToken))
                return false;

            var goal = await _context.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.AiConversationGoal).FirstOrDefaultAsync(cancellationToken);
            var kind = BookingKinds.Of(goal);

            // A booking the tenant marked Reschedule is the same booking with a new time, not a second one: when the customer confirms a new time in that
            // conversation, it is updated in place and goes back to Pending, so the list never holds the old and the new side by side.
            var moved = !isTest && conversationId is { } thread
                ? await _context.NextStepBookings.IgnoreQueryFilters()
                    .Where(b => b.TenantId == tenantId && b.ConversationId == thread && b.Status == BookingStatus.Reschedule)
                    .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken)
                : null;
            if (moved is not null)
            {
                moved.Summary = summary;
                moved.Status = BookingStatus.Pending;
                moved.NewSlotUtc = null; // the customer has agreed a time: what the team proposed no longer applies
            }
            else
            {
                _context.NextStepBookings.Add(new NextStepBooking
                {
                    TenantId = tenantId, ConversationId = conversationId, CustomerId = customerId, Kind = kind, Summary = summary, IsTest = isTest
                });
            }
            await _context.SaveChangesAsync(cancellationToken);

            await TellTenantAsync(tenantId, conversationId, customerId, kind, summary, isTest, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The customer already has their confirmation; losing the tenant's copy must not undo it.
            _logger.LogWarning(ex, "Could not record a booking for tenant {TenantId}", tenantId);
            return false;
        }
    }

    private async Task TellTenantAsync(Guid tenantId, Guid? conversationId, Guid? customerId, string kind, string summary, bool isTest, CancellationToken cancellationToken)
    {
        string who;
        if (isTest)
        {
            who = "Test chat";
        }
        else
        {
            var customer = customerId is { } id
                ? await _context.Customers.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == id && c.TenantId == tenantId)
                    .Select(c => new { c.FirstName, c.LastName, c.PhoneNumberE164 }).FirstOrDefaultAsync(cancellationToken)
                : null;
            var name = customer is null ? string.Empty : string.Join(" ", new[] { customer.FirstName, customer.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            who = customer is null ? "A customer" : name.Length == 0 ? customer.PhoneNumberE164 : $"{name} ({customer.PhoneNumberE164})";
        }

        var episode = isTest
            ? $"test:{Guid.NewGuid():N}"
            : $"booked:{conversationId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(summary.ToLowerInvariant())), 0, 6)}";

        await _notifier.NotifyAsync(
            new TenantNotificationRequest(
                tenantId, TenantNotificationKind.NextStepBooked, null, episode,
                isTest ? $"Test: a customer booked a {kind.ToLowerInvariant()}" : $"A customer booked a {kind.ToLowerInvariant()}",
                isTest ? $"This is a test from Test chat. {summary}" : $"{who}: {summary}",
                AlsoWhatsApp: true, ToSupportPhone: true, PlainTextFallback: true),
            cancellationToken);
    }
}
