using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Leads.FollowUps;

/// <summary>
/// The "follow up later" list: leads who were interested but could not go ahead yet, each with a date to be
/// contacted again. See <see cref="LeadFollowUpPolicy"/> for the limits that keep it from irritating anyone.
/// </summary>
public interface ILeadFollowUpService
{
    /// <param name="status">One <c>LeadFollowUpStatus</c> name (Suggested for the AI's unconfirmed ones), or null for Scheduled only.</param>
    /// <param name="dueWithinDays">Scheduled follow-ups due within this many days from now (overdue ones included).</param>
    Task<PagedResult<LeadFollowUpDto>> GetPagedAsync(PagedRequest request, string? status = null, int? dueWithinDays = null, CancellationToken cancellationToken = default);

    Task<LeadFollowUpSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Every follow-up the lead has had, newest first.</summary>
    Task<IReadOnlyList<LeadFollowUpDto>> GetForLeadAsync(Guid leadId, CancellationToken cancellationToken = default);

    /// <summary>Parks the lead for a follow-up. Replaces the lead's current Scheduled one. Refused (Conflict) for a
    /// Won/Lost lead, a customer who is not opted in, or a lead that has already had the maximum follow-ups.</summary>
    Task<LeadFollowUpDto> ScheduleAsync(Guid leadId, ScheduleLeadFollowUpRequest request, Guid scheduledByUserId, CancellationToken cancellationToken = default);

    /// <summary>Records the AI's observation that the lead is interested but cannot proceed, as a
    /// <c>Suggested</c> follow-up for a person to confirm or dismiss. Never schedules or sends anything. Stages the
    /// row on the context WITHOUT saving - the caller saves it with the rest of its turn. Returns false, adding
    /// nothing, when a follow-up is already open or was recently dismissed, the lead is closed, the customer is not
    /// opted in, or the lead has had the maximum follow-ups.</summary>
    Task<bool> SuggestAsync(Guid leadId, string? reason, int? months, CancellationToken cancellationToken = default);

    /// <summary>Cancels a Scheduled or Failed follow-up, or dismisses a Suggested one.</summary>
    Task<LeadFollowUpDto> CancelAsync(Guid id, Guid cancelledByUserId, CancellationToken cancellationToken = default);

    /// <summary>Sends a Scheduled or Failed follow-up now, on a person's say-so: it skips the daytime window, the
    /// quiet period and the "customer wrote in since" check, but never opt-in, template approval or quota.</summary>
    Task<LeadFollowUpDto> SendNowAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The scheduled job's pass: sends what has fallen due, inside the tenant's daytime hours.</summary>
    Task<LeadFollowUpRunResult> ProcessDueAsync(CancellationToken cancellationToken = default);
}
