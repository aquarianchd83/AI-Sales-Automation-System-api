using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Reports;

public interface IReportService
{
    Task<CampaignPerformanceReportDto> GetCampaignPerformanceAsync(int days, Guid? campaignId, CancellationToken cancellationToken = default);

    Task<LeadFunnelReportDto> GetLeadFunnelAsync(int days, CancellationToken cancellationToken = default);

    Task<HumanAgentPerformanceReportDto> GetAgentPerformanceAsync(int days, CancellationToken cancellationToken = default);

    Task<AiPerformanceReportDto> GetAiPerformanceAsync(int days, CancellationToken cancellationToken = default);
}

/// <summary>
/// The four reports of the Phase 1 design. Every query goes through the tenant-filtered context, so a
/// report is inherently the caller's own tenant's numbers - there is no tenant parameter to get wrong.
///
/// Counting is pushed into SQL with GROUP BY; the few places that pull rows into memory (average
/// resolution time, which has no portable date-difference translation) say so where they do it.
/// </summary>
public sealed class ReportService : IReportService
{
    public const int MinDays = 1;
    public const int MaxDays = 365;
    public const int DefaultDays = 30;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _clock;

    public ReportService(IApplicationDbContext context, IDateTimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <summary>Clamped rather than rejected: a report for "the last 10,000 days" is a request for
    /// everything, and the sensible reading of it is the maximum, not an error.</summary>
    private ReportWindow Window(int days)
    {
        var clamped = Math.Clamp(days, MinDays, MaxDays);
        var to = _clock.UtcNow;
        return new ReportWindow(clamped, to.AddDays(-clamped), to);
    }

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round((double)numerator / denominator, 4);

    // ── Campaign performance ─────────────────────────────────────────────────────────────

    public async Task<CampaignPerformanceReportDto> GetCampaignPerformanceAsync(
        int days, Guid? campaignId, CancellationToken cancellationToken = default)
    {
        var window = Window(days);

        var campaigns = await _context.Campaigns.AsNoTracking()
            .Where(c => c.Status != CampaignStatus.Draft && (campaignId == null || c.Id == campaignId))
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Status })
            .ToListAsync(cancellationToken);

        var ids = campaigns.Select(c => c.Id).ToList();

