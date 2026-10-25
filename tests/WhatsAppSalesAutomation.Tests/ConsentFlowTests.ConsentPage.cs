using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Application.Compliance;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The consent page workflow: the SMS carries a link, opening it records nothing, and only the customer's explicit Yes (with the box ticked)
/// or No is saved - after which, and only after a Yes, the existing WhatsApp pipeline starts the conversation once.
/// </summary>
public sealed partial class ConsentFlowTests
{
    private static readonly ConsentClientInfo Browser = new("203.0.113.9", "Mozilla/5.0 (test)");

    /// <summary>Sends the invitation and returns the secret from the SMS the customer received.</summary>
    private async Task<string> InviteAndGetTokenAsync()
    {
        await InviteAsync();
        return TokenOf(_sms.Requests.Last());
    }

    private static string TokenOf(ConsentSmsRequest sms) => sms.Body[(sms.Body.LastIndexOf('/') + 1)..];

    private static ConsentDecisionRequest Agree(bool ticked = true, int? version = 1) => new("agree", ticked, version);

    private static ConsentDecisionRequest Decline() => new("decline", false, 1);

    private Task<ConsentDecisionResultDto> Decide(string token, ConsentDecisionRequest request) => _page.DecideAsync(token, request, Browser);

    // ------------------------------------------------------------------------------------------------ the link and the page

