using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

// Two reminders after the consent SMS, "No response" when the link runs out, and the campaign's own length as the limit.
public sealed partial class ConsentFlowTests
{
    private async Task EnableRemindersAsync(int first = 3, int? second = 7)
    {
        _db.LeadDiscoveryProfiles.Add(new LeadDiscoveryProfile
        {
            TenantId = _tenant.Id, TargetBusinessType = "Clinics", BatchSize = 10, ConsentSmsEnabled = true,
            ConsentReminderEnabled = true, ConsentReminderFirstDays = first, ConsentReminderSecondDays = second
        });
        await _db.SaveChangesAsync();
    }

    private Task<ConsentInvitation> InvitationAsync() => _db.ConsentInvitations.SingleAsync();

    private async Task<ConsentReminderRunResult> RemindAtAsync(DateTime utc)
    {
        _clock.UtcNow = utc;
        return await _consent.ProcessRemindersAsync();
    }

    [Fact]
    public async Task No_reminder_goes_while_the_tenant_has_them_switched_off()
    {
        await InviteAsync();

        var result = await RemindAtAsync(T0.AddDays(5));

        Assert.Equal(0, result.Sent);
        Assert.Single(_sms.Requests);
        Assert.Equal(0, (await InvitationAsync()).ReminderCount);
    }

    [Fact]
    public async Task The_first_reminder_goes_on_its_day_with_a_fresh_link_and_does_not_move_the_expiry()
    {
        await EnableRemindersAsync();
        await InviteAsync();
        var invitation = await InvitationAsync();
        var firstHash = invitation.TokenHash;
        var expiry = invitation.TokenExpiresAtUtc;

        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(2))).Sent);
        var result = await RemindAtAsync(T0.AddDays(3));

        Assert.Equal(1, result.Sent);
        Assert.Equal(2, _sms.Requests.Count);
        Assert.StartsWith("Reminder: ", _sms.Requests[1].Body);
        await _db.Entry(invitation).ReloadAsync();
        Assert.Equal(1, invitation.ReminderCount);
        Assert.NotEqual(firstHash, invitation.TokenHash);
        Assert.Equal(expiry, invitation.TokenExpiresAtUtc);
        Assert.Contains("SmsSubmitted", await EventTypesAsync());
    }

    [Fact]
    public async Task The_second_reminder_goes_on_its_day_and_there_is_never_a_third()
    {
        await EnableRemindersAsync();
        await InviteAsync();

        await RemindAtAsync(T0.AddDays(3));
        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(5))).Sent);
        Assert.Equal(1, (await RemindAtAsync(T0.AddDays(7))).Sent);
        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(9))).Sent);
        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(12))).Sent);

        Assert.Equal(3, _sms.Requests.Count); // the invitation and two reminders
        Assert.Equal(2, (await InvitationAsync()).ReminderCount);
    }

    [Fact]
    public async Task A_reminder_waits_for_daytime_in_the_tenants_timezone()
    {
        await EnableRemindersAsync();
        await InviteAsync();

        // 21:00 UTC is 02:30 in India: nothing goes to a phone at night, the reminder waits.
        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(3).AddHours(15))).Sent);
        // The next morning it goes.
        Assert.Equal(1, (await RemindAtAsync(T0.AddDays(4))).Sent);
    }

    [Fact]
    public async Task A_customer_who_answered_or_opted_out_gets_no_reminder()
    {
        await EnableRemindersAsync();
        await InviteAsync();
        _customer.OptInStatus = OptInStatus.OptedOut;
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(3))).Sent);

        var invitation = await InvitationAsync();
        invitation.DecisionStatus = ConsentDecisionStatus.Granted;
        invitation.ConsentRecordedAtUtc = T0.AddDays(1);
        _customer.OptInStatus = OptInStatus.OptedIn;
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(7))).Sent);
        Assert.Single(_sms.Requests);
    }

    [Fact]
    public async Task With_no_campaign_length_the_link_lasts_fourteen_days_then_the_customer_is_no_response()
    {
        await EnableRemindersAsync();
        await InviteAsync();
        var invitation = await InvitationAsync();
        Assert.Equal(T0.AddDays(14), invitation.TokenExpiresAtUtc);

        var result = await RemindAtAsync(T0.AddDays(14).AddHours(1));

        Assert.Equal(1, result.NoResponse);
        Assert.Equal(0, result.Sent);
        await _db.Entry(invitation).ReloadAsync();
        Assert.Equal(ConsentDecisionStatus.Expired, invitation.DecisionStatus);
        Assert.Contains("ConsentExpired", await EventTypesAsync());
        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(15))).NoResponse); // once
    }

    [Fact]
    public async Task A_campaign_that_closes_after_six_days_cuts_the_link_and_the_reminders_to_those_six_days()
    {
        // The first step goes at once; follow-ups 2, 2 and 2 days apart - the campaign's last message falls due on day 6.
        foreach (var n in new[] { 1, 2, 3 })
            _db.CampaignSteps.Add(new CampaignStep { CampaignId = _campaign.Id, StepNumber = n, StepType = "FollowUp", MessageText = "Hi", MessageTemplateId = _template.Id, IsActive = true, DelayDaysAfterPrevious = 2 });
        await _db.SaveChangesAsync();
        await EnableRemindersAsync(3, 7);
        await InviteAsync();
        var invitation = await InvitationAsync();
        Assert.Equal(T0.AddDays(6), invitation.TokenExpiresAtUtc);

        Assert.Equal(1, (await RemindAtAsync(T0.AddDays(3))).Sent);
        // Day 7 is after the link closed: no second reminder, and the customer is "No response" from day 6.
        var late = await RemindAtAsync(T0.AddDays(7));

        Assert.Equal(0, late.Sent);
        Assert.Equal(1, late.NoResponse);
        Assert.Equal(2, _sms.Requests.Count);
        await _db.Entry(invitation).ReloadAsync();
        Assert.Equal(ConsentDecisionStatus.Expired, invitation.DecisionStatus);
    }

    [Fact]
    public async Task A_reminder_that_the_provider_refuses_is_counted_so_it_cannot_repeat_every_hour()
    {
        await EnableRemindersAsync();
        await InviteAsync();
        _sms.Results.Enqueue(new ConsentSmsSendResult(false, null, "down", Transient: true));

        var result = await RemindAtAsync(T0.AddDays(3));

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, (await InvitationAsync()).ReminderCount);
        Assert.Equal(0, (await RemindAtAsync(T0.AddDays(3).AddHours(1))).Sent);
    }
}
