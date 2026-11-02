using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Bookings;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The plain confirmation email a customer gets when a demo is booked, and what the tenant can send afterwards - a meeting link, joining details - by email,
/// WhatsApp or both, including while rescheduling.</summary>
public sealed class BookingMessagingTests : IDisposable
{
    private sealed class AmbientTenant : ITenantContext
    {
        public AmbientTenant(Guid tenantId) => TenantId = tenantId;

        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class User : ICurrentUserService
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class Notices : ITenantNotifier
    {
        public List<TenantNotificationRequest> Sent { get; } = new();

        public Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class Emails : IEmailSender
    {
        public DeliveryResult Result { get; set; } = new(true);
        public List<(string To, string Subject, string Body)> Sent { get; } = new();

        public Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
        {
            Sent.Add((toEmail, subject, body));
            return Task.FromResult(Result);
        }
    }

    /// <summary>Only SendMessageAsync is used; the rest of the conversation service is not this test's business.</summary>
    public class Chat : DispatchProxy
    {
        public static (IConversationService Service, Chat Handler) Create()
        {
            var proxy = Create<IConversationService, Chat>();
            return (proxy, (Chat)(object)proxy);
        }

        public Exception? Throw { get; set; }
        public List<(Guid Id, string? Text)> Sent { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IConversationService.SendMessageAsync))
                throw new NotSupportedException(targetMethod.Name);
            if (Throw is not null)
                throw Throw;
            Sent.Add(((Guid)args![0]!, ((SendConversationMessageRequest)args[1]!).Text));
            return Task.FromResult<ConversationMessageDto>(null!);
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Notices _notices = new();
    private readonly Emails _emails = new();
    private readonly User _user = new();
    private readonly IConversationService _chat;
    private readonly Chat _chatLog;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly Guid _conversation = Guid.NewGuid();

    public BookingMessagingTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new User()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Clinic", Slug = "clinic", AiConversationGoal = ConversationGoal.Demo });
        _db.Customers.Add(new Customer { Id = _customer, TenantId = _tenant, FirstName = "Asha", LastName = "Verma", PhoneNumberE164 = "+919000000001" });
        _db.SaveChanges();
        (_chat, _chatLog) = Chat.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BookingRecorder Recorder() => new(_db, _notices, NullLogger<BookingRecorder>.Instance, _emails);

    private BookingService Service() => new(_db, null, new BookingMessenger(_db, _emails, _chat, _user, NullLogger<BookingMessenger>.Instance));

    private Task RecordAsync(BookingConfirmation? confirmation, bool isTest = false) =>
        Recorder().RecordAsync(_tenant, isTest ? null : _conversation, isTest ? null : _customer, "Demo on 3 Nov at 9:30 IST", isTest, confirmation);

    private async Task<NextStepBooking> AddAsync(string? email = null, Guid? conversation = null)
    {
        var booking = new NextStepBooking
        {
            TenantId = _tenant, ConversationId = conversation ?? _conversation, CustomerId = _customer, Kind = "Demo", Summary = "Demo on 3 Nov at 9:30 IST", CustomerEmail = email
        };
        _db.NextStepBookings.Add(booking);
        await _db.SaveChangesAsync();
        return booking;
    }

    // ----- the confirmation email -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_booking_with_an_email_sends_a_plain_confirmation_and_records_it()
    {
        await RecordAsync(new BookingConfirmation("asha@example.com"));

        var mail = Assert.Single(_emails.Sent);
        Assert.Equal("asha@example.com", mail.To);
        Assert.Equal("Your demo with Clinic is booked", mail.Subject);
        Assert.Contains("Hi Asha", mail.Body);
        Assert.Contains("This confirms your demo", mail.Body);
        Assert.Contains("Demo on 3 Nov at 9:30 IST", mail.Body);
        Assert.DoesNotContain("BEGIN:VCALENDAR", mail.Body);

        var booking = await _db.NextStepBookings.AsNoTracking().SingleAsync();
        Assert.Equal("asha@example.com", booking.CustomerEmail);
        Assert.Equal("Sent", booking.ConfirmationStatus);
        Assert.Contains("A confirmation email was sent to asha@example.com.", Assert.Single(_notices.Sent).Body);
    }

    [Fact]
    public async Task A_booking_without_an_email_sends_nothing()
    {
        await RecordAsync(null);

        Assert.Empty(_emails.Sent);
        Assert.Null((await _db.NextStepBookings.AsNoTracking().SingleAsync()).ConfirmationStatus);
        Assert.DoesNotContain("email", Assert.Single(_notices.Sent).Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "no SMTP host configured", "Skipped")]
    [InlineData(false, "connection refused", "Failed")]
    public async Task A_confirmation_that_could_not_go_is_recorded_and_the_tenant_is_told(bool skipped, string note, string status)
    {
        _emails.Result = new DeliveryResult(false, note, skipped);

        await RecordAsync(new BookingConfirmation("asha@example.com"));

        var booking = await _db.NextStepBookings.AsNoTracking().SingleAsync();
        Assert.Equal(status, booking.ConfirmationStatus);
        Assert.Equal(note, booking.ConfirmationNote);
        Assert.Contains("was NOT sent", Assert.Single(_notices.Sent).Body);
    }

    // ----- messaging the customer -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tenant_can_send_a_meeting_link_by_email_and_whatsapp_together()
    {
        var booking = await AddAsync("asha@example.com");

        var results = await Service().MessageCustomerAsync(booking.Id, new BookingMessageRequest("  Join here: https://zoom.example/abc  ", SendEmail: true, SendWhatsApp: true));

        Assert.Equal(new[] { ("Email", "Sent"), ("WhatsApp", "Sent") }, results.Select(r => (r.Channel, r.Status)));
        var mail = Assert.Single(_emails.Sent);
        Assert.Equal("asha@example.com", mail.To);
        Assert.StartsWith("Join here: https://zoom.example/abc", mail.Body);
        Assert.Contains("- Clinic", mail.Body);
        var chat = Assert.Single(_chatLog.Sent);
        Assert.Equal(_conversation, chat.Id);
        Assert.Equal("Join here: https://zoom.example/abc", chat.Text);
    }

    [Fact]
    public async Task Each_channel_reports_for_itself_so_one_failing_does_not_hide_the_other()
    {
        var booking = await AddAsync("asha@example.com");
        _chatLog.Throw = new ConflictException("The 24-hour customer service window is closed - send an approved template instead.");

        var results = await Service().MessageCustomerAsync(booking.Id, new BookingMessageRequest("See you soon", SendEmail: true, SendWhatsApp: true));

        Assert.Equal("Sent", results.Single(r => r.Channel == "Email").Status);
        var whatsApp = results.Single(r => r.Channel == "WhatsApp");
        Assert.Equal("Failed", whatsApp.Status);
        Assert.Contains("window is closed", whatsApp.Note);
    }

    [Fact]
    public async Task A_channel_that_does_not_apply_is_skipped_with_the_reason()
    {
        var booking = await AddAsync(email: null, conversation: null);
        booking.ConversationId = null;
        booking.CustomerId = null;
        await _db.SaveChangesAsync();

        var results = await Service().MessageCustomerAsync(booking.Id, new BookingMessageRequest("Hello", SendEmail: true, SendWhatsApp: true));

        Assert.All(results, r => Assert.Equal("Skipped", r.Status));
        Assert.Contains("no email address", results.Single(r => r.Channel == "Email").Note);
        Assert.Contains("no WhatsApp conversation", results.Single(r => r.Channel == "WhatsApp").Note);
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task The_customers_own_email_is_used_when_the_booking_has_none()
    {
        (await _db.Customers.SingleAsync(c => c.Id == _customer)).Email = "on-file@example.com";
        var booking = await AddAsync(email: null);

        await Service().MessageCustomerAsync(booking.Id, new BookingMessageRequest("Hello", SendEmail: true));

        Assert.Equal("on-file@example.com", Assert.Single(_emails.Sent).To);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("   ", true)]
    [InlineData("Hello", false)] // no channel chosen
    public async Task A_message_needs_text_and_a_channel(string? message, bool email)
    {
        var booking = await AddAsync("asha@example.com");

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().MessageCustomerAsync(booking.Id, new BookingMessageRequest(message, email, false)));
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task Another_tenants_booking_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service().MessageCustomerAsync(Guid.NewGuid(), new BookingMessageRequest("Hello", SendEmail: true)));
    }