    [Fact]
    public async Task Opening_the_link_shows_the_tenants_published_page_and_records_nothing()
    {
        var token = await InviteAndGetTokenAsync();
        var eventsBefore = await EventTypesAsync();

        // Once for the person, again for the SMS scanner and the link-preview bot.
        var first = await _page.GetAsync(token);
        var again = await _page.GetAsync(token);

        Assert.Equal(PublicConsentStates.Ready, first.State);
        Assert.Equal(PublicConsentStates.Ready, again.State);
        Assert.Equal("Acme Eye Care", first.Page!.BusinessName);
        Assert.Equal("Eye tests and glasses for the whole family.", first.Page.ProductDescription);
        Assert.Equal(1, first.Page.Version);
        Assert.Equal(
            "I agree to receive WhatsApp messages from Acme Eye Care about its products and services, including relevant follow-ups. I understand that I can opt out at any time.",
            first.Page.ConsentStatement);

        var invitation = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(ConsentDecisionStatus.LinkSent, invitation.DecisionStatus);
        Assert.Null(invitation.ConsentRecordedAtUtc);
        Assert.Null(invitation.DecisionAtUtc);
        Assert.Equal(WhatsAppInitiationStatus.NotStarted, invitation.WhatsAppInitiation);

        var customer = await _db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(OptInStatus.WaitingForConsent, customer.OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.Equal(eventsBefore, await EventTypesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("not a token with spaces or symbols !!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // right shape, but not ours
    public async Task Invalid_and_unknown_links_are_rejected_without_saying_why(string token)
    {
        await InviteAsync();

        var view = await _page.GetAsync(token);
        var decision = await Decide(token, Agree());

        Assert.Equal(PublicConsentStates.Invalid, view.State);
        Assert.Null(view.Page);
        Assert.Equal(ConsentDecisionOutcomes.Invalid, decision.Outcome);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    [Fact]
    public async Task An_expired_link_shows_expired_and_cannot_be_used_to_agree()
    {
        var token = await InviteAndGetTokenAsync();
        _clock.UtcNow = T0.AddDays(15);

        Assert.Equal(PublicConsentStates.Expired, (await _page.GetAsync(token)).State);
        var decision = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.Expired, decision.Outcome);
        Assert.Equal(ConsentDecisionStatus.Expired, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).DecisionStatus);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    [Fact]
    public async Task Each_link_shows_its_own_tenants_page_and_only_changes_its_own_tenants_customer()
    {
        var tokenA = await InviteAndGetTokenAsync();

        // A second tenant with its own page, customer and link.
        var other = new Tenant { Name = "Beta Dental", Slug = "beta" };
        _db.Tenants.Add(other);
        const string tokenB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var customerB = new Customer { TenantId = other.Id, PhoneNumberE164 = "+919800000002", FirstName = "Bea", OptInStatus = OptInStatus.WaitingForConsent };
        _db.Customers.Add(customerB);
        _db.TenantConsentPages.Add(new TenantConsentPage { TenantId = other.Id, BusinessName = "Beta Dental", ProductDescription = "Dental care for everyone.", PublishedVersion = 1 });
        _db.ConsentPageVersions.Add(new ConsentPageVersion
        {
            TenantId = other.Id, Version = 1, BusinessName = "Beta Dental", ProductDescription = "Dental care for everyone.",
            ConsentStatement = "I agree to receive WhatsApp messages from Beta Dental.", PublishedAtUtc = T0
        });
        _db.ConsentInvitations.Add(new ConsentInvitation
        {
            TenantId = other.Id, CustomerId = customerB.Id, IdempotencyKey = "beta", ToPhoneE164 = customerB.PhoneNumberE164, Body = "x", WhatsAppLink = "x",
            Status = ConsentInvitationStatus.Delivered, DecisionStatus = ConsentDecisionStatus.LinkSent, SubmittedAtUtc = T0,
            TokenHash = ConsentLinkToken.Hash(tokenB), TokenExpiresAtUtc = T0.AddDays(14)
        });
        await _db.SaveChangesAsync();

        var viewB = await _page.GetAsync(tokenB);
        _ambient.SetTenant(_tenant.Id);
        var viewA = await _page.GetAsync(tokenA);

        Assert.Equal("Beta Dental", viewB.Page!.BusinessName);
        Assert.Equal("Acme Eye Care", viewA.Page!.BusinessName);
        Assert.DoesNotContain("Beta", viewA.Page.ConsentStatement);

        // Agreeing through A's link changes A's customer and never B's.
        _ambient.SetTenant(_tenant.Id);
        Assert.Equal(ConsentDecisionOutcomes.Recorded, (await Decide(tokenA, Agree())).Outcome);
        _ambient.SetTenant(other.Id);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerB.Id)).OptInStatus);
    }

    // ------------------------------------------------------------------------------------------------ Yes

    [Fact]
    public async Task Yes_without_the_checkbox_is_refused_on_the_server_and_records_nothing()
    {
        var token = await InviteAndGetTokenAsync();

        var result = await Decide(token, Agree(ticked: false));

        Assert.Equal(ConsentDecisionOutcomes.BadRequest, result.Outcome);
        Assert.Equal(ConsentDecisionStatus.LinkSent, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).DecisionStatus);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    [Fact]
    public async Task Yes_with_the_checkbox_records_consent_with_its_evidence_and_only_then_starts_the_conversation_once()
    {
        var token = await InviteAndGetTokenAsync();
        OptInStatus? statusWhenWhatsAppWasCalled = null;
        ConsentDecisionStatus? decisionWhenWhatsAppWasCalled = null;
        _whatsApp.BeforeSend = () =>
        {
            statusWhenWhatsAppWasCalled = _db.Customers.AsNoTracking().Single().OptInStatus;
            decisionWhenWhatsAppWasCalled = _db.ConsentInvitations.AsNoTracking().Single().DecisionStatus;
        };
        _clock.UtcNow = T0.AddMinutes(10);

        var result = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.Recorded, result.Outcome);
        Assert.Equal("Granted", result.Decision);
        Assert.Contains("Acme Eye Care", result.Message);

        // The consent was already committed when the provider was called.
        Assert.Equal(OptInStatus.OptedIn, statusWhenWhatsAppWasCalled);
        Assert.Equal(ConsentDecisionStatus.Granted, decisionWhenWhatsAppWasCalled);

        var invitation = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(ConsentDecisionStatus.Granted, invitation.DecisionStatus);
        Assert.Equal(T0.AddMinutes(10), invitation.DecisionAtUtc);
        Assert.Equal("ConsentPage", invitation.DecisionSource);
        Assert.Equal(1, invitation.DecisionPageVersion);
        Assert.Equal(ConsentPageRules.ResolveStatement(null, "Acme Eye Care", null), invitation.DecisionConsentText);
        Assert.Equal(ConsentLinkToken.HashClient("203.0.113.9"), invitation.DecisionIpHash);
        Assert.DoesNotContain("203.0.113.9", invitation.DecisionIpHash);
        Assert.Equal("Mozilla/5.0 (test)", invitation.DecisionUserAgent);
        Assert.Equal(WhatsAppInitiationStatus.Started, invitation.WhatsAppInitiation);
        Assert.NotNull(invitation.WhatsAppInitiatedAtUtc);

        var customer = await _db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(OptInStatus.OptedIn, customer.OptInStatus);
        Assert.Equal("ConsentPage", customer.OptInSource);
        Assert.Equal(ConsentScope.Marketing, customer.OptInScope);
        Assert.Contains(invitation.Id.ToString(), customer.OptInProof);
        Assert.True(TemplateSendGate.Evaluate(customer, TemplateCategory.Marketing).Allowed);

        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(0, (await _db.CampaignCustomers.AsNoTracking().SingleAsync()).CurrentStepNumber);
        Assert.Equal(1, _sms.Requests.Count);   // no second SMS asking for confirmation
    }

