using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;

namespace WhatsAppSalesAutomation.Application.Bookings;

/// <summary>How one channel of a message to the customer went. <paramref name="Status"/> is Sent, Failed, or Skipped (the channel did not apply: no email address on
/// file, or no conversation to send into). <paramref name="Note"/> says why when it did not go.</summary>
public record BookingMessageResult(string Channel, string Status, string? Note = null);

/// <summary>A message from the tenant to the customer of a booking - a meeting link, joining details, anything - by email, WhatsApp or both.</summary>
public record BookingMessageRequest(string? Message, bool SendEmail = false, bool SendWhatsApp = false);

public static class BookingChannels
{
    public const string Email = "Email";
    public const string WhatsApp = "WhatsApp";
}

/// <summary>Sends what the tenant wrote to the customer of a booking. Each channel reports for itself: one failing never hides the other.</summary>
public interface IBookingMessenger
{
    Task<IReadOnlyList<BookingMessageResult>> SendAsync(NextStepBooking booking, string message, bool email, bool whatsApp, CancellationToken cancellationToken = default);
}

public class BookingMessenger : IBookingMessenger
{
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _email;
    private readonly IConversationService _conversations;
    private readonly ICurrentUserService _user;
    private readonly ILogger<BookingMessenger> _logger;

    public BookingMessenger(IApplicationDbContext context, IEmailSender email, IConversationService conversations, ICurrentUserService user, ILogger<BookingMessenger> logger)
    {
        _context = context;
        _email = email;
        _conversations = conversations;
        _user = user;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BookingMessageResult>> SendAsync(NextStepBooking booking, string message, bool email, bool whatsApp, CancellationToken cancellationToken = default)
    {
        var results = new List<BookingMessageResult>();
        if (email)
            results.Add(await SendEmailAsync(booking, message, cancellationToken));
        if (whatsApp)
            results.Add(await SendWhatsAppAsync(booking, message, cancellationToken));
        return results;
    }

    private async Task<BookingMessageResult> SendEmailAsync(NextStepBooking booking, string message, CancellationToken cancellationToken)
    {
        try
        {
            var to = booking.CustomerEmail;
            if (string.IsNullOrWhiteSpace(to) && booking.CustomerId is { } customerId)
                to = await _context.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Email).FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(to))
                return new BookingMessageResult(BookingChannels.Email, "Skipped", "no email address on file for this customer");

            var tenantName = await _context.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == booking.TenantId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? "us";
            var sent = await _email.SendAsync(to, $"{booking.Kind} with {tenantName}", $"{message.Trim()}\n\n- {tenantName}", cancellationToken);
            return new BookingMessageResult(BookingChannels.Email, sent.Skipped ? "Skipped" : sent.Success ? "Sent" : "Failed", sent.Success ? null : sent.Note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Email to the customer of booking {BookingId} failed", booking.Id);
            return new BookingMessageResult(BookingChannels.Email, "Failed", ex.Message);
        }
    }

    private async Task<BookingMessageResult> SendWhatsAppAsync(NextStepBooking booking, string message, CancellationToken cancellationToken)
    {
        if (booking.ConversationId is not { } conversationId)
            return new BookingMessageResult(BookingChannels.WhatsApp, "Skipped", "this booking has no WhatsApp conversation (it was made in Test chat)");
        if (_user.UserId is not { } userId)
            return new BookingMessageResult(BookingChannels.WhatsApp, "Failed", "no signed-in user to send as");

        try
        {
            await _conversations.SendMessageAsync(conversationId, new SendConversationMessageRequest(message.Trim(), null), userId, cancellationToken);
            return new BookingMessageResult(BookingChannels.WhatsApp, "Sent");
        }
        catch (Exception ex) when (ex is ConflictException or FluentValidation.ValidationException or NotFoundException)
        {
            // The usual one: the customer's 24-hour window is closed, so only an approved template can go - the conversation screen offers those.
            return new BookingMessageResult(BookingChannels.WhatsApp, "Failed", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp message to the customer of booking {BookingId} failed", booking.Id);
            return new BookingMessageResult(BookingChannels.WhatsApp, "Failed", ex.Message);
        }
    }
}