        // Customer-level counts are over the campaign's whole life, not the window: "how many of the
        // people we contacted replied" is a property of the campaign, and clipping it to 30 days would
        // count a reply that arrived on day 31 against a contact made on day 2.
        var customers = await _context.CampaignCustomers.AsNoTracking()
            .Where(cc => ids.Contains(cc.CampaignId))
            .GroupBy(cc => cc.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Audience = g.Count(),
                Contacted = g.Count(x => x.LastMessageSentAt != null),
                Responded = g.Count(x => x.LastCustomerResponseAt != null),
                OptedOut = g.Count(x => x.Status == CampaignCustomerStatus.OptedOut),
                HandedOff = g.Count(x => x.Status == CampaignCustomerStatus.HandedOff)
            })
            .ToDictionaryAsync(x => x.CampaignId, cancellationToken);

        // Message-level counts ARE windowed: delivery and read behaviour is about recent sends.
        var messages = await (
                from m in _context.Messages.AsNoTracking()
                join cc in _context.CampaignCustomers.AsNoTracking() on m.CampaignCustomerId equals cc.Id
                where m.Direction == MessageDirection.Outbound
                      && m.CreatedAt >= window.From && m.CreatedAt <= window.To
                      && ids.Contains(cc.CampaignId)
                select new { cc.CampaignId, m.Status })
            .GroupBy(x => x.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Sent = g.Count(x => x.Status == MessageStatus.Sent || x.Status == MessageStatus.Delivered || x.Status == MessageStatus.Read),
                Delivered = g.Count(x => x.Status == MessageStatus.Delivered || x.Status == MessageStatus.Read),
                Read = g.Count(x => x.Status == MessageStatus.Read),
                Failed = g.Count(x => x.Status == MessageStatus.Failed)
            })
            .ToDictionaryAsync(x => x.CampaignId, cancellationToken);

        var rows = campaigns.Select(c =>
        {
            customers.TryGetValue(c.Id, out var cu);
            messages.TryGetValue(c.Id, out var m);
            return Row(c.Id, c.Name, c.Status.ToString(),
                cu?.Audience ?? 0, cu?.Contacted ?? 0, m?.Sent ?? 0, m?.Delivered ?? 0, m?.Read ?? 0, m?.Failed ?? 0,
                cu?.Responded ?? 0, cu?.OptedOut ?? 0, cu?.HandedOff ?? 0);
        }).ToList();

        var totals = Row(Guid.Empty, "Total", string.Empty,
            rows.Sum(r => r.Audience), rows.Sum(r => r.Contacted), rows.Sum(r => r.MessagesSent), rows.Sum(r => r.Delivered),
            rows.Sum(r => r.Read), rows.Sum(r => r.Failed), rows.Sum(r => r.Responded), rows.Sum(r => r.OptedOut), rows.Sum(r => r.HandedOff));

        return new CampaignPerformanceReportDto(window, rows, totals);
    }

    private static CampaignPerformanceRow Row(
        Guid id, string name, string status, int audience, int contacted, int sent, int delivered, int read, int failed,
        int responded, int optedOut, int handedOff) =>
        new(id, name, status, audience, contacted, sent, delivered, read, failed, responded, optedOut, handedOff,
            Rate(delivered, sent), Rate(read, delivered), Rate(responded, contacted), Rate(optedOut, contacted));

    // ── Lead funnel ──────────────────────────────────────────────────────────────────────

    public async Task<LeadFunnelReportDto> GetLeadFunnelAsync(int days, CancellationToken cancellationToken = default)
    {
        var window = Window(days);

        var leads = _context.Leads.AsNoTracking().Where(l => l.CreatedAt >= window.From && l.CreatedAt <= window.To);

        var byStage = await leads.GroupBy(l => l.Stage).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var byBand = await leads.GroupBy(l => l.Score).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var hot = await leads.CountAsync(l => l.HotLeadDetectedAt != null, cancellationToken);
        var fromCampaigns = await leads.CountAsync(l => l.CampaignId != null, cancellationToken);

        var total = byStage.Sum(s => s.Count);

        // Every stage is listed, including empty ones, in funnel order: a report that omits "Won"
        // because there were none looks like the column does not exist.
        var stages = Enum.GetValues<LeadStage>()
            .Select(s => new FunnelStageRow(s.ToString(), byStage.FirstOrDefault(x => x.Key == s)?.Count ?? 0, null))
            .Select(r => r with { Share = Rate(r.Count, total) })
            .ToList();

        var bands = Enum.GetValues<LeadScoreBand>()
            .Select(b => new FunnelStageRow(b.ToString(), byBand.FirstOrDefault(x => x.Key == b)?.Count ?? 0, null))
            .Select(r => r with { Share = Rate(r.Count, total) })
            .ToList();

        int Count(LeadStage stage) => byStage.FirstOrDefault(x => x.Key == stage)?.Count ?? 0;
        var qualified = Count(LeadStage.Qualified) + Count(LeadStage.Negotiation) + Count(LeadStage.Won);

        return new LeadFunnelReportDto(
            window, total, stages, bands, hot, fromCampaigns, total - fromCampaigns,
            Rate(qualified, total), Rate(Count(LeadStage.Won), total), Rate(Count(LeadStage.Lost), total),
            "A snapshot of where each lead is now, not where it has been: no stage history is kept, so a lead " +
            "that was Qualified and is now Lost is counted only as Lost. Read the qualified rate as a floor.");
    }

    // ── Human agent performance ──────────────────────────────────────────────────────────

    public async Task<HumanAgentPerformanceReportDto> GetAgentPerformanceAsync(int days, CancellationToken cancellationToken = default)
    {
        var window = Window(days);

        var leads = await _context.Leads.AsNoTracking()
            .Where(l => l.AssignedTo != null && l.CreatedAt >= window.From && l.CreatedAt <= window.To)
            .GroupBy(l => l.AssignedTo!.Value)
            .Select(g => new
            {
                UserId = g.Key,
                Assigned = g.Count(),
                Won = g.Count(l => l.Stage == LeadStage.Won),
                Lost = g.Count(l => l.Stage == LeadStage.Lost)
            })
            .ToDictionaryAsync(x => x.UserId, cancellationToken);

        var assigned = await _context.HumanHandoffs.AsNoTracking()
            .Where(h => h.AssignedAgentId != null && h.AssignedAt != null && h.AssignedAt >= window.From && h.AssignedAt <= window.To)
            .GroupBy(h => h.AssignedAgentId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, cancellationToken);

        // Open is a live queue, not a windowed count: a handoff assigned two months ago and still
        // unresolved is exactly what this column exists to show.
        var open = await _context.HumanHandoffs.AsNoTracking()
            .Where(h => h.AssignedAgentId != null && (h.Status == HandoffStatus.Assigned || h.Status == HandoffStatus.InProgress))
            .GroupBy(h => h.AssignedAgentId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, cancellationToken);

        // In memory: a difference between two datetimes has no translation that behaves the same on
        // SQL Server and SQLite. Bounded by handoffs resolved in the window.
        var resolved = await _context.HumanHandoffs.AsNoTracking()
            .Where(h => h.AssignedAgentId != null && h.AssignedAt != null && h.ResolvedAt != null
                        && h.ResolvedAt >= window.From && h.ResolvedAt <= window.To)
            .Select(h => new { h.AssignedAgentId, h.AssignedAt, h.ResolvedAt })
            .ToListAsync(cancellationToken);

        var resolutionByUser = resolved
            .GroupBy(r => r.AssignedAgentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), AverageMinutes: g.Average(r => (r.ResolvedAt!.Value - r.AssignedAt!.Value).TotalMinutes)));

        var userIds = leads.Keys.Union(assigned.Keys).Union(open.Keys).Union(resolutionByUser.Keys).ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var agents = userIds.Select(id =>
        {
            leads.TryGetValue(id, out var l);
            resolutionByUser.TryGetValue(id, out var r);
            return new AgentPerformanceRow(
                id, names.GetValueOrDefault(id) ?? "Unknown user",
                l?.Assigned ?? 0, l?.Won ?? 0, l?.Lost ?? 0, Rate(l?.Won ?? 0, l?.Assigned ?? 0),
                assigned.GetValueOrDefault(id), r.Count, open.GetValueOrDefault(id),
                r.Count == 0 ? null : Math.Round(r.AverageMinutes, 1));
        })
        .OrderByDescending(a => a.LeadsAssigned + a.HandoffsAssigned).ThenBy(a => a.Name)
        .ToList();

        return new HumanAgentPerformanceReportDto(window, agents);
    }

    // ── AI performance ───────────────────────────────────────────────────────────────────

    public async Task<AiPerformanceReportDto> GetAiPerformanceAsync(int days, CancellationToken cancellationToken = default)
    {
        var window = Window(days);
        var interactions = _context.AiInteractions.AsNoTracking().Where(i => i.CreatedAt >= window.From && i.CreatedAt <= window.To);

        var byAction = await interactions.GroupBy(i => i.ActionTaken).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Actions(AiActionTaken a) => byAction.FirstOrDefault(x => x.Key == a)?.Count ?? 0;
        var total = byAction.Sum(x => x.Count);

        // Nullable projections so an empty window averages to null rather than throwing.
        var confidence = await interactions.Select(i => (double?)i.ConfidenceScore).AverageAsync(cancellationToken);
        var latency = await interactions.Select(i => (double?)i.LatencyMs).AverageAsync(cancellationToken);
        var promptTokens = await interactions.SumAsync(i => (long?)i.PromptTokens, cancellationToken) ?? 0;
        var completionTokens = await interactions.SumAsync(i => (long?)i.CompletionTokens, cancellationToken) ?? 0;

        var buying = await interactions.CountAsync(i => i.BuyingIntentReported, cancellationToken);
        var human = await interactions.CountAsync(i => i.HumanRequestReported, cancellationToken);
        var optOut = await interactions.CountAsync(i => i.OptOutReported, cancellationToken);

        var byModel = await interactions.GroupBy(i => i.ModelUsed)
            .Select(g => new
            {
                Model = g.Key,
                Count = g.Count(),
                Confidence = g.Average(i => (double?)i.ConfidenceScore),
                Latency = g.Average(i => (double?)i.LatencyMs)
            })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        var daily = await interactions.GroupBy(i => i.CreatedAt.Date)
            .Select(g => new
            {
                Date = g.Key,
                Count = g.Count(),
                Escalated = g.Count(i => i.ActionTaken == AiActionTaken.Escalated)
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        return new AiPerformanceReportDto(
            window, total, Actions(AiActionTaken.Replied), Actions(AiActionTaken.Escalated), Actions(AiActionTaken.NoActionNeeded),
            Rate(Actions(AiActionTaken.Escalated), total),
            confidence is null ? null : Math.Round(confidence.Value, 4),
            latency is null ? null : Math.Round(latency.Value, 1),
            promptTokens, completionTokens, buying, human, optOut,
            byModel.Select(m => new AiModelRow(m.Model, m.Count,
                m.Confidence is null ? null : Math.Round(m.Confidence.Value, 4),
                m.Latency is null ? null : Math.Round(m.Latency.Value, 1))).ToList(),
            daily.Select(d => new AiDailyRow(d.Date, d.Count, d.Escalated)).ToList());
    }
}