    [Fact]
    public async Task Submitting_yes_again_never_starts_a_second_conversation_or_a_second_consent()
    {
        var token = await InviteAndGetTokenAsync();

        var outcomes = new[] { await Decide(token, Agree()), await Decide(token, Agree()), await Decide(token, Agree()) }.Select(r => r.Outcome).ToArray();

        Assert.Equal(new[] { ConsentDecisionOutcomes.Recorded, ConsentDecisionOutcomes.AlreadyRecorded, ConsentDecisionOutcomes.AlreadyRecorded }, outcomes);
        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == CustomerHistoryEventTypes.ConsentRecorded));
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == CustomerHistoryEventTypes.FirstMessageSent));
        Assert.Equal(1, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).WhatsAppAttemptCount);
    }

    [Fact]
    public async Task Duplicate_background_work_for_the_same_consent_does_not_send_the_first_message_twice()
    {
        var token = await InviteAndGetTokenAsync();
        await Decide(token, Agree());
        var invitation = await _db.ConsentInvitations.SingleAsync();
        var starter = new ConsentWhatsAppStarter(_db, _campaignSend, _clock, Microsoft.Extensions.Options.Options.Create(_pageOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsentWhatsAppStarter>.Instance);

        // The retry sweep and a repeated job both reach for the same consent.
        Assert.False(await starter.StartAsync(invitation));
        Assert.False(await starter.StartAsync(invitation));
        Assert.Equal(0, await _consent.RetryDueAsync());

        Assert.Equal(1, _whatsApp.Calls);
    }

    [Fact]
    public async Task A_yes_that_loses_a_race_to_another_yes_reports_it_and_the_conversation_still_starts_once()
    {
        var token = await InviteAndGetTokenAsync();
        RaceWith(ConsentDecisionStatus.Granted);

        var result = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.AlreadyRecorded, result.Outcome);
        Assert.Equal(ConsentDecisionStatus.Granted, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).DecisionStatus);
        Assert.Equal(1, _whatsApp.Calls);
    }

    [Fact]
    public async Task A_yes_that_loses_a_race_to_a_no_is_a_conflict_and_never_starts_a_conversation()
    {
        var token = await InviteAndGetTokenAsync();
        RaceWith(ConsentDecisionStatus.NotInterested);

        var result = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.Conflict, result.Outcome);
        var invitation = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(ConsentDecisionStatus.NotInterested, invitation.DecisionStatus);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    /// <summary>On the first save, another request has already decided - the database refuses this one's write, as the row-version guard does in production.</summary>
    private void RaceWith(ConsentDecisionStatus winner)
    {
        var fired = false;
        _db.BeforeSave = ctx =>
        {
            if (fired || !ctx.ChangeTracker.Entries<ConsentInvitation>().Any(e => e.Entity.DecisionStatus is ConsentDecisionStatus.Granted or ConsentDecisionStatus.NotInterested))
                return;

            fired = true;
            using var other = new SqliteApplicationDbContext(_dbOptions, new Ambient(_tenant.Id), new NoUser());
            var row = other.ConsentInvitations.Single();
            row.DecisionStatus = winner;
            row.DecisionAtUtc = _clock.UtcNow;
            if (winner == ConsentDecisionStatus.Granted)
            {
                row.ConsentRecordedAtUtc = _clock.UtcNow;
                var customer = other.Customers.Single();
                customer.OptInStatus = OptInStatus.OptedIn;
                customer.OptInTimestamp = _clock.UtcNow;
                customer.OptInSource = "ConsentPage";
                customer.OptInProof = "recorded by the request that won the race";
                customer.OptInScope = ConsentScope.Marketing;
            }

            other.SaveChanges();
            throw new DbUpdateConcurrencyException("another request decided first");
        };
    }

    [Fact]
    public async Task A_provider_failure_keeps_the_consent_and_the_campaigns_own_retry_sends_the_first_message_once()
    {
        var token = await InviteAndGetTokenAsync();
        _whatsApp.FailNext = true;

        var result = await Decide(token, Agree());

        // The customer is told their choice was saved: the failure is not theirs and does not undo it.
        Assert.Equal(ConsentDecisionOutcomes.Recorded, result.Outcome);
        var failed = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(ConsentDecisionStatus.Granted, failed.DecisionStatus);
        Assert.Equal(WhatsAppInitiationStatus.Failed, failed.WhatsAppInitiation);
        Assert.NotNull(failed.WhatsAppInitiationError);
        Assert.Equal(OptInStatus.OptedIn, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Delivered);

        // The message the provider refused belongs to the campaign pipeline, which retries it on its own backoff. The consent sweep
        // does not send a second copy beside it.
        Assert.Equal(0, await _consent.RetryDueAsync());
        Assert.Equal(1, _whatsApp.Calls);

        _clock.UtcNow = T0.AddHours(2);
        var retried = await _campaignSend.RetryFailedSendsAsync();
        Assert.Equal(1, retried.Sent);
        Assert.Equal(1, _whatsApp.Delivered);

        // The consent record catches up with what the pipeline did, and nothing more is sent.
        Assert.Equal(0, await _consent.RetryDueAsync());
        var recovered = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(WhatsAppInitiationStatus.Started, recovered.WhatsAppInitiation);
        Assert.Null(recovered.WhatsAppInitiationError);
        Assert.Equal(ConsentDecisionStatus.Granted, recovered.DecisionStatus);
        Assert.Equal(1, _whatsApp.Delivered);
        Assert.Equal(1, await _db.Messages.CountAsync());
    }

    [Fact]
    public async Task A_provider_that_throws_is_recorded_as_retryable_not_raised_to_the_customer_and_never_doubles_the_message()
    {
        var token = await InviteAndGetTokenAsync();
        _whatsApp.ThrowNext = true;

        var result = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.Recorded, result.Outcome);
        var invitation = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(ConsentDecisionStatus.Granted, invitation.DecisionStatus);
        Assert.Equal(WhatsAppInitiationStatus.Failed, invitation.WhatsAppInitiation);
        Assert.Equal(OptInStatus.OptedIn, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);

        // The message row is already reserved; sweeping again or submitting again sends nothing new.
        await _consent.RetryDueAsync();
        await Decide(token, Agree());
        Assert.Equal(1, _whatsApp.Calls);
        Assert.Equal(1, await _db.Messages.CountAsync());
    }

    [Fact]
    public async Task Yes_to_a_page_that_has_changed_since_it_was_loaded_is_refused_until_the_customer_has_read_the_new_one()
    {
        var token = await InviteAndGetTokenAsync();
        var settings = new ConsentPageSettingsService(_db, _ambient, _clock);
        var current = await settings.GetAsync();
        await settings.SaveAsync(new SaveConsentPageSettingsRequest(
            current.BusinessName, current.ProductDescription, null, "I agree to receive WhatsApp promotions from [Business Name] every day.", null, null));
        await settings.PublishAsync();

        var stale = await Decide(token, Agree(version: 1));

        Assert.Equal(ConsentDecisionOutcomes.PageChanged, stale.Outcome);
        Assert.Equal(2, stale.Page!.Page!.Version);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);

        Assert.Equal(ConsentDecisionOutcomes.Recorded, (await Decide(token, Agree(version: 2))).Outcome);
        Assert.Equal(2, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).DecisionPageVersion);
    }

    [Fact]
    public async Task Yes_while_the_page_is_switched_off_is_refused_but_no_is_still_accepted()
    {
        var token = await InviteAndGetTokenAsync();
        var page = await _db.TenantConsentPages.SingleAsync();
        page.IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Equal(PublicConsentStates.Unavailable, (await _page.GetAsync(token)).State);
        Assert.Equal(ConsentDecisionOutcomes.Unavailable, (await Decide(token, Agree())).Outcome);
        Assert.Equal(0, _whatsApp.Calls);

        Assert.Equal(ConsentDecisionOutcomes.Recorded, (await Decide(token, Decline())).Outcome);
    }

    [Fact]
    public async Task A_number_that_opted_out_cannot_be_opted_back_in_by_the_page()
    {
        var token = await InviteAndGetTokenAsync();
        var customer = await _db.Customers.SingleAsync();
        customer.OptInStatus = OptInStatus.OptedOut;
        await _db.SaveChangesAsync();

        var result = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.NotAllowed, result.Outcome);
        Assert.Equal(OptInStatus.OptedOut, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    // ------------------------------------------------------------------------------------------------ No

    [Fact]
    public async Task No_saves_not_interested_starts_nothing_sends_nothing_and_is_not_a_failure()
    {
        var token = await InviteAndGetTokenAsync();
        _clock.UtcNow = T0.AddMinutes(5);

        var result = await Decide(token, Decline());

        Assert.Equal(ConsentDecisionOutcomes.Recorded, result.Outcome);
        Assert.Equal("NotInterested", result.Decision);
        Assert.Contains("No WhatsApp conversation will be started", result.Message);

        var invitation = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(ConsentDecisionStatus.NotInterested, invitation.DecisionStatus);
        Assert.Equal(T0.AddMinutes(5), invitation.DecisionAtUtc);
        Assert.Equal("ConsentPage", invitation.DecisionSource);
        Assert.Null(invitation.ConsentRecordedAtUtc);
        Assert.Null(invitation.DecisionConsentText);
        Assert.Equal(WhatsAppInitiationStatus.NotStarted, invitation.WhatsAppInitiation);
        Assert.Equal(ConsentInvitationStatus.Delivered, invitation.Status);   // the SMS was fine; this is not a technical failure
        Assert.Null(invitation.FailureReason);

        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(CampaignCustomerStatus.Pending, (await _db.CampaignCustomers.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.Equal(1, _sms.Requests.Count);   // and no second SMS asking again
        Assert.Contains(CustomerHistoryEventTypes.ConsentDeclined, await EventTypesAsync());
        Assert.DoesNotContain(CustomerHistoryEventTypes.SmsFailed, await EventTypesAsync());
    }

    [Fact]
    public async Task No_needs_no_checkbox_and_repeating_it_changes_nothing()
    {
        var token = await InviteAndGetTokenAsync();

        var first = await Decide(token, new ConsentDecisionRequest("decline", ConsentChecked: false, PageVersion: null));
        var second = await Decide(token, Decline());

        Assert.Equal(ConsentDecisionOutcomes.Recorded, first.Outcome);
        Assert.Equal(ConsentDecisionOutcomes.AlreadyRecorded, second.Outcome);
        Assert.Equal(1, await _db.CustomerHistoryEvents.CountAsync(e => e.EventType == CustomerHistoryEventTypes.ConsentDeclined));
        Assert.Equal(0, _whatsApp.Calls);
    }

    [Fact]
    public async Task After_no_the_same_link_cannot_be_used_to_say_yes()
    {
        var token = await InviteAndGetTokenAsync();
        await Decide(token, Decline());

        var result = await Decide(token, Agree());

        Assert.Equal(ConsentDecisionOutcomes.Conflict, result.Outcome);
        Assert.Equal(ConsentDecisionStatus.NotInterested, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).DecisionStatus);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.Equal(PublicConsentStates.Answered, (await _page.GetAsync(token)).State);
    }

    [Fact]
    public async Task After_yes_the_page_cannot_be_used_to_say_no()
    {
        var token = await InviteAndGetTokenAsync();
        await Decide(token, Agree());

        var result = await Decide(token, Decline());

        Assert.Equal(ConsentDecisionOutcomes.Conflict, result.Outcome);
        Assert.Equal(ConsentDecisionStatus.Granted, (await _db.ConsentInvitations.AsNoTracking().SingleAsync()).DecisionStatus);
        Assert.Equal(OptInStatus.OptedIn, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
    }

    [Fact]
    public async Task Someone_who_said_no_is_not_texted_again_by_a_later_campaign()
    {
        var token = await InviteAndGetTokenAsync();
        await Decide(token, Decline());

        var laterCampaign = new Campaign { Name = "Next month", Status = CampaignStatus.Running, CreatedBy = Guid.NewGuid(), StartedAt = T0 };
        _db.Campaigns.Add(laterCampaign);
        await _db.SaveChangesAsync();
        var result = await _consent.SendAsync(laterCampaign.Id, Guid.NewGuid(), new[] { _customer.Id });

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, _sms.Requests.Count);
    }

    [Fact]
    public async Task A_whatsapp_message_from_a_customer_who_was_sent_the_page_link_is_not_consent()
    {
        await InviteAsync();

        var outcome = await ReplyAsync();

        Assert.False(outcome.InvitationAnswered);
        Assert.Equal(OptInStatus.WaitingForConsent, (await _db.Customers.AsNoTracking().SingleAsync()).OptInStatus);
        Assert.Equal(0, _whatsApp.Calls);
    }

    // ------------------------------------------------------------------------------------------------ the page changes after discovery

    [Fact]
    public async Task If_the_page_is_unpublished_before_the_sms_nothing_is_sent_the_lead_is_kept_and_the_tenant_is_told()
    {
        var page = await _db.TenantConsentPages.SingleAsync();
        page.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await InviteAsync();

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(_sms.Requests);
        Assert.Equal(1, await _db.Customers.CountAsync());   // the discovered lead stays

        var held = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.True(held.HeldForConsentPage);
        Assert.Null(held.TokenHash);
        Assert.Contains("SmsSkipped", await EventTypesAsync());

        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal(TenantNotificationKind.ConsentPageSetupRequired, notice.Kind);
        Assert.Equal(_tenant.Id, notice.TenantId);
    }

    [Fact]
    public async Task Once_the_page_is_live_again_the_held_message_goes_out_without_a_new_discovery_or_a_duplicate()
    {
        var page = await _db.TenantConsentPages.SingleAsync();
        page.IsActive = false;
        await _db.SaveChangesAsync();
        await InviteAsync();

        Assert.Equal(0, await _consent.RetryDueAsync());   // still off: nothing is sent, nothing is repeated on the history
        Assert.Empty(_sms.Requests);
        Assert.Equal(1, (await EventTypesAsync()).Count(e => e == "SmsSkipped"));

        page = await _db.TenantConsentPages.SingleAsync();
        page.IsActive = true;
        await _db.SaveChangesAsync();

        Assert.Equal(1, await _consent.RetryDueAsync());
        Assert.Equal(0, await _consent.RetryDueAsync());
        Assert.Single(_sms.Requests);
        var invitation = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        Assert.False(invitation.HeldForConsentPage);
        Assert.Equal(ConsentDecisionStatus.LinkSent, invitation.DecisionStatus);

        // And discovering the same customers again does not text them a second time.
        var again = await _consent.SendAsync(_campaign.Id, Guid.NewGuid(), new[] { _customer.Id });
        Assert.Equal(1, again.Skipped);
        Assert.Single(_sms.Requests);
    }

    [Fact]
    public async Task Without_a_public_address_for_the_page_no_sms_is_sent()
    {
        _pageOptions.PublicBaseUrl = null;

        var result = await InviteAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Empty(_sms.Requests);
        Assert.Empty(_notifier.Sent);   // not something the tenant can fix
    }

    // ------------------------------------------------------------------------------------------------ evidence stays put

    [Fact]
    public async Task Editing_and_republishing_the_page_never_rewrites_what_an_earlier_customer_agreed_to()
    {
        var token = await InviteAndGetTokenAsync();
        await Decide(token, Agree());
        var before = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        var versionBefore = await _db.ConsentPageVersions.AsNoTracking().SingleAsync(v => v.Version == 1);

        var settings = new ConsentPageSettingsService(_db, _ambient, _clock);
        await settings.SaveAsync(new SaveConsentPageSettingsRequest(
            "Acme Optical", "A new description of the business.", null, "I agree to receive WhatsApp offers from [Business Name].", null, null));
        await settings.PublishAsync();

        var after = await _db.ConsentInvitations.AsNoTracking().SingleAsync();
        var versionAfter = await _db.ConsentPageVersions.AsNoTracking().SingleAsync(v => v.Version == 1);
        Assert.Equal(before.DecisionConsentText, after.DecisionConsentText);
        Assert.Equal(1, after.DecisionPageVersion);
        Assert.Equal(versionBefore.ConsentStatement, versionAfter.ConsentStatement);
        Assert.Equal("Acme Eye Care", versionAfter.BusinessName);
        Assert.Equal(2, await _db.ConsentPageVersions.CountAsync());
        Assert.Equal("Acme Optical", (await _db.ConsentPageVersions.AsNoTracking().SingleAsync(v => v.Version == 2)).BusinessName);

        // The customer who already answered is shown their own answer, in the words they saw.
        var view = await _page.GetAsync(token);
        Assert.Equal(PublicConsentStates.Answered, view.State);
        Assert.Contains("Acme Eye Care", view.Message);
    }
}
