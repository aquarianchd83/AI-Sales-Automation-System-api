using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Bookings;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A booking is Pending until the tenant marks it Done, or Reschedule when the time has to change; a confirmed new time in the same conversation moves
/// the same booking instead of adding a second one.</summary>
public sealed class BookingStatusTests : IDisposable
{
    private sealed class AmbientTenant : ITenantContext
    {
        public AmbientTenant(Guid tenantId) => TenantId = tenantId;

        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
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

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Notices _notices = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly Guid _conversation = Guid.NewGuid();

    public BookingStatusTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Tenants.AddRange(new Tenant { Id = _tenant, Name = "Clinic", Slug = "clinic", AiConversationGoal = ConversationGoal.Demo }, new Tenant { Id = _otherTenant, Name = "Other", Slug = "other" });
        _db.Customers.Add(new Customer { Id = _customer, TenantId = _tenant, FirstName = "Asha", LastName = "Verma", PhoneNumberE164 = "+919000000001" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BookingService Service() => new(_db);

    private BookingRecorder Recorder() => new(_db, _notices, NullLogger<BookingRecorder>.Instance);

    private async Task<NextStepBooking> AddAsync(BookingStatus status = BookingStatus.Pending, bool test = false, Guid? tenant = null, string summary = "Demo Mon 2 Nov 4 PM IST")
    {
        var booking = new NextStepBooking
        {
            TenantId = tenant ?? _tenant, ConversationId = _conversation, CustomerId = _customer, Kind = "Demo", Summary = summary, Status = status, IsTest = test
        };
        _db.NextStepBookings.Add(booking);
        await _db.SaveChangesAsync();
        return booking;
    }

    [Fact]
    public async Task A_new_booking_is_pending_and_the_list_says_so()
    {
        await Recorder().RecordAsync(_tenant, _conversation, _customer, "Demo Mon 2 Nov 4 PM IST", isTest: false);

        var item = Assert.Single((await Service().GetPagedAsync(new BookingQuery { Page = 1, PageSize = 10 })).Items);

        Assert.Equal("Pending", item.Status);
        Assert.Equal("Asha Verma", item.CustomerName);
        Assert.Equal(_conversation, item.ConversationId);
    }

    [Theory]
    [InlineData("Done", BookingStatus.Done)]
    [InlineData("Reschedule", BookingStatus.Reschedule)]
    [InlineData("reschedule", BookingStatus.Reschedule)]
    [InlineData("Pending", BookingStatus.Pending)]
    public async Task The_status_can_be_changed_to_any_of_the_three_and_back(string requested, BookingStatus expected)
    {
        var booking = await AddAsync(BookingStatus.Done);

        var updated = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest(requested, null, "Customer asked"));

        Assert.Equal(expected.ToString(), updated.Status);
        Assert.Equal(expected, (await _db.NextStepBookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).Status);
        Assert.Equal("Asha Verma", updated.CustomerName);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("")]
    [InlineData("7")]
    public async Task Any_other_status_is_refused_and_nothing_changes(string requested)
    {
        var booking = await AddAsync();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest(requested, null, "Customer asked")));

        Assert.Equal(BookingStatus.Pending, (await _db.NextStepBookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).Status);
    }

    // ----- a new slot, picked when rescheduling ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rescheduling_with_a_new_time_keeps_it_as_utc_read_in_the_tenants_own_time_zone()
    {
        var booking = await AddAsync();

        // 11:00 on the tenant's clock (India by default, UTC+5:30) is 05:30 UTC, whatever zone the browser is in.
        var updated = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", "2099-11-04T11:00", "Customer asked"));

        Assert.Equal("Reschedule", updated.Status);
        Assert.Equal(new DateTime(2099, 11, 4, 5, 30, 0, DateTimeKind.Utc), updated.NewSlotUtc);
        Assert.Equal(new DateTime(2099, 11, 4, 5, 30, 0), (await _db.NextStepBookings.AsNoTracking().SingleAsync()).NewSlotUtc);
    }

    [Fact]
    public async Task A_tenant_in_another_zone_has_its_own_clock_used()
    {
        (await _db.Tenants.SingleAsync(t => t.Id == _tenant)).Timezone = "America/New_York";
        await _db.SaveChangesAsync();
        var booking = await AddAsync();

        var updated = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", "2099-11-04T11:00", "Customer asked"));

        // New York is UTC-5 in November (standard time).
        Assert.Equal(new DateTime(2099, 11, 4, 16, 0, 0), updated.NewSlotUtc);
    }

    [Fact]
    public async Task Rescheduling_without_a_time_is_allowed_and_leaves_no_slot()
    {
        var booking = await AddAsync();

        var updated = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", null, "Customer asked"));

        Assert.Equal("Reschedule", updated.Status);
        Assert.Null(updated.NewSlotUtc);
    }

