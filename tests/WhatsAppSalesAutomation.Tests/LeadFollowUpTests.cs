using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Leads.FollowUps;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"Follow up later": a lead who could not go ahead is parked for 1/2/3 months and reminded once, without
/// ever being messaged when that would irritate - opted out, already back in touch, just messaged, or at night.</summary>
public sealed class LeadFollowUpTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Clock _clock = new() { UtcNow = new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc) }; // 11:30 IST
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly FakeWhatsApp _whatsApp = new();
    private readonly LeadFollowUpService _service;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Customer _customer = new() { PhoneNumberE164 = "+919000000000", FirstName = "Asha", OptInStatus = OptInStatus.OptedIn };
    private readonly Lead _lead = new() { Stage = LeadStage.Qualified };
    private readonly MessageTemplate _template = new()
    {
        Name = "Check in", WhatsAppTemplateName = "check_in", BodyText = "Hi {{FirstName}}, still thinking it over?",
        Category = TemplateCategory.Marketing, WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved
    };

    public LeadFollowUpTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        var gate = new QuotaGate(_ledger, new FixedOptions<WhatsAppPricingOptions>(new()), new FixedOptions<TrialQuotaOptions>(new()));

        var conversations = DispatchProxy.Create<IConversationService, Stub>();
        ((Stub)(object)conversations).Handler = (m, _) => m.Name == nameof(IConversationService.GetOrCreateActiveConversationIdAsync)
            ? Task.FromResult(_conversation.Id)
            : throw new NotImplementedException(m.Name);

        var config = DispatchProxy.Create<ITenantConfigOverrideProvider, Stub>();
        ((Stub)(object)config).Handler = (m, _) => m.Name == nameof(ITenantConfigOverrideProvider.GetMessagingOptionsAsync)
            ? Task.FromResult(new MessagingOptions())
            : throw new NotImplementedException(m.Name);

        _service = new LeadFollowUpService(
            _db, _clock, _whatsApp, conversations, new AmbientTenant(_tenant.Id), gate, config, new LocalClock(_clock),
            new ScheduleLeadFollowUpRequestValidator(), NullLogger<LeadFollowUpService>.Instance);

        _db.Tenants.Add(_tenant);
        _db.Customers.Add(_customer);
        _lead.CustomerId = _customer.Id;
        _db.Leads.Add(_lead);
        _db.MessageTemplates.Add(_template);
        _conversation.CustomerId = _customer.Id;
        _db.Conversations.Add(_conversation);
        _db.SaveChanges();
    }

    private readonly Conversation _conversation = new();

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task Fund(decimal units) => _ledger.AdjustAsync(_tenant.Id, QuotaType.WhatsAppMessages, units, "test funding", Guid.NewGuid());

    private async Task<decimal> BalanceAsync() =>
        (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == QuotaType.WhatsAppMessages).Balance;

    /// <summary>The test db does not stamp CreatedAt the way the real interceptor does; "what happened since it was
    /// scheduled" depends on it, so set it as production would.</summary>
    private async Task<LeadFollowUpDto> ScheduleAsync(int? months = 2, DateTime? dueAt = null)
    {
        var dto = await _service.ScheduleAsync(_lead.Id, new ScheduleLeadFollowUpRequest(months, dueAt, _template.Id, "Budget freezes until Q1"), _agent);
        var row = await _db.LeadFollowUps.SingleAsync(f => f.Id == dto.Id);
        row.CreatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return dto;
    }

    private Task<LeadFollowUp> RowAsync(Guid id) => _db.LeadFollowUps.SingleAsync(f => f.Id == id);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Scheduling_for_n_months_sets_the_due_date_n_months_ahead_and_records_it_on_the_lead(int months)
    {
        var dto = await ScheduleAsync(months);

        Assert.Equal(_clock.UtcNow.AddMonths(months), dto.DueAt);
        Assert.Equal("Scheduled", dto.Status);
        Assert.Equal(1, dto.FollowUpNumber);
        Assert.Equal("Budget freezes until Q1", dto.Reason);
        Assert.Equal("Asha", dto.CustomerName);
        Assert.Contains(await _db.LeadActivities.ToListAsync(), a => a.ActivityType == LeadActivityType.FollowUpScheduled && a.CreatedBy == _agent);
    }

    [Fact]
    public async Task Scheduling_again_replaces_the_pending_follow_up_so_a_lead_never_has_two()
    {
        var first = await ScheduleAsync(1);
        var second = await ScheduleAsync(3);

        Assert.Equal(LeadFollowUpStatus.Cancelled, (await RowAsync(first.Id)).Status);
        Assert.Equal(LeadFollowUpStatus.Scheduled, (await RowAsync(second.Id)).Status);
        Assert.Equal(1, await _db.LeadFollowUps.CountAsync(f => f.Status == LeadFollowUpStatus.Scheduled));
    }

    [Fact]
    public async Task A_custom_date_must_be_a_week_or_more_ahead()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ScheduleAsync(null, _clock.UtcNow.AddDays(3)));

        var ok = await ScheduleAsync(null, _clock.UtcNow.AddDays(10));
        Assert.Null(ok.IntervalMonths);
    }

    [Fact]
    public async Task Months_and_a_date_together_or_neither_is_rejected()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _service.ScheduleAsync(_lead.Id, new ScheduleLeadFollowUpRequest(2, _clock.UtcNow.AddDays(30), _template.Id, null), _agent));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _service.ScheduleAsync(_lead.Id, new ScheduleLeadFollowUpRequest(null, null, _template.Id, null), _agent));
    }

    [Fact]
    public async Task A_closed_lead_or_an_opted_out_customer_cannot_be_scheduled()
    {
        _lead.Stage = LeadStage.Lost;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => ScheduleAsync());

        _lead.Stage = LeadStage.Qualified;
        _customer.OptInStatus = OptInStatus.OptedOut;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => ScheduleAsync());
    }

    [Fact]
    public async Task A_template_that_is_not_approved_is_refused()
    {
        _template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ScheduleAsync());
    }

    [Fact]
    public async Task A_lead_cannot_be_scheduled_again_after_three_follow_ups_were_sent()
    {
        for (var i = 1; i <= LeadFollowUpPolicy.MaxSentPerLead; i++)
            _db.LeadFollowUps.Add(new LeadFollowUp { LeadId = _lead.Id, CustomerId = _customer.Id, Status = LeadFollowUpStatus.Sent, MessageTemplateId = _template.Id, FollowUpNumber = i });
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => ScheduleAsync());
        Assert.Contains("irritate", ex.Message);
    }

    [Fact]
    public async Task Nothing_is_sent_before_the_due_date()
    {
        await Fund(5);
        await ScheduleAsync(1);
        _clock.UtcNow = _clock.UtcNow.AddDays(20);

        var result = await _service.ProcessDueAsync();

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, _whatsApp.Calls);
    }

    [Fact]
    public async Task A_due_follow_up_goes_out_once_as_the_template_and_is_charged()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);

        var result = await _service.ProcessDueAsync();
        var again = await _service.ProcessDueAsync();

        Assert.Equal(1, result.Sent);
        Assert.Equal(0, again.Considered); // already Sent, so the next tick has nothing to do
        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(4m, await BalanceAsync());

        var row = await RowAsync(dto.Id);
        Assert.Equal(LeadFollowUpStatus.Sent, row.Status);
        Assert.Equal(_clock.UtcNow, row.SentAt);

        var message = await _db.Messages.SingleAsync();
        Assert.Equal(row.MessageId, message.Id);
        Assert.Equal(MessageStatus.Sent, message.Status);
        Assert.Equal("Hi Asha, still thinking it over?", message.Text);
        Assert.Equal(_conversation.Id, message.ConversationId);
        Assert.Contains(await _db.LeadActivities.ToListAsync(), a => a.ActivityType == LeadActivityType.FollowUpSent);
    }

    [Fact]
    public async Task Nothing_is_sent_at_night_in_the_tenants_timezone_and_it_goes_out_the_next_morning()
    {
        await Fund(5);
        await ScheduleAsync(1);
        _clock.UtcNow = new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc); // 23:30 IST, past the due date

        Assert.Equal(0, (await _service.ProcessDueAsync()).Considered);
        Assert.Equal(0, _whatsApp.Calls);

        _clock.UtcNow = new DateTime(2026, 10, 11, 4, 30, 0, DateTimeKind.Utc); // 10:00 IST
        Assert.Equal(1, (await _service.ProcessDueAsync()).Sent);
    }

    [Fact]
    public async Task A_customer_who_wrote_in_after_it_was_scheduled_is_not_nudged()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);
        _conversation.LastInboundMessageAt = _clock.UtcNow.AddDays(5);
        await _db.SaveChangesAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);

        var result = await _service.ProcessDueAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, _whatsApp.Calls);
        var row = await RowAsync(dto.Id);
        Assert.Equal(LeadFollowUpStatus.Skipped, row.Status);
        Assert.Contains("got in touch", row.OutcomeNote);
    }

    [Fact]
    public async Task A_message_before_it_was_scheduled_does_not_count_as_writing_in()
    {
        await Fund(5);
        _conversation.LastInboundMessageAt = _clock.UtcNow.AddDays(-2); // the conversation that ended with "not now"
        await _db.SaveChangesAsync();
        await ScheduleAsync(1);
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);

        Assert.Equal(1, (await _service.ProcessDueAsync()).Sent);
    }

    [Fact]
    public async Task A_customer_messaged_in_the_last_week_waits_instead_of_getting_two_messages_back_to_back()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);
        var campaignSentAt = _clock.UtcNow.AddDays(-2);
        _db.Messages.Add(new Message
        {
            CustomerId = _customer.Id, Direction = MessageDirection.Outbound, Status = MessageStatus.Sent, SentAt = campaignSentAt, IdempotencyKey = "campaign:step1"
        });
        await _db.SaveChangesAsync();

        var result = await _service.ProcessDueAsync();

        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, _whatsApp.Calls);
        var row = await RowAsync(dto.Id);
        Assert.Equal(LeadFollowUpStatus.Scheduled, row.Status);
        Assert.Equal(campaignSentAt.AddDays(LeadFollowUpPolicy.QuietDaysAfterAnyMessage), row.DueAt);
    }

    [Fact]
    public async Task A_customer_who_opts_out_after_being_scheduled_is_never_messaged()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);
        _customer.OptInStatus = OptInStatus.OptedOut;
        await _db.SaveChangesAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);

        var result = await _service.ProcessDueAsync();

        Assert.Equal(1, result.Cancelled);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.Equal(LeadFollowUpStatus.Cancelled, (await RowAsync(dto.Id)).Status);
    }

    [Fact]
    public async Task A_lead_that_was_won_in_the_meantime_is_not_followed_up()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);
        _lead.Stage = LeadStage.Won;
        await _db.SaveChangesAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);

        await _service.ProcessDueAsync();

        Assert.Equal(0, _whatsApp.Calls);
        Assert.Equal(LeadFollowUpStatus.Cancelled, (await RowAsync(dto.Id)).Status);
    }

    [Fact]
    public async Task With_no_quota_it_stays_scheduled_and_sends_once_the_tenant_tops_up()
    {
        var dto = await ScheduleAsync(1);
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);

        Assert.Equal(1, (await _service.ProcessDueAsync()).Skipped);
        Assert.Equal(LeadFollowUpStatus.Scheduled, (await RowAsync(dto.Id)).Status);

        await Fund(5);
        Assert.Equal(1, (await _service.ProcessDueAsync()).Sent);
    }

    [Fact]
    public async Task A_rejected_send_is_marked_failed_costs_nothing_and_can_be_retried_by_a_person()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);
        _whatsApp.Succeed = false;

        var result = await _service.ProcessDueAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(5m, await BalanceAsync());
        var failed = await RowAsync(dto.Id);
        Assert.Equal(LeadFollowUpStatus.Failed, failed.Status);
        Assert.Equal("rejected", failed.OutcomeNote);
        Assert.Equal(0, (await _service.ProcessDueAsync()).Considered); // not retried on its own

        _whatsApp.Succeed = true;
        var retried = await _service.SendNowAsync(dto.Id);

        Assert.Equal("Sent", retried.Status);
        Assert.Equal(4m, await BalanceAsync());
        Assert.Equal(2, await _db.Messages.CountAsync()); // the failed attempt and the retry are separate messages
    }

    [Fact]
    public async Task Send_now_goes_out_early_and_outside_the_daytime_window_but_never_to_someone_opted_out()
    {
        await Fund(5);
        var dto = await ScheduleAsync(3);
        _clock.UtcNow = new DateTime(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc); // 01:30 IST

        var sent = await _service.SendNowAsync(dto.Id);
        Assert.Equal("Sent", sent.Status);

        var second = await ScheduleAsync(3);
        _customer.OptInStatus = OptInStatus.OptedOut;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _service.SendNowAsync(second.Id));
        Assert.Equal(LeadFollowUpStatus.Cancelled, (await RowAsync(second.Id)).Status);
    }

    [Fact]
    public async Task A_cancelled_follow_up_is_not_sent_and_cannot_be_cancelled_twice()
    {
        await Fund(5);
        var dto = await ScheduleAsync(1);

        var cancelled = await _service.CancelAsync(dto.Id, _agent);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Contains(await _db.LeadActivities.ToListAsync(), a => a.ActivityType == LeadActivityType.FollowUpCancelled);

        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(1);
        Assert.Equal(0, (await _service.ProcessDueAsync()).Considered);
        await Assert.ThrowsAsync<ConflictException>(() => _service.CancelAsync(dto.Id, _agent));
    }

    [Fact]
    public async Task The_list_shows_scheduled_follow_ups_soonest_first_and_can_be_narrowed_to_those_due_soon()
    {
        var other = new Customer { PhoneNumberE164 = "+919111111111", FirstName = "Ravi", OptInStatus = OptInStatus.OptedIn };
        var otherLead = new Lead { Stage = LeadStage.Qualifying, CustomerId = other.Id };
        _db.Customers.Add(other);
        _db.Leads.Add(otherLead);
        await _db.SaveChangesAsync();

        await ScheduleAsync(3);
        await _service.ScheduleAsync(otherLead.Id, new ScheduleLeadFollowUpRequest(1, null, _template.Id, null), _agent);

        var all = await _service.GetPagedAsync(new Application.Common.Models.PagedRequest());
        Assert.Equal(new[] { "Ravi", "Asha" }, all.Items.Select(i => i.CustomerName));

        var soon = await _service.GetPagedAsync(new Application.Common.Models.PagedRequest(), dueWithinDays: 45);
        Assert.Equal(new[] { "Ravi" }, soon.Items.Select(i => i.CustomerName));

        var summary = await _service.GetSummaryAsync();
        Assert.Equal(2, summary.Scheduled);
        Assert.Equal(0, summary.DueNow);
        Assert.Equal(1, summary.DueWithin30Days);
    }

    private sealed class FakeWhatsApp : IWhatsAppService
    {
        public bool Succeed { get; set; } = true;
        public int Calls { get; private set; }

        public Task<WhatsAppSendResult> SendTemplateMessageAsync(
            string toPhoneNumberE164, string templateName, string languageCode, IReadOnlyList<string> parameterValues,
            string? mediaUrl = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Succeed ? new WhatsAppSendResult(true, $"wamid.{Calls}", null) : new WhatsAppSendResult(false, null, "rejected"));
        }

        public Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (m, _) => throw new NotImplementedException(m.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class LocalClock : ITenantTimeZoneProvider
    {
        private readonly Clock _clock;
        public LocalClock(Clock clock) => _clock = clock;
        public Task<DateTime> GetLocalNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(_clock.IstNow);
    }

    private sealed class AmbientTenant : ITenantContext
    {
        private readonly Guid _tenantId;
        public AmbientTenant(Guid tenantId) => _tenantId = tenantId;
        public Guid? TenantId => _tenantId;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
