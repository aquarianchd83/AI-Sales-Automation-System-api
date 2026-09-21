namespace WhatsAppSalesAutomation.Application.Reports;

// Rates are nullable, not zero, everywhere below. "0% of 0" is no data, and a report that shows 0% for
// a campaign that has sent nothing tells a reader the campaign performed terribly rather than that
// it has not run.

/// <summary>The window every report is computed over: the last <see cref="Days"/> days, ending now.</summary>
public record ReportWindow(int Days, DateTime From, DateTime To);

// ── Campaign performance ────────────────────────────────────────────────────────────────

/// <remarks>
/// TWO time bases, deliberately. The customer-level columns (Audience, Contacted, Responded, OptedOut,
/// HandedOff) are over the campaign's whole life: a reply that arrives on day 31 to a message sent on
/// day 2 is still that campaign's reply, and clipping it to the window would count the contact and not
/// the response. The message-level columns (MessagesSent, Delivered, Read, Failed) are over the window,
/// because delivery and open behaviour is about recent sends.
/// </remarks>
/// <param name="Audience">Customers enrolled in the campaign, ever.</param>
/// <param name="Contacted">Enrolled customers who have been sent at least one message.</param>
/// <param name="MessagesSent">Outbound messages created in the window that left the queue (Sent, Delivered or Read).</param>
/// <param name="ResponseRate">Responded / Contacted.</param>
/// <param name="DeliveryRate">Delivered / MessagesSent.</param>
/// <param name="ReadRate">Read / Delivered - of what arrived, how much was opened.</param>
public record CampaignPerformanceRow(
    Guid CampaignId,
    string Name,
    string Status,
    int Audience,
    int Contacted,
    int MessagesSent,
    int Delivered,
    int Read,
    int Failed,
    int Responded,
    int OptedOut,
    int HandedOff,
    double? DeliveryRate,
    double? ReadRate,
    double? ResponseRate,
    double? OptOutRate);

public record CampaignPerformanceReportDto(
    ReportWindow Window,
    IReadOnlyList<CampaignPerformanceRow> Campaigns,
    CampaignPerformanceRow Totals);

// ── Lead funnel ─────────────────────────────────────────────────────────────────────────

public record FunnelStageRow(string Stage, int Count, double? Share);

public record LeadFunnelReportDto(
    ReportWindow Window,
    int TotalLeads,
    IReadOnlyList<FunnelStageRow> Stages,
    IReadOnlyList<FunnelStageRow> ByScoreBand,
    int HotLeads,
    int FromCampaigns,
    int Organic,
    /// <summary>Qualified, Negotiation or Won as a share of all leads created in the window.</summary>
    double? QualifiedRate,
    double? WonRate,
    double? LostRate,
    /// <summary>The funnel is a snapshot of where each lead is NOW, not a record of where it has been:
    /// the schema keeps no stage history, so a lead that was Qualified and is now Lost is counted only
    /// as Lost. Read the qualified rate as a floor.</summary>
    string Note);

// ── Human agent performance ─────────────────────────────────────────────────────────────

public record AgentPerformanceRow(
    Guid UserId,
    string Name,
    int LeadsAssigned,
    int LeadsWon,
    int LeadsLost,
    double? WinRate,
    int HandoffsAssigned,
    int HandoffsResolved,
    int HandoffsOpen,
    double? AverageResolutionMinutes);

public record HumanAgentPerformanceReportDto(ReportWindow Window, IReadOnlyList<AgentPerformanceRow> Agents);

// ── AI performance ──────────────────────────────────────────────────────────────────────

public record AiModelRow(string Model, int Interactions, double? AverageConfidence, double? AverageLatencyMs);

public record AiDailyRow(DateTime Date, int Interactions, int Escalated);

/// <summary>What the AI sales agent did. For how well its qualification questions and scoring rules are
/// working, see <c>GET /api/v1/agent-performance</c>; this one is about volume, outcome and cost.</summary>
public record AiPerformanceReportDto(
    ReportWindow Window,
    int Interactions,
    int Replied,
    int Escalated,
    int NoActionNeeded,
    double? EscalationRate,
    double? AverageConfidence,
    double? AverageLatencyMs,
    long PromptTokens,
    long CompletionTokens,
    int BuyingIntentReported,
    int HumanRequestReported,
    int OptOutReported,
    IReadOnlyList<AiModelRow> ByModel,
    IReadOnlyList<AiDailyRow> Daily);
