namespace WhatsAppSalesAutomation.Application.Leads.FollowUps;

public record LeadFollowUpDto(
    Guid Id,
    Guid LeadId,
    Guid CustomerId,
    string CustomerName,
    string CustomerPhoneNumberE164,
    string LeadStage,
    string Status,
    DateTime DueAt,
    int? IntervalMonths,
    string? Reason,
    Guid? MessageTemplateId,
    string? MessageTemplateName,
    int FollowUpNumber,
    DateTime? SentAt,
    string? OutcomeNote,
    DateTime CreatedAt);

/// <summary>Schedules the lead's next follow-up. Exactly one of <paramref name="Months"/> (1, 2, 3 ... up to
/// a year) or <paramref name="DueAt"/> (an exact date) is given. Replaces any follow-up already scheduled for the lead.</summary>
public record ScheduleLeadFollowUpRequest(int? Months, DateTime? DueAt, Guid MessageTemplateId, string? Reason);

/// <summary>Header numbers for the follow-up list. <paramref name="Suggested"/> is what the AI proposed and nobody
/// has confirmed or dismissed yet.</summary>
public record LeadFollowUpSummaryDto(int Scheduled, int DueNow, int DueWithin30Days, int Sent, int Suggested);

/// <summary>What one pass of the sender did.</summary>
public record LeadFollowUpRunResult(int Considered, int Sent, int Failed, int Skipped, int Deferred, int Cancelled)
{
    public static readonly LeadFollowUpRunResult Empty = new(0, 0, 0, 0, 0, 0);

    public string Describe() =>
        $"considered={Considered} sent={Sent} failed={Failed} skipped={Skipped} deferred={Deferred} cancelled={Cancelled}";
}
