using System.Globalization;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Campaigns;

/// <summary>
/// Builds a customer's journey through one campaign from what is already recorded - the customer's consent fields, their
/// CampaignCustomer row and the messages - so it also covers history from before any event log existed. Consent, campaign
/// status and messages are separate dimensions, and each step says which one moved. Times are UTC; the screen renders them.
/// </summary>
public static class CampaignJourneyBuilder
{
    private const int ReplyPreviewLength = 200;

    public static CampaignCustomerJourneyDto Build(
        Campaign campaign,
        CampaignCustomer member,
        Customer customer,
        IReadOnlyCollection<Message> outbound,
        IReadOnlyCollection<Message> inbound)
    {
        var steps = new List<(int Order, CampaignJourneyStepDto Step)>();
        void Add(CampaignJourneyStepDto step) => steps.Add((steps.Count, step));

        var name = string.IsNullOrWhiteSpace(customer.FullName) ? customer.PhoneNumberE164 : customer.FullName;

        // 1. Consent not yet given. Skipped for a customer who arrived already opted in (an import with consent).
        var optedInAtStart = customer.OptInTimestamp is { } consentAt && consentAt <= customer.CreatedAt;
        if (!optedInAtStart)
        {
            var waiting = customer.OptInStatus == OptInStatus.PendingOptIn ? OptInStatus.PendingOptIn : OptInStatus.WaitingForConsent;
            Add(new CampaignJourneyStepDto(
                "consent-waiting",
                waiting == OptInStatus.PendingOptIn ? "Pending opt-in" : "Waiting for consent",
                "Consent", waiting.ToString(), customer.CreatedAt,
                "The customer was added without consent, so they cannot be messaged until they opt in.",
                Details(
                    ("Customer", name),
                    ("Phone", customer.PhoneNumberE164),
                    ("Added from", customer.Source ?? "Not recorded"),
                    ("Consent at this point", "None recorded"),
                    ("Can be messaged", "No - only an opted-in customer can be"))));
        }

        // 2. Consent given.
        if (customer.OptInTimestamp is { } optedInAt)
        {
            Add(new CampaignJourneyStepDto(
                "consent-given", "Got consent", "Consent", OptInStatus.OptedIn.ToString(), optedInAt,
                "The customer's consent was recorded, so they can be messaged.",
                Details(
                    ("How it was captured", customer.OptInSource ?? "Not recorded"),
                    ("Proof", customer.OptInProof ?? "Not recorded"),
                    ("Covers", customer.OptInScope?.ToString() ?? "Not recorded"),
                    ("Recorded at", Format(optedInAt)))));
        }

        // 3. Attached to this campaign - the campaign status starts at Pending.
        Add(new CampaignJourneyStepDto(
            "campaign-added", "Pending", "Campaign", CampaignCustomerStatus.Pending.ToString(), member.CreatedAt,
            "Added to the campaign. The initial message has not gone out yet.",
            Details(
                ("Campaign", campaign.Name),
                ("Campaign status", campaign.Status.ToString()),
                ("Customer status in campaign", CampaignCustomerStatus.Pending.ToString()),
                ("Opt-in status now", customer.OptInStatus.ToString()),
                ("Steps in the campaign", campaign.Steps.Count(s => s.IsActive).ToString(CultureInfo.InvariantCulture)))));

        // 4. Every message this campaign sent them.
        var ordered = outbound.OrderBy(m => m.CampaignStepNumber ?? 0).ThenBy(m => m.CreatedAt).ToList();
        foreach (var message in ordered)
        {
            var stepLabel = StepLabel(message.CampaignStepNumber ?? 0);
            // Dated when it went out; delivered and read times are in the detail, and the title shows how far it got.
            var at = message.SentAt ?? message.CreatedAt;

            Add(new CampaignJourneyStepDto(
                $"message-{message.Id}", $"{stepLabel} {message.Status.ToString().ToLowerInvariant()}", "Message", message.Status.ToString(), at,
                message.Status == MessageStatus.Failed
                    ? $"The message could not be sent{(message.FailureReason is null ? "." : ": " + message.FailureReason)}"
                    : "The campaign message to this customer.",
                Details(
                    ("Step", stepLabel),
                    ("Template", message.TemplateName ?? "Message text only"),
                    ("Status", message.Status.ToString()),
                    ("Queued at", Format(message.CreatedAt)),
                    ("Sent at", Format(message.SentAt)),
                    ("Delivered at", Format(message.DeliveredAt)),
                    ("Read at", Format(message.ReadAt)),
                    ("Send attempts", message.AttemptCount.ToString(CultureInfo.InvariantCulture)),
                    ("Failure reason", message.FailureReason),
                    ("WhatsApp message id", message.WhatsAppMessageId))));
        }

        // 5. Awaiting a reply once the first message has gone out.
        var firstSent = ordered.Where(m => m.SentAt is not null).OrderBy(m => m.SentAt).FirstOrDefault();
        if (firstSent?.SentAt is { } firstSentAt && member.Status != CampaignCustomerStatus.Pending)
        {
            Add(new CampaignJourneyStepDto(
                "campaign-awaiting", "Awaiting response", "Campaign", CampaignCustomerStatus.AwaitingResponse.ToString(), firstSentAt,
                "The first message went out; the campaign now waits for the customer to reply.",
                Details(
                    ("Customer status in campaign", CampaignCustomerStatus.AwaitingResponse.ToString()),
                    ("Last step sent", member.CurrentStepNumber < 0 ? "None" : StepLabel(member.CurrentStepNumber)),
                    ("Last message sent", Format(member.LastMessageSentAt)),
                    ("Next follow-up due", member.NextFollowUpDueAt is null ? "No further step" : Format(member.NextFollowUpDueAt)))));
        }

        // 6. A reply.
        if (member.LastCustomerResponseAt is { } repliedAt)
        {
            var reply = inbound.OrderBy(m => m.CreatedAt).FirstOrDefault(m => m.CreatedAt >= (firstSent?.SentAt ?? member.CreatedAt));
            Add(new CampaignJourneyStepDto(
                "campaign-responded", "Responded", "Campaign", CampaignCustomerStatus.Responded.ToString(), repliedAt,
                "The customer replied. Automated follow-ups for this campaign stop.",
                Details(
                    ("Replied at", Format(repliedAt)),
                    ("Message", reply?.Text is { Length: > 0 } text ? Truncate(text) : null),
                    ("Customer status in campaign", member.Status.ToString()))));
        }

        // 7. Opted out.
        if (customer.OptOutTimestamp is { } optedOutAt)
        {
            Add(new CampaignJourneyStepDto(
                "consent-withdrawn", "Opted out", "Consent", OptInStatus.OptedOut.ToString(), optedOutAt,
                "The customer withdrew consent. No further messages are sent.",
                Details(
                    ("Detected by", customer.OptOutSource?.ToString() ?? "Not recorded"),
                    ("Recorded at", Format(optedOutAt)),
                    ("Reason in campaign", member.StoppedReason))));
        }

        // 8. A terminal campaign status that nothing above already shows.
        if (member.Status is CampaignCustomerStatus.Completed or CampaignCustomerStatus.HandedOff or CampaignCustomerStatus.Failed)
        {
            var endedAt = member.UpdatedAt ?? member.LastMessageSentAt ?? member.CreatedAt;
            Add(new CampaignJourneyStepDto(
                "campaign-ended", member.Status.ToString(), "Campaign", member.Status.ToString(), endedAt,
                member.Status switch
                {
                    CampaignCustomerStatus.Completed => "Every configured step was sent with no response.",
                    CampaignCustomerStatus.HandedOff => "Passed to a person; the campaign's automation for this customer stops.",
                    _ => "Sending to this customer failed after every attempt for the current step."
                },
                Details(
                    ("Customer status in campaign", member.Status.ToString()),
                    ("Reason", member.StoppedReason),
                    ("Last step sent", member.CurrentStepNumber < 0 ? "None" : StepLabel(member.CurrentStepNumber)),
                    ("Last message sent", Format(member.LastMessageSentAt)))));
        }

        // First step first: oldest at the top, ties kept in the order the steps were built (consent, campaign, messages).
        var ordering = steps
            .OrderBy(s => s.Step.OccurredAtUtc ?? DateTime.MaxValue)
            .ThenBy(s => s.Order)
            .Select(s => s.Step)
            .ToList();

        return new CampaignCustomerJourneyDto(
            customer.Id, name, customer.PhoneNumberE164, customer.OptInStatus.ToString(), member.Status.ToString(), ordering);
    }

    private static string StepLabel(int stepNumber) => stepNumber == 0 ? "Initial message" : $"Follow-up {stepNumber}";

    private static IReadOnlyList<CampaignJourneyDetailDto> Details(params (string Label, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new CampaignJourneyDetailDto(p.Label, p.Value!)).ToList();

    /// <summary>The extra times inside a step's details, in UTC; the step's own time is rendered by the screen in the viewer's timezone.</summary>
    private static string? Format(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Truncate(string text) =>
        text.Length <= ReplyPreviewLength ? text : text[..ReplyPreviewLength] + "…";
}
