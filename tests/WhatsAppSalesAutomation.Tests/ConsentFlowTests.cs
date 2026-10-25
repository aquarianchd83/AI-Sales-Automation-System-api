using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Compliance;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Webhooks;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Phases 5 and 6 end to end through the real consent and campaign services: the SMS inviting a customer to chat on WhatsApp, its delivery
/// tracking, the customer's own WhatsApp message that records consent, and the campaign's first message that follows - or the wait for
/// content when it is not ready.
/// </summary>
public sealed partial class ConsentFlowTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Clock _clock = new() { UtcNow = T0 };
    private readonly SqliteApplicationDbContext _db;
    private readonly FakeWhatsApp _whatsApp = new();
    private readonly Ambient _ambient;
    private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
    private readonly FakeSms _sms = new();
    private readonly CampaignSendService _campaignSend;
    private readonly ConsentInvitationService _consent;
    private readonly RecordingNotifier _notifier = new();
    private readonly ConsentPageGate _gate;
    private readonly ConsentPageService _page;
    private readonly ConsentPageOptions _pageOptions = new() { PublicBaseUrl = "https://app.example.com" };
    private readonly InboundWebhookProcessor _processor;
    private readonly FakeParser _parser = new();
    private readonly FakeOrchestrator _orchestrator = new();
    private readonly Tenant _tenant = new() { Name = "Acme Eye Care", Slug = "acme", WhatsAppNumber = "+91 98000 12345" };
    private readonly Guid _conversationId = Guid.NewGuid();
    private Customer _customer = null!;
    private Campaign _campaign = null!;
    private MessageTemplate _template = null!;
    private CampaignCustomer _member = null!;

    public ConsentFlowTests()
    {
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _ambient = new Ambient(_tenant.Id);
        _db = new SqliteApplicationDbContext(_dbOptions, _ambient, new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();

        var ledger = new QuotaLedgerService(_db, _clock);
        var gate = CreditTestDoubles.Gate(ledger);

        var conversations = DispatchProxy.Create<IConversationService, Stub>();
        ((Stub)(object)conversations).Handler = (m, _) => m.Name == nameof(IConversationService.GetOrCreateActiveConversationIdAsync)
            ? Task.FromResult(_conversationId)
            : throw new NotImplementedException(m.Name);

        var config = DispatchProxy.Create<ITenantConfigOverrideProvider, Stub>();
        ((Stub)(object)config).Handler = (m, _) => m.Name == nameof(ITenantConfigOverrideProvider.GetMessagingOptionsAsync)
            ? Task.FromResult(new MessagingOptions())
            : throw new NotImplementedException(m.Name);

        _campaignSend = new CampaignSendService(
            _db, _whatsApp, _clock, conversations, _ambient, gate, config, new LocalClock(_clock),
            NullLogger<CampaignSendService>.Instance);

        _gate = new ConsentPageGate(_db, _notifier, Options.Create(_pageOptions), NullLogger<ConsentPageGate>.Instance);
        var starter = new ConsentWhatsAppStarter(_db, _campaignSend, _clock, Options.Create(_pageOptions), NullLogger<ConsentWhatsAppStarter>.Instance);
        _consent = new ConsentInvitationService(
            _db, _sms, _clock, _ambient, config, _campaignSend, _gate, starter, Options.Create(_pageOptions),
            NullLogger<ConsentInvitationService>.Instance);
        _page = new ConsentPageService(_db, _ambient, _clock, starter, NullLogger<ConsentPageService>.Instance);

        var notifications = DispatchProxy.Create<INotificationService, Stub>();
        ((Stub)(object)notifications).Handler = (_, _) => Task.CompletedTask;

        _processor = new InboundWebhookProcessor(
            _db, _clock, _parser, conversations, _orchestrator, notifications, _ambient,
            NullLogger<InboundWebhookProcessor>.Instance, _consent);

        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
        ledger.AdjustAsync(_tenant.Id, QuotaType.WhatsAppMessages, 20, "test funding", Guid.NewGuid()).GetAwaiter().GetResult();

        Seed();
        PublishPage();
    }

    /// <summary>The tenant's WhatsApp Consent Page, valid and live as version 1.</summary>
    private void PublishPage(string name = "Acme Eye Care")
    {
        _db.TenantConsentPages.Add(new TenantConsentPage
        {
            TenantId = _tenant.Id, BusinessName = name, ProductDescription = "Eye tests and glasses for the whole family.",
            IsActive = true, PublishedVersion = 1, PublishedAtUtc = T0
        });
        _db.ConsentPageVersions.Add(new ConsentPageVersion
        {
            TenantId = _tenant.Id, Version = 1, BusinessName = name, ProductLabel = null,
            ProductDescription = "Eye tests and glasses for the whole family.",
            ConsentStatement = ConsentPageRules.ResolveStatement(null, name, null), PublishedAtUtc = T0
        });
        _db.SaveChanges();
    }

    /// <summary>An invitation of the earlier kind - an SMS with a wa.me link that the customer answers by messaging the business - as
    /// already sent before the consent page existed. Still answered by the customer's reply.</summary>
    private async Task SeedLegacyInvitationAsync()
    {
        _db.ConsentInvitations.Add(new ConsentInvitation
        {
            TenantId = _tenant.Id, CustomerId = _customer.Id, CampaignId = _campaign.Id, ExecutionId = Guid.NewGuid(),
            IdempotencyKey = $"{_tenant.Id:N}:{_customer.Id:N}:{_campaign.Id:N}", Status = ConsentInvitationStatus.Delivered,
            ToPhoneE164 = _customer.PhoneNumberE164, Body = "Hi, Acme Eye Care here. Chat with us on WhatsApp: https://wa.me/919800012345",
            WhatsAppLink = "https://wa.me/919800012345", SubmittedAtUtc = T0, DeliveredAtUtc = T0, AttemptCount = 1, CorrelationId = "legacy"
        });
        await _db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Seed(WhatsAppTemplateStatus templateStatus = WhatsAppTemplateStatus.Approved, CampaignStatus campaignStatus = CampaignStatus.Running)
    {
        _customer = new Customer
        {
            PhoneNumberE164 = "+919800000001", FirstName = "Sim", LastName = "Owner", Source = "Lead discovery",
            OptInStatus = OptInStatus.WaitingForConsent
        };
        _db.Customers.Add(_customer);
        _db.Conversations.Add(new Conversation { Id = _conversationId, CustomerId = _customer.Id });

        _template = new MessageTemplate
        {
            Name = "intro", WhatsAppTemplateName = "intro", Category = TemplateCategory.Marketing, BodyText = "Hello from Acme",
            WhatsAppTemplateStatus = templateStatus, IsActive = true
        };
        _db.MessageTemplates.Add(_template);

        _campaign = new Campaign { Name = "Eye clinic - 2026-10-15", Status = campaignStatus, CreatedBy = Guid.NewGuid(), StartedAt = T0 };
        _campaign.Steps.Add(new CampaignStep { StepNumber = 0, StepType = "Initial", MessageText = "Hi", MessageTemplateId = _template.Id, IsActive = true });
        _db.Campaigns.Add(_campaign);

        _member = new CampaignCustomer { CampaignId = _campaign.Id, CustomerId = _customer.Id };
        _db.CampaignCustomers.Add(_member);
        _db.SaveChanges();
    }

    private Task<ConsentInvitationBatchResult> InviteAsync() =>
        _consent.SendAsync(_campaign.Id, Guid.NewGuid(), new[] { _customer.Id });

    /// <summary>The customer's message arrives, and the clock is at that moment - so everything done in answer to it is later than it.</summary>
    private Task<ConsentReplyOutcome> ReplyAsync(string messageId = "wamid.HI1", string text = "Hi", int minutesLater = 30)
    {
        _clock.UtcNow = T0.AddMinutes(minutesLater);
        return _consent.HandleInboundMessageAsync(_customer, new InboundConsentMessage(messageId, text, _clock.UtcNow));
    }

    private async Task<List<string>> EventTypesAsync() =>
        (await _db.CustomerHistoryEvents.Where(e => e.CustomerId == _customer.Id).ToListAsync())
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => CustomerHistoryEventTypes.Rank(e.EventType)).ThenBy(e => e.CreatedAt)
            .Select(e => e.EventType).ToList();

    // ------------------------------------------------------------------------------------------------ Phase 5: the SMS

    [Fact]
    public async Task The_sms_carries_a_secure_link_to_the_tenants_consent_page_and_is_not_consent()
    {
        var result = await InviteAsync();

        Assert.Equal(1, result.Sent);
        var sms = Assert.Single(_sms.Requests);
        Assert.Equal("+919800000001", sms.ToPhoneE164);

        // "Hi Sim! Would you like to learn about Acme Eye Care? View the details and choose whether to receive WhatsApp follow-ups: <link>"
        Assert.StartsWith("Hi Sim! Would you like to learn about Acme Eye Care? View the details and choose whether to receive WhatsApp follow-ups: https://app.example.com/consent/", sms.Body);
        var token = sms.Body[(sms.Body.LastIndexOf('/') + 1)..];
        Assert.Equal(43, token.Length);
        Assert.DoesNotContain("919800000001", sms.Body);   // no personal data in the link
        Assert.DoesNotContain(_customer.Id.ToString(), sms.Body);
        Assert.DoesNotContain(_tenant.Id.ToString(), sms.Body);
        Assert.DoesNotContain("agreed", sms.Body, StringComparison.OrdinalIgnoreCase);   // does not imply consent was given

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(ConsentInvitationStatus.Delivered, invitation.Status);   // the simulator reports delivery in the same call
        Assert.Equal(ConsentDecisionStatus.LinkSent, invitation.DecisionStatus);
        Assert.Equal(ConsentLinkToken.Hash(token), invitation.TokenHash);
        Assert.DoesNotContain(token, invitation.Body);   // only the hash is kept
        Assert.DoesNotContain(token, invitation.WhatsAppLink);
        Assert.Equal(T0.AddDays(14), invitation.TokenExpiresAtUtc);

        // An SMS, even a delivered one, is not consent.
        var customer = await _db.Customers.SingleAsync();
        Assert.Equal(OptInStatus.WaitingForConsent, customer.OptInStatus);
        Assert.Null(customer.OptInTimestamp);
        Assert.Equal(CampaignCustomerStatus.Pending, (await _db.CampaignCustomers.SingleAsync()).Status);
        Assert.Equal(new[] { "SmsQueued", "SmsSubmitted", "SmsDelivered" }, await EventTypesAsync());
    }

    [Fact]
    public async Task Inviting_again_never_sends_a_second_sms()
    {
        await InviteAsync();
        var second = await InviteAsync();

        Assert.Equal(1, _sms.Requests.Count);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, await _db.ConsentInvitations.CountAsync());
    }

    [Fact]
    public async Task The_tenants_own_whatsapp_number_is_not_needed_to_send_the_link()
    {
        var tenant = await _db.Tenants.SingleAsync();
        tenant.WhatsAppNumber = null;
        await _db.SaveChangesAsync();

        var result = await InviteAsync();

        Assert.Equal(1, result.Sent);
        Assert.Single(_sms.Requests);
    }

    [Theory]
    [InlineData(OptInStatus.OptedOut)]
    [InlineData(OptInStatus.OptedIn)]
    public async Task A_customer_who_opted_out_or_already_opted_in_is_not_invited(OptInStatus status)
    {
        _customer.OptInStatus = status;
        await _db.SaveChangesAsync();

        var result = await InviteAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Empty(_sms.Requests);
    }

    [Fact]
    public async Task A_transient_rejection_is_retried_after_its_backoff_then_goes_through_without_a_duplicate()
    {
        _sms.Results.Enqueue(new ConsentSmsSendResult(false, null, "provider down", Transient: true));

        var first = await InviteAsync();
        var failed = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(1, first.Failed);
        Assert.Equal(ConsentInvitationStatus.Failed, failed.Status);
        Assert.NotNull(failed.NextAttemptAt);

        Assert.Equal(0, await _consent.RetryDueAsync());   // not yet due

        _clock.UtcNow = failed.NextAttemptAt!.Value.AddMinutes(1);
        Assert.Equal(1, await _consent.RetryDueAsync());

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(ConsentInvitationStatus.Delivered, invitation.Status);
        Assert.Equal(2, invitation.AttemptCount);
        Assert.Equal(2, _sms.Requests.Count);
        Assert.All(_sms.Requests, r => Assert.Equal(invitation.IdempotencyKey, r.IdempotencyKey));   // the provider can drop a repeat

        Assert.Equal(0, await _consent.RetryDueAsync());   // nothing left to retry
    }

    [Fact]
    public async Task A_permanent_rejection_is_not_retried()
    {
        _sms.Results.Enqueue(new ConsentSmsSendResult(false, null, "unreachable", Transient: false));

        await InviteAsync();

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(ConsentInvitationStatus.Failed, invitation.Status);
        Assert.Null(invitation.NextAttemptAt);
        _clock.UtcNow = T0.AddDays(1);
        Assert.Equal(0, await _consent.RetryDueAsync());
    }

    [Fact]
    public async Task Retries_stop_when_the_attempts_are_used_up()
    {
        for (var i = 0; i < ConsentSmsMessage.MaxAttempts; i++)
            _sms.Results.Enqueue(new ConsentSmsSendResult(false, null, "provider down", Transient: true));

        await InviteAsync();
        for (var i = 1; i < ConsentSmsMessage.MaxAttempts; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddDays(1);
            await _consent.RetryDueAsync();
        }

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(ConsentSmsMessage.MaxAttempts, invitation.AttemptCount);
        Assert.Null(invitation.NextAttemptAt);
        _clock.UtcNow = _clock.UtcNow.AddDays(5);
        Assert.Equal(0, await _consent.RetryDueAsync());
        Assert.Equal(ConsentSmsMessage.MaxAttempts, _sms.Requests.Count);
    }

    [Fact]
    public async Task Accepted_is_not_delivered_until_the_provider_sends_a_receipt_and_a_repeated_receipt_changes_nothing()
    {
        _sms.Results.Enqueue(new ConsentSmsSendResult(true, "prov-1", null));   // a real provider: no immediate receipt

        await InviteAsync();
        Assert.Equal(ConsentInvitationStatus.Submitted, (await _db.ConsentInvitations.SingleAsync()).Status);

        Assert.True(await _consent.ApplyReceiptAsync("prov-1", ConsentInvitationStatus.Delivered, T0.AddSeconds(30), null));
        Assert.False(await _consent.ApplyReceiptAsync("prov-1", ConsentInvitationStatus.Delivered, T0.AddSeconds(31), null));   // duplicate
        Assert.False(await _consent.ApplyReceiptAsync("prov-1", ConsentInvitationStatus.Failed, T0.AddSeconds(60), "late"));   // would move it backwards
        Assert.False(await _consent.ApplyReceiptAsync("unknown", ConsentInvitationStatus.Delivered, T0, null));

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(ConsentInvitationStatus.Delivered, invitation.Status);
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == "SmsDelivered"));
    }

    [Fact]
    public async Task A_failed_receipt_marks_the_invitation_failed_and_queues_a_retry()
    {
        _sms.Results.Enqueue(new ConsentSmsSendResult(true, "prov-2", null));
        await InviteAsync();

        Assert.True(await _consent.ApplyReceiptAsync("prov-2", ConsentInvitationStatus.Failed, T0.AddSeconds(20), "Handset unreachable"));

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal(ConsentInvitationStatus.Failed, invitation.Status);
        Assert.Equal("Handset unreachable", invitation.FailureReason);
        Assert.NotNull(invitation.NextAttemptAt);
    }

    // ------------------------------------------------------------------------------------------------ Phase 6: an earlier invitation is answered by the customer's WhatsApp message

    [Fact]
    public async Task The_customers_own_whatsapp_message_records_consent_with_evidence_and_the_first_message_goes_out_once()
    {
        await SeedLegacyInvitationAsync();

        var outcome = await ReplyAsync();

        Assert.True(outcome.InvitationAnswered);
        Assert.True(outcome.ConsentRecorded);
        Assert.Equal(FirstMessageStatus.Sent, outcome.FirstMessage!.Status);

        var customer = await _db.Customers.SingleAsync();
        Assert.Equal(OptInStatus.OptedIn, customer.OptInStatus);
        Assert.Equal(T0.AddMinutes(30), customer.OptInTimestamp);
        Assert.Equal("WhatsAppReplyToSmsInvitation", customer.OptInSource);
        Assert.Contains("wamid.HI1", customer.OptInProof);
        Assert.Equal(ConsentScope.Marketing, customer.OptInScope);
        Assert.True(TemplateSendGate.Evaluate(customer, TemplateCategory.Marketing).Allowed);   // the evidence is complete enough to send on

        var invitation = await _db.ConsentInvitations.SingleAsync();
        Assert.Equal("wamid.HI1", invitation.ConsentMessageId);
        Assert.NotNull(invitation.ConsentRecordedAtUtc);

        Assert.Equal(1, _whatsApp.Calls);
        var member = await _db.CampaignCustomers.SingleAsync();
        Assert.Equal(0, member.CurrentStepNumber);

        var events = await EventTypesAsync();
        Assert.Equal(
            new[] { "ConversationStarted", "ConsentRecorded", "FirstMessageSent" }, events);
        var consent = await _db.CustomerHistoryEvents.SingleAsync(e => e.EventType == "ConsentRecorded");
        Assert.Contains("wamid.HI1", consent.Reason);
        Assert.Contains("window", (await _db.CustomerHistoryEvents.SingleAsync(e => e.EventType == "ConversationStarted")).Reason);
    }

    [Fact]
    public async Task A_second_hi_or_the_same_message_again_does_not_consent_or_send_twice()
    {
        await SeedLegacyInvitationAsync();
        await ReplyAsync("wamid.HI1");

        var again = await ReplyAsync("wamid.HI1");
        var later = await ReplyAsync("wamid.HI2", "Hi again", minutesLater: 90);

        Assert.False(again.InvitationAnswered);
        Assert.False(later.InvitationAnswered);
        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == "ConsentRecorded"));
        Assert.Equal(1, await _db.Messages.CountAsync(m => m.CampaignCustomerId == _member.Id));
    }

    [Fact]
    public async Task A_message_from_a_customer_with_no_invitation_changes_nothing()
    {
        var outcome = await ReplyAsync();

        Assert.False(outcome.InvitationAnswered);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    [Fact]
    public async Task An_invitation_that_was_never_sent_cannot_be_answered()
    {
        _db.ConsentInvitations.Add(new ConsentInvitation
        {
            TenantId = _tenant.Id, CustomerId = _customer.Id, CampaignId = _campaign.Id, IdempotencyKey = "k", ToPhoneE164 = _customer.PhoneNumberE164,
            Body = "b", WhatsAppLink = "https://wa.me/1", Status = ConsentInvitationStatus.Pending
        });
        await _db.SaveChangesAsync();

        Assert.False((await ReplyAsync()).InvitationAnswered);
    }

    [Fact]
    public async Task A_customer_who_opted_out_is_not_opted_back_in_by_a_reply()
    {
        await SeedLegacyInvitationAsync();
        _customer.OptInStatus = OptInStatus.OptedOut;
        await _db.SaveChangesAsync();

        var outcome = await ReplyAsync();

        Assert.False(outcome.InvitationAnswered);
        Assert.Equal(OptInStatus.OptedOut, (await _db.Customers.SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    // ------------------------------------------------------------------------------------------------ Waiting for content

    [Fact]
    public async Task With_no_approved_content_the_customer_is_opted_in_but_waits_and_nothing_is_sent()
    {
        _template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;
        await _db.SaveChangesAsync();
        await SeedLegacyInvitationAsync();

        var outcome = await ReplyAsync();

        Assert.True(outcome.ConsentRecorded);
        Assert.Equal(FirstMessageStatus.WaitingForContent, outcome.FirstMessage!.Status);
        Assert.Equal(0, _whatsApp.Calls);

        // Consent and the campaign's processing status are separate: opted in, and waiting.
        Assert.Equal(OptInStatus.OptedIn, (await _db.Customers.SingleAsync()).OptInStatus);
        var member = await _db.CampaignCustomers.SingleAsync();
        Assert.Equal(CampaignCustomerStatus.WaitingForContent, member.Status);
        Assert.Contains("not approved", member.StoppedReason);
        Assert.Contains("WaitingForContent", await EventTypesAsync());
    }

    [Fact]
    public async Task When_the_content_becomes_available_the_first_message_goes_out_exactly_once()
    {
        _template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;
        await _db.SaveChangesAsync();
        await SeedLegacyInvitationAsync();
        await ReplyAsync();
        Assert.Equal(0, _whatsApp.Calls);

        _template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        await _db.SaveChangesAsync();

        var resumed = await _campaignSend.ProcessInitialSendsAsync();
        var again = await _campaignSend.ProcessInitialSendsAsync();

        Assert.Equal(1, resumed.Sent);
        Assert.Equal(0, again.Sent);
        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(1, await _db.Messages.CountAsync(m => m.CampaignCustomerId == _member.Id));

        var member = await _db.CampaignCustomers.SingleAsync();
        Assert.NotEqual(CampaignCustomerStatus.WaitingForContent, member.Status);
        Assert.Null(member.StoppedReason);

        var events = await EventTypesAsync();
        Assert.Contains("ContentAvailable", events);
        Assert.Equal(1, events.Count(e => e == "FirstMessageSent"));
        Assert.True(events.IndexOf("WaitingForContent") < events.IndexOf("ContentAvailable"));
    }

    [Fact]
    public async Task A_campaign_that_has_not_started_holds_the_first_message_until_it_is_running()
    {
        _campaign.Status = CampaignStatus.Scheduled;
        await _db.SaveChangesAsync();
        await SeedLegacyInvitationAsync();

        var outcome = await ReplyAsync();

        Assert.Equal(FirstMessageStatus.Held, outcome.FirstMessage!.Status);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.Equal(OptInStatus.OptedIn, (await _db.Customers.SingleAsync()).OptInStatus);

        _campaign.Status = CampaignStatus.Running;
        await _db.SaveChangesAsync();
        Assert.Equal(1, (await _campaignSend.ProcessInitialSendsAsync()).Sent);
        Assert.Equal(1, _whatsApp.Calls);
    }

    [Fact]
    public async Task A_member_past_their_first_message_is_reported_already_processed_and_nothing_is_sent()
    {
        _member.Status = CampaignCustomerStatus.AwaitingResponse;
        _member.CurrentStepNumber = 0;
        await _db.SaveChangesAsync();

        var outcome = await _campaignSend.SendFirstMessageAsync(_member.Id);

        Assert.Equal(FirstMessageStatus.AlreadyProcessed, outcome.Status);
        Assert.Equal(0, _whatsApp.Calls);
    }

    // ------------------------------------------------------------------------------------------------ The campaign journey shows it

    [Fact]
    public async Task The_campaign_journey_tells_the_whole_story_from_waiting_for_consent_to_the_first_message()
    {
        await SeedLegacyInvitationAsync();
        await ReplyAsync();

        var member = await _db.CampaignCustomers.AsNoTracking().SingleAsync();
        var customer = await _db.Customers.AsNoTracking().SingleAsync();
        var invitations = await _db.ConsentInvitations.AsNoTracking().ToListAsync();
        var outbound = await _db.Messages.AsNoTracking().Where(m => m.CampaignCustomerId == member.Id).ToListAsync();

        var journey = CampaignJourneyBuilder.Build(
            await _db.Campaigns.Include(c => c.Steps).SingleAsync(), member, customer, outbound, Array.Empty<Message>(), invitations);

        var titles = journey.Steps.Select(s => s.Title).ToList();
        Assert.Contains("Waiting for consent", titles);
        Assert.Contains("Consent SMS delivered", titles);
        Assert.Contains("Customer started a WhatsApp conversation", titles);
        Assert.Contains("Got consent", titles);
        Assert.Contains("Pending", titles);
        Assert.Equal("Waiting for consent", titles[0]);

        var sms = journey.Steps.Single(s => s.Title == "Consent SMS delivered");
        Assert.Contains(sms.Details, d => d.Label == "WhatsApp link" && d.Value == "https://wa.me/919800012345");
    }

    // ------------------------------------------------------------------------------------------------ through the webhook

    /// <summary>Delivers one inbound WhatsApp message from the customer through the real webhook processor, as Meta would.</summary>
    private async Task DeliverAsync(string wamid, string text, int minutesLater = 30)
    {
        _clock.UtcNow = T0.AddMinutes(minutesLater);
        _parser.Next = new WhatsAppWebhookParseResult(
            new[] { new InboundWhatsAppMessage(wamid, "919800000001", "Sim Owner", _clock.UtcNow, "text", text) },
            Array.Empty<WhatsAppStatusUpdate>());

        var eventId = await _processor.RecordAsync(_tenant.Id, "messages", "{}");
        await _processor.ProcessAsync(_tenant.Id, eventId);
    }

    [Fact]
    public async Task A_hi_through_the_webhook_records_consent_sends_the_first_message_and_the_ai_does_not_also_reply()
    {
        await SeedLegacyInvitationAsync();

        await DeliverAsync("wamid.HI1", "Hi");

        var customer = await _db.Customers.SingleAsync();
        Assert.Equal(OptInStatus.OptedIn, customer.OptInStatus);
        Assert.Contains("wamid.HI1", customer.OptInProof);
        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(0, _orchestrator.Calls);   // the campaign message is the reply to this "Hi"

        // The inbound message itself is on the record, with the customer service window starting from it.
        var inbound = await _db.Messages.SingleAsync(m => m.Direction == MessageDirection.Inbound);
        Assert.Equal("wamid.HI1", inbound.WhatsAppMessageId);
        var conversation = await _db.Conversations.SingleAsync();
        Assert.Equal(T0.AddMinutes(30), conversation.LastInboundMessageAt);
        Assert.Contains("ConsentRecorded", await EventTypesAsync());
    }

    [Fact]
    public async Task The_same_webhook_delivered_twice_consents_and_sends_once()
    {
        await SeedLegacyInvitationAsync();

        await DeliverAsync("wamid.HI1", "Hi");
        await DeliverAsync("wamid.HI1", "Hi");   // Meta redelivers

        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == "ConsentRecorded"));
        Assert.Equal(1, await _db.Messages.CountAsync(m => m.Direction == MessageDirection.Inbound));
    }

    [Fact]
    public async Task A_stop_from_an_invited_customer_opts_them_out_and_is_not_consent()
    {
        await SeedLegacyInvitationAsync();

        await DeliverAsync("wamid.STOP1", "STOP");

        var customer = await _db.Customers.SingleAsync();
        Assert.Equal(OptInStatus.OptedOut, customer.OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.DoesNotContain("ConsentRecorded", await EventTypesAsync());
        Assert.Null((await _db.ConsentInvitations.SingleAsync()).ConsentRecordedAtUtc);
    }

    [Fact]
    public async Task A_later_message_after_the_conversation_started_is_an_ordinary_message_for_the_ai()
    {
        await SeedLegacyInvitationAsync();
        await DeliverAsync("wamid.HI1", "Hi");
        Assert.Equal(0, _orchestrator.Calls);

        await DeliverAsync("wamid.Q2", "What are your timings?", minutesLater: 45);

        Assert.Equal(1, _orchestrator.Calls);
        Assert.Equal(1, _whatsApp.Calls);   // no second first message
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == "ConsentRecorded"));
    }

    // ------------------------------------------------------------------------------------------------ fakes

    private sealed class FakeParser : IWhatsAppWebhookParser
    {
        public WhatsAppWebhookParseResult Next { get; set; } = new(Array.Empty<InboundWhatsAppMessage>(), Array.Empty<WhatsAppStatusUpdate>());

        public WhatsAppWebhookParseResult Parse(string rawPayload) => Next;
    }

    private sealed class FakeOrchestrator : IConversationOrchestrator
    {
        public int Calls { get; private set; }

        public Task HandleInboundMessageAsync(Guid conversationId, Guid customerId, Guid inboundMessageId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }


    private sealed class FakeSms : IConsentSmsSender
    {
        public List<ConsentSmsRequest> Requests { get; } = new();

        /// <summary>Scripted answers, used first; once empty, every send is accepted and reported delivered.</summary>
        public Queue<ConsentSmsSendResult> Results { get; } = new();

        public Task<ConsentSmsSendResult> SendAsync(ConsentSmsRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new ConsentSmsSendResult(true, $"sms-{Requests.Count}", null, ImmediateReceipt: new ConsentSmsReceipt(ConsentInvitationStatus.Delivered)));
        }
    }

    private sealed class FakeWhatsApp : IWhatsAppService
    {
        public int Calls { get; private set; }

        /// <summary>Messages the provider actually accepted.</summary>
        public int Delivered { get; private set; }

        /// <summary>Runs as the provider is called - lets a test look at what was already saved by then.</summary>
        public Action? BeforeSend { get; set; }

        public bool FailNext { get; set; }
        public bool ThrowNext { get; set; }

        public Task<WhatsAppSendResult> SendTemplateMessageAsync(
            string toPhoneNumberE164, string templateName, string languageCode, IReadOnlyList<string> parameterValues,
            string? mediaUrl = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeSend?.Invoke();
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new HttpRequestException("WhatsApp provider unreachable");
            }

            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(new WhatsAppSendResult(false, null, "provider unavailable"));
            }

            Delivered++;
            return Task.FromResult(new WhatsAppSendResult(true, $"wamid.out{Calls}", null));
        }

        public Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class Stub : DispatchProxy
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

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
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
}