    [Fact]
    public async Task Rescheduling_keeps_the_reason_trimmed()
    {
        var booking = await AddAsync();

        var updated = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", null, "  Customer is travelling  "));

        Assert.Equal("Customer is travelling", updated.RescheduleReason);
        Assert.Equal("Customer is travelling", (await _db.NextStepBookings.AsNoTracking().SingleAsync()).RescheduleReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rescheduling_without_a_reason_is_refused(string? reason)
    {
        var booking = await AddAsync();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", null, reason)));
    }

    [Fact]
    public async Task Moving_a_booking_on_from_reschedule_drops_the_reason()
    {
        var booking = await AddAsync();
        await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", null, "Customer asked"));

        var done = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Done"));

        Assert.Null(done.RescheduleReason);
    }

    [Fact]
    public async Task Moving_a_booking_on_from_reschedule_drops_the_slot_that_was_picked()
    {
        var booking = await AddAsync();
        await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", "2099-11-04T11:00", "Customer asked"));

        var pending = await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Pending"));

        Assert.Null(pending.NewSlotUtc);
        Assert.Null((await _db.NextStepBookings.AsNoTracking().SingleAsync()).NewSlotUtc);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Done")]
    public async Task A_new_time_with_any_status_but_reschedule_is_refused(string status)
    {
        var booking = await AddAsync();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest(status, "2099-11-04T11:00")));

        Assert.Equal(BookingStatus.Pending, (await _db.NextStepBookings.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("2020-01-01T10:00")] // in the past
    [InlineData("tomorrow at 11")]
    [InlineData("2099-13-45T11:00")]
    [InlineData("2099-11-04")]
    public async Task A_time_in_the_past_or_one_that_is_not_a_date_and_time_is_refused_and_nothing_changes(string slot)
    {
        var booking = await AddAsync();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", slot, "Customer asked")));

        var stored = await _db.NextStepBookings.AsNoTracking().SingleAsync();
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Null(stored.NewSlotUtc);
    }

    [Fact]
    public async Task The_list_carries_the_slot_that_was_picked()
    {
        var booking = await AddAsync();
        await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", "2099-11-04T11:00", "Customer asked"));

        var item = Assert.Single((await Service().GetPagedAsync(new BookingQuery { Page = 1, PageSize = 10 })).Items);

        Assert.Equal(new DateTime(2099, 11, 4, 5, 30, 0), item.NewSlotUtc);
    }

    [Fact]
    public async Task When_the_customer_confirms_a_time_of_their_own_the_slot_the_team_proposed_is_cleared()
    {
        var booking = await AddAsync();
        await Service().SetStatusAsync(booking.Id, new SetBookingStatusRequest("Reschedule", "2099-11-04T11:00", "Customer asked"));

        await Recorder().RecordAsync(_tenant, _conversation, _customer, "Demo Thu 5 Nov 3 PM IST", isTest: false);

        var only = await _db.NextStepBookings.AsNoTracking().SingleAsync();
        Assert.Equal(BookingStatus.Pending, only.Status);
        Assert.Null(only.NewSlotUtc);
    }

    [Fact]
    public async Task A_booking_that_does_not_exist_or_belongs_to_another_tenant_is_not_found()
    {
        var theirs = await AddAsync(tenant: _otherTenant);

        await Assert.ThrowsAsync<NotFoundException>(() => Service().SetStatusAsync(Guid.NewGuid(), new SetBookingStatusRequest("Done")));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().SetStatusAsync(theirs.Id, new SetBookingStatusRequest("Done")));

        Assert.Equal(BookingStatus.Pending, (await _db.NextStepBookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == theirs.Id)).Status);
    }

    [Fact]
    public async Task The_list_can_be_narrowed_to_one_status_and_a_bad_one_is_refused()
    {
        await AddAsync(BookingStatus.Pending, summary: "a");
        await AddAsync(BookingStatus.Done, summary: "b");
        await AddAsync(BookingStatus.Reschedule, summary: "c");

        var done = await Service().GetPagedAsync(new BookingQuery { Page = 1, PageSize = 10, Status = "Done" });
        var all = await Service().GetPagedAsync(new BookingQuery { Page = 1, PageSize = 10 });

        Assert.Equal("b", Assert.Single(done.Items).Summary);
        Assert.Equal(3, all.TotalCount);
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().GetPagedAsync(new BookingQuery { Page = 1, PageSize = 10, Status = "Soon" }));
    }

    [Fact]
    public async Task What_is_still_outstanding_is_everything_not_done_and_leaves_out_test_chat()
    {
        await AddAsync(BookingStatus.Pending, summary: "a");
        await AddAsync(BookingStatus.Reschedule, summary: "b");
        await AddAsync(BookingStatus.Done, summary: "c");
        await AddAsync(BookingStatus.Pending, test: true, summary: "d");

        var outstanding = await Service().GetPagedAsync(new BookingQuery { Page = 1, PageSize = 10, Outstanding = true });

        Assert.Equal(new[] { "a", "b" }.OrderBy(x => x), outstanding.Items.Select(i => i.Summary).OrderBy(x => x));
    }

    [Fact]
    public async Task A_new_time_confirmed_for_a_booking_marked_reschedule_moves_that_booking_and_makes_it_pending_again()
    {
        var booking = await AddAsync(BookingStatus.Reschedule, summary: "Demo Mon 2 Nov 4 PM IST");

        var created = await Recorder().RecordAsync(_tenant, _conversation, _customer, "Demo moved to Wed 4 Nov 11 AM IST", isTest: false);

        Assert.True(created);
        var only = await _db.NextStepBookings.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(booking.Id, only.Id);
        Assert.Equal("Demo moved to Wed 4 Nov 11 AM IST", only.Summary);
        Assert.Equal(BookingStatus.Pending, only.Status);
        // The tenant is told about the new time as it would be about any booking.
        Assert.Single(_notices.Sent);
    }

    [Fact]
    public async Task A_different_time_for_a_booking_that_is_not_marked_reschedule_is_a_second_booking()
    {
        await AddAsync(BookingStatus.Pending, summary: "Demo Mon 2 Nov 4 PM IST");

        await Recorder().RecordAsync(_tenant, _conversation, _customer, "A site visit on Fri 6 Nov", isTest: false);

        Assert.Equal(2, await _db.NextStepBookings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_test_chat_booking_never_moves_a_real_one()
    {
        await AddAsync(BookingStatus.Reschedule);

        await Recorder().RecordAsync(_tenant, null, null, "Demo Tue 3 Nov", isTest: true);

        var rows = await _db.NextStepBookings.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => !r.IsTest && r.Status == BookingStatus.Reschedule);
    }
}