    // ----- while rescheduling -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rescheduling_can_send_the_message_and_the_answer_says_how_it_went()
    {
        var booking = await AddAsync("asha@example.com");

        var updated = await Service().SetStatusAsync(
            booking.Id, new SetBookingStatusRequest("Reschedule", "2099-11-04T11:00", "Customer asked", "New time 11:00, link: https://zoom.example/abc", SendEmail: true, SendWhatsApp: true));

        Assert.Equal("Reschedule", updated.Status);
        Assert.Equal(new[] { ("Email", "Sent"), ("WhatsApp", "Sent") }, updated.MessageResults!.Select(r => (r.Channel, r.Status)));
        Assert.Single(_emails.Sent);
        Assert.Single(_chatLog.Sent);
    }

    [Fact]
    public async Task A_message_that_fails_does_not_undo_the_reschedule()
    {
        var booking = await AddAsync("asha@example.com");
        _chatLog.Throw = new InvalidOperationException("boom");
        _emails.Result = new DeliveryResult(false, "no SMTP host configured", Skipped: true);

        var updated = await Service().SetStatusAsync(
            booking.Id, new SetBookingStatusRequest("Reschedule", null, "Customer asked", "Moved", SendEmail: true, SendWhatsApp: true));

        Assert.Equal("Reschedule", updated.Status);
        Assert.Equal(BookingStatus.Reschedule, (await _db.NextStepBookings.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(new[] { "Skipped", "Failed" }, updated.MessageResults!.Select(r => r.Status));
    }

    [Fact]
    public async Task Rescheduling_without_a_message_sends_nothing()
    {
        var booking = await AddAsync("asha@example.com");

        var updated = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", null, "Customer asked"));

        Assert.Null(updated.MessageResults);
        Assert.Empty(_emails.Sent);
        Assert.Empty(_chatLog.Sent);
    }

    [Fact]
    public async Task A_message_is_only_allowed_with_reschedule_and_needs_a_channel()
    {
        var booking = await AddAsync("asha@example.com");

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Done", null, null, "Hello", SendEmail: true)));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", null, "Customer asked", "Hello")));
        Assert.Equal(BookingStatus.Pending, (await _db.NextStepBookings.AsNoTracking().SingleAsync()).Status);
    }
}
