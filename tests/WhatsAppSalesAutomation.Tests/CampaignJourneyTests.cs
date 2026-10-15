using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A customer's journey through a campaign: consent, campaign status and messages, first step first, each with its detail.</summary>
public sealed class CampaignJourneyTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);

    private static Campaign NewCampaign()
    {
        var campaign = new Campaign { Name = "Eye clinic - 2026-10-15", Status = CampaignStatus.Running };
        campaign.Steps.Add(new CampaignStep { StepNumber = 0, StepType = "Initial", MessageText = "Hi", IsActive = true });
        campaign.Steps.Add(new CampaignStep { StepNumber = 1, StepType = "FollowUp1", MessageText = "Again", IsActive = true });
        return campaign;
    }

    private static Customer NewCustomer(Action<Customer>? tweak = null)
    {
        var customer = new Customer
        {
            PhoneNumberE164 = "+919800000001", FirstName = "Sim", LastName = "Owner 1", Source = "Lead discovery",
            OptInStatus = OptInStatus.WaitingForConsent, CreatedAt = T0
        };
        tweak?.Invoke(customer);
        return customer;
    }

    private static CampaignCustomer NewMember(Action<CampaignCustomer>? tweak = null)
    {
        var member = new CampaignCustomer { Status = CampaignCustomerStatus.Pending, CurrentStepNumber = -1, CreatedAt = T0.AddMinutes(1) };
        tweak?.Invoke(member);
        return member;
    }

    private static string[] Titles(CampaignCustomerJourneyDto journey) => journey.Steps.Select(s => s.Title).ToArray();

    [Fact]
    public void A_customer_still_waiting_starts_with_waiting_for_consent_then_pending_and_nothing_else()
    {
        var journey = CampaignJourneyBuilder.Build(NewCampaign(), NewMember(), NewCustomer(), Array.Empty<Message>(), Array.Empty<Message>());

        Assert.Equal(new[] { "Waiting for consent", "Pending" }, Titles(journey));
        Assert.Equal("Consent", journey.Steps[0].Dimension);
        Assert.Equal("Campaign", journey.Steps[1].Dimension);
        Assert.Equal("WaitingForConsent", journey.OptInStatus);
    }

    [Fact]
    public void The_full_story_runs_waiting_consent_pending_sent_awaiting_response_in_that_order_with_detail_on_each()
    {
        var customer = NewCustomer(c =>
        {
            c.OptInStatus = OptInStatus.OptedIn;
            c.OptInTimestamp = T0.AddMinutes(30);
            c.OptInSource = "CustomerMessagedFirst";
            c.OptInProof = "wamid.ABC";
            c.OptInScope = ConsentScope.Marketing;
        });
        var member = NewMember(m =>
        {
            m.Status = CampaignCustomerStatus.AwaitingResponse;
            m.CurrentStepNumber = 0;
            m.LastMessageSentAt = T0.AddHours(1);
            m.NextFollowUpDueAt = T0.AddDays(3);
        });
        var sent = new Message
        {
            CampaignStepNumber = 0, TemplateName = "intro_offer", Status = MessageStatus.Delivered, CreatedAt = T0.AddHours(1),
            SentAt = T0.AddHours(1), DeliveredAt = T0.AddHours(1).AddSeconds(5), AttemptCount = 1, WhatsAppMessageId = "wamid.XYZ"
        };

        var journey = CampaignJourneyBuilder.Build(NewCampaign(), member, customer, new[] { sent }, Array.Empty<Message>());

        // consent at +30m comes after "Pending" (attached at +1m): the order is by time, so the story reads as it happened.
        Assert.Equal(
            new[] { "Waiting for consent", "Pending", "Got consent", "Initial message delivered", "Awaiting response" },
            Titles(journey));

        var consent = journey.Steps.Single(s => s.Title == "Got consent");
        Assert.Contains(consent.Details, d => d.Label == "How it was captured" && d.Value == "CustomerMessagedFirst");
        Assert.Contains(consent.Details, d => d.Label == "Proof" && d.Value == "wamid.ABC");
        Assert.Contains(consent.Details, d => d.Label == "Covers" && d.Value == "Marketing");

        var message = journey.Steps.Single(s => s.Dimension == "Message");
        Assert.Contains(message.Details, d => d.Label == "Template" && d.Value == "intro_offer");
        Assert.Contains(message.Details, d => d.Label == "WhatsApp message id" && d.Value == "wamid.XYZ");
        Assert.Contains(message.Details, d => d.Label == "Delivered at");

        var awaiting = journey.Steps.Last();
        Assert.Contains(awaiting.Details, d => d.Label == "Next follow-up due");
        Assert.All(journey.Steps, s => Assert.NotEmpty(s.Details));
    }

    [Fact]
    public void A_customer_who_arrived_with_consent_has_no_waiting_step()
    {
        var customer = NewCustomer(c =>
        {
            c.OptInStatus = OptInStatus.OptedIn;
            c.OptInTimestamp = T0;   // given when they were created
        });

        var journey = CampaignJourneyBuilder.Build(NewCampaign(), NewMember(), customer, Array.Empty<Message>(), Array.Empty<Message>());

        Assert.Equal(new[] { "Got consent", "Pending" }, Titles(journey));
    }

    [Fact]
    public void A_failed_send_shows_why()
    {
        var failed = new Message
        {
            CampaignStepNumber = 0, Status = MessageStatus.Failed, FailureReason = "Template rejected", CreatedAt = T0.AddHours(1), AttemptCount = 3
        };
        var member = NewMember(m => m.Status = CampaignCustomerStatus.Failed);

        var journey = CampaignJourneyBuilder.Build(NewCampaign(), member, NewCustomer(), new[] { failed }, Array.Empty<Message>());

        var step = journey.Steps.Single(s => s.Dimension == "Message");
        Assert.Equal("Initial message failed", step.Title);
        Assert.Contains("Template rejected", step.Summary);
        Assert.Contains(step.Details, d => d.Label == "Send attempts" && d.Value == "3");
        Assert.Equal("Failed", journey.Steps.Last().State);   // and the campaign ended there
    }

    [Fact]
    public void A_reply_and_an_opt_out_each_get_a_step()
    {
        var customer = NewCustomer(c =>
        {
            c.OptInStatus = OptInStatus.OptedOut;
            c.OptOutTimestamp = T0.AddDays(1);
            c.OptOutSource = OptOutSource.ExactKeyword;
        });
        var member = NewMember(m =>
        {
            m.Status = CampaignCustomerStatus.OptedOut;
            m.LastCustomerResponseAt = T0.AddHours(5);
            m.StoppedReason = "Customer opted out";
        });
        var reply = new Message { Direction = MessageDirection.Inbound, Text = "STOP", CreatedAt = T0.AddHours(5) };

        var journey = CampaignJourneyBuilder.Build(NewCampaign(), member, customer, Array.Empty<Message>(), new[] { reply });

        Assert.Contains("Responded", Titles(journey));
        Assert.Contains("Opted out", Titles(journey));
        Assert.Contains(journey.Steps.Single(s => s.Title == "Responded").Details, d => d.Value == "STOP");
        Assert.Equal("Opted out", journey.Steps.Last().Title);
    }

    [Fact]
    public void The_first_step_has_the_earliest_time_and_no_step_is_dated_before_the_one_above_it()
    {
        var customer = NewCustomer(c => { c.OptInTimestamp = T0.AddHours(2); c.OptInStatus = OptInStatus.OptedIn; });
        var member = NewMember(m => { m.Status = CampaignCustomerStatus.AwaitingResponse; m.CurrentStepNumber = 0; });
        var sent = new Message { CampaignStepNumber = 0, Status = MessageStatus.Sent, CreatedAt = T0.AddHours(3), SentAt = T0.AddHours(3) };

        var journey = CampaignJourneyBuilder.Build(NewCampaign(), member, customer, new[] { sent }, Array.Empty<Message>());

        var times = journey.Steps.Select(s => s.OccurredAtUtc!.Value).ToList();
        Assert.Equal(times.OrderBy(t => t), times);
        Assert.Equal("Waiting for consent", journey.Steps[0].Title);
    }
}
