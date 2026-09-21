using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Reports;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The four reports, over real SQLite so the GROUP BY translations are the real ones.
///
/// The recurring subject is the difference between zero and no data. A campaign that has sent nothing
/// has a delivery rate of "unknown", not 0%; a report that prints 0% tells the reader it failed rather
/// than that it has not run. Most of the null assertions below are that one rule.
/// </summary>
public sealed class ReportServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly ReportService _service;
    private long _phone = 9000000000;

    public ReportServiceTests()
    {
        _connection.Open();
        _db = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new StubTenant(TenantA), new AnonymousUser()) { StampTenantId = TenantA };
        _db.Database.EnsureCreated();
        _service = new ReportService(_db, new TestClock { UtcNow = Now });
    }

    // ── Window ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(9999, 365)]
    public async Task The_window_is_clamped_not_rejected(int asked, int expected)
    {
        var report = await _service.GetLeadFunnelAsync(asked);

        Assert.Equal(expected, report.Window.Days);
        Assert.Equal(Now, report.Window.To);
        Assert.Equal(Now.AddDays(-expected), report.Window.From);
    }

    // ── Campaign performance ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_campaign_reports_delivery_reads_and_responses_with_correct_rates()
    {
        var campaign = AddCampaign("Diwali", CampaignStatus.Running);
        var c1 = Enrol(campaign, contacted: true, responded: true);
        var c2 = Enrol(campaign, contacted: true);
        var c3 = Enrol(campaign, contacted: true, status: CampaignCustomerStatus.OptedOut);
        Enrol(campaign, contacted: false);   // enrolled, never messaged

        Send(c1, MessageStatus.Read, Now.AddDays(-2));
        Send(c2, MessageStatus.Delivered, Now.AddDays(-2));
        Send(c3, MessageStatus.Sent, Now.AddDays(-2));
        Send(c3, MessageStatus.Failed, Now.AddDays(-2));

        var row = (await _service.GetCampaignPerformanceAsync(30, null)).Campaigns.Single();

        Assert.Equal(4, row.Audience);
        Assert.Equal(3, row.Contacted);
        Assert.Equal(3, row.MessagesSent);     // Read + Delivered + Sent; the Failed one never left
        Assert.Equal(2, row.Delivered);        // Read counts as delivered
        Assert.Equal(1, row.Read);
        Assert.Equal(1, row.Failed);
        Assert.Equal(1, row.Responded);
        Assert.Equal(1, row.OptedOut);
        Assert.Equal(2.0 / 3, row.DeliveryRate!.Value, 3);
        Assert.Equal(0.5, row.ReadRate!.Value, 3);              // of what arrived, half were opened
        Assert.Equal(1.0 / 3, row.ResponseRate!.Value, 3);
        Assert.Equal(1.0 / 3, row.OptOutRate!.Value, 3);
    }

    [Fact]
    public async Task A_campaign_that_has_sent_nothing_has_null_rates_not_zero()
    {
        AddCampaign("Not yet", CampaignStatus.Scheduled);

        var row = (await _service.GetCampaignPerformanceAsync(30, null)).Campaigns.Single();

        Assert.Null(row.DeliveryRate);
        Assert.Null(row.ReadRate);
        Assert.Null(row.ResponseRate);
        Assert.Null(row.OptOutRate);
    }

    [Fact]
    public async Task Messages_outside_the_window_are_not_counted_but_the_audience_still_is()
    {
        var campaign = AddCampaign("Old", CampaignStatus.Running);
        var c = Enrol(campaign, contacted: true);
        Send(c, MessageStatus.Delivered, Now.AddDays(-5));
        Send(c, MessageStatus.Delivered, Now.AddDays(-90));

        var row = (await _service.GetCampaignPerformanceAsync(30, null)).Campaigns.Single();

        Assert.Equal(1, row.MessagesSent);
        Assert.Equal(1, row.Audience);
    }

    [Fact]
    public async Task Draft_campaigns_are_left_out_and_one_campaign_can_be_asked_for()
    {
        AddCampaign("Draft", CampaignStatus.Draft);
        var a = AddCampaign("A", CampaignStatus.Running);
        AddCampaign("B", CampaignStatus.Running);

        var all = await _service.GetCampaignPerformanceAsync(30, null);
        var one = await _service.GetCampaignPerformanceAsync(30, a.Id);

        Assert.Equal(new[] { "A", "B" }, all.Campaigns.Select(c => c.Name));
        Assert.Equal("A", one.Campaigns.Single().Name);
    }

    [Fact]
    public async Task Inbound_messages_are_never_counted_as_campaign_sends()
    {
        var campaign = AddCampaign("C", CampaignStatus.Running);
        var c = Enrol(campaign, contacted: true);
        Send(c, MessageStatus.Delivered, Now.AddDays(-1), MessageDirection.Inbound);

        Assert.Equal(0, (await _service.GetCampaignPerformanceAsync(30, null)).Campaigns.Single().MessagesSent);
    }

    [Fact]
    public async Task Totals_are_the_sum_of_the_rows_with_rates_recomputed_not_averaged()
    {
        var big = AddCampaign("Big", CampaignStatus.Running);
        var small = AddCampaign("Small", CampaignStatus.Running);
        for (var i = 0; i < 9; i++) Send(Enrol(big, true), MessageStatus.Delivered, Now.AddDays(-1));
        Send(Enrol(small, true), MessageStatus.Sent, Now.AddDays(-1));

        var report = await _service.GetCampaignPerformanceAsync(30, null);

        // 9 of 10 delivered overall. Averaging the two campaigns' rates (100% and 0%) would say 50%,
        // which is wrong by a wide margin and is the classic way a totals row lies.
        Assert.Equal(10, report.Totals.MessagesSent);
        Assert.Equal(0.9, report.Totals.DeliveryRate!.Value, 3);
    }

    [Fact]
    public async Task Another_tenants_campaigns_never_appear()
    {
        AddCampaign("Mine", CampaignStatus.Running);
        using var other = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options, new StubTenant(TenantB), new AnonymousUser()) { StampTenantId = TenantB };
        other.Campaigns.Add(new Campaign { TenantId = TenantB, Name = "Theirs", Status = CampaignStatus.Running, CreatedBy = Guid.NewGuid() });
        other.SaveChanges();

        Assert.Equal(new[] { "Mine" }, (await _service.GetCampaignPerformanceAsync(30, null)).Campaigns.Select(c => c.Name));
    }

    // ── Lead funnel ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_funnel_counts_by_stage_and_lists_every_stage_including_empty_ones()
    {
        AddLead(LeadStage.New); AddLead(LeadStage.New);
        AddLead(LeadStage.Qualified);
        AddLead(LeadStage.Won);
        AddLead(LeadStage.Lost);

        var report = await _service.GetLeadFunnelAsync(30);

        Assert.Equal(5, report.TotalLeads);
        Assert.Equal(Enum.GetNames<LeadStage>(), report.Stages.Select(s => s.Stage));
        Assert.Equal(2, report.Stages.Single(s => s.Stage == "New").Count);
        Assert.Equal(0, report.Stages.Single(s => s.Stage == "Negotiation").Count);   // listed, at zero
        Assert.Equal(0.4, report.Stages.Single(s => s.Stage == "New").Share!.Value, 3);
    }

    [Fact]
    public async Task Conversion_rates_follow_from_the_counts()
    {
        AddLead(LeadStage.New);
        AddLead(LeadStage.Qualified);
        AddLead(LeadStage.Negotiation);
        AddLead(LeadStage.Won);

        var report = await _service.GetLeadFunnelAsync(30);

        Assert.Equal(0.75, report.QualifiedRate!.Value, 3);   // Qualified + Negotiation + Won
        Assert.Equal(0.25, report.WonRate!.Value, 3);
        Assert.Equal(0.0, report.LostRate!.Value, 3);
    }

    [Fact]
    public async Task Only_leads_created_in_the_window_are_counted()
    {
        AddLead(LeadStage.Won, createdAt: Now.AddDays(-5));
        AddLead(LeadStage.Won, createdAt: Now.AddDays(-100));

        Assert.Equal(1, (await _service.GetLeadFunnelAsync(30)).TotalLeads);
    }

    [Fact]
    public async Task Hot_campaign_and_organic_leads_are_split_out()
    {
        var campaign = AddCampaign("C", CampaignStatus.Running);
        AddLead(LeadStage.New, campaignId: campaign.Id, hot: true);
        AddLead(LeadStage.New);

        var report = await _service.GetLeadFunnelAsync(30);

        Assert.Equal(1, report.HotLeads);
        Assert.Equal(1, report.FromCampaigns);
        Assert.Equal(1, report.Organic);
    }

    [Fact]
    public async Task An_empty_funnel_has_null_rates_and_says_it_is_a_snapshot()
    {
        var report = await _service.GetLeadFunnelAsync(30);

        Assert.Equal(0, report.TotalLeads);
        Assert.Null(report.WonRate);
        Assert.Null(report.QualifiedRate);
        Assert.Contains("snapshot", report.Note);
    }

    // ── Human agent performance ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_agent_report_combines_leads_and_handoffs_and_resolves_names()
    {
        var priya = AddUser("Priya Sharma");
        AddLead(LeadStage.Won, assignedTo: priya);
        AddLead(LeadStage.Won, assignedTo: priya);
        AddLead(LeadStage.Lost, assignedTo: priya);
        AddLead(LeadStage.New, assignedTo: priya);

        AddHandoff(priya, HandoffStatus.Resolved, assignedAt: Now.AddDays(-3), resolvedAt: Now.AddDays(-3).AddMinutes(30));
        AddHandoff(priya, HandoffStatus.Resolved, assignedAt: Now.AddDays(-2), resolvedAt: Now.AddDays(-2).AddMinutes(90));
        AddHandoff(priya, HandoffStatus.InProgress, assignedAt: Now.AddDays(-1));

        var row = (await _service.GetAgentPerformanceAsync(30)).Agents.Single();

        Assert.Equal("Priya Sharma", row.Name);
        Assert.Equal(4, row.LeadsAssigned);
        Assert.Equal(2, row.LeadsWon);
        Assert.Equal(1, row.LeadsLost);
        Assert.Equal(0.5, row.WinRate!.Value, 3);
        Assert.Equal(3, row.HandoffsAssigned);
        Assert.Equal(2, row.HandoffsResolved);
        Assert.Equal(1, row.HandoffsOpen);
        Assert.Equal(60.0, row.AverageResolutionMinutes!.Value, 1);   // (30 + 90) / 2
    }

    [Fact]
    public async Task An_agent_with_no_resolved_handoffs_has_a_null_resolution_time_not_zero()
    {
        var agent = AddUser("New Hire");
        AddHandoff(agent, HandoffStatus.Assigned, assignedAt: Now.AddDays(-1));

        Assert.Null((await _service.GetAgentPerformanceAsync(30)).Agents.Single().AverageResolutionMinutes);
    }

    [Fact]
    public async Task An_open_handoff_from_before_the_window_still_shows_as_open()
    {
        // The open column is a live queue: a handoff assigned two months ago and never resolved is
        // exactly what it exists to surface.
        var agent = AddUser("Slow Queue");
        AddHandoff(agent, HandoffStatus.InProgress, assignedAt: Now.AddDays(-60));

        var row = (await _service.GetAgentPerformanceAsync(30)).Agents.Single();

        Assert.Equal(1, row.HandoffsOpen);
        Assert.Equal(0, row.HandoffsAssigned);   // assigned outside the window
    }

    [Fact]
    public async Task A_deleted_user_is_reported_as_unknown_rather_than_dropped()
    {
        AddLead(LeadStage.Won, assignedTo: Guid.NewGuid());

        Assert.Equal("Unknown user", (await _service.GetAgentPerformanceAsync(30)).Agents.Single().Name);
    }

    [Fact]
    public async Task The_busiest_agent_is_listed_first()
    {
        var quiet = AddUser("Quiet");
        var busy = AddUser("Busy");
        AddLead(LeadStage.New, assignedTo: quiet);
        AddLead(LeadStage.New, assignedTo: busy); AddLead(LeadStage.New, assignedTo: busy); AddLead(LeadStage.New, assignedTo: busy);

        Assert.Equal(new[] { "Busy", "Quiet" }, (await _service.GetAgentPerformanceAsync(30)).Agents.Select(a => a.Name));
    }

    // ── AI performance ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_ai_report_counts_outcomes_and_computes_the_escalation_rate()
    {
        var conversation = AddConversation();
        AddInteraction(conversation, AiActionTaken.Replied, 0.9, 400, model: "gpt-5-nano", prompt: 100, completion: 20);
        AddInteraction(conversation, AiActionTaken.Replied, 0.7, 600, model: "gpt-5-nano", prompt: 200, completion: 40, buying: true);
        AddInteraction(conversation, AiActionTaken.Escalated, 0.3, 500, model: "claude", human: true);
        AddInteraction(conversation, AiActionTaken.NoActionNeeded, 0.8, 300, model: "claude", optOut: true);

        var report = await _service.GetAiPerformanceAsync(30);

        Assert.Equal(4, report.Interactions);
        Assert.Equal(2, report.Replied);
        Assert.Equal(1, report.Escalated);
        Assert.Equal(1, report.NoActionNeeded);
        Assert.Equal(0.25, report.EscalationRate!.Value, 3);
        Assert.Equal(0.675, report.AverageConfidence!.Value, 3);
        Assert.Equal(450.0, report.AverageLatencyMs!.Value, 1);
        Assert.Equal(300, report.PromptTokens);
        Assert.Equal(60, report.CompletionTokens);
        Assert.Equal(1, report.BuyingIntentReported);
        Assert.Equal(1, report.HumanRequestReported);
        Assert.Equal(1, report.OptOutReported);
    }

    [Fact]
    public async Task The_ai_report_breaks_down_by_model_busiest_first()
    {
        var conversation = AddConversation();
        AddInteraction(conversation, AiActionTaken.Replied, 0.9, 100, model: "a");
        AddInteraction(conversation, AiActionTaken.Replied, 0.5, 300, model: "b");
        AddInteraction(conversation, AiActionTaken.Replied, 0.7, 500, model: "b");

        var models = (await _service.GetAiPerformanceAsync(30)).ByModel;

        Assert.Equal(new[] { "b", "a" }, models.Select(m => m.Model));
        Assert.Equal(2, models[0].Interactions);
        Assert.Equal(0.6, models[0].AverageConfidence!.Value, 3);
        Assert.Equal(400.0, models[0].AverageLatencyMs!.Value, 1);
    }

    [Fact]
    public async Task The_ai_report_has_a_daily_series_with_escalations()
    {
        var conversation = AddConversation();
        AddInteraction(conversation, AiActionTaken.Replied, 0.9, 100, at: Now.AddDays(-2));
        AddInteraction(conversation, AiActionTaken.Escalated, 0.3, 100, at: Now.AddDays(-2));
        AddInteraction(conversation, AiActionTaken.Replied, 0.9, 100, at: Now.AddDays(-1));

        var daily = (await _service.GetAiPerformanceAsync(30)).Daily;

        Assert.Equal(2, daily.Count);
        Assert.True(daily[0].Date < daily[1].Date);
        Assert.Equal(2, daily[0].Interactions);
        Assert.Equal(1, daily[0].Escalated);
    }

    [Fact]
    public async Task With_no_interactions_the_averages_are_null_and_nothing_throws()
    {
        var report = await _service.GetAiPerformanceAsync(30);

        Assert.Equal(0, report.Interactions);
        Assert.Null(report.EscalationRate);
        Assert.Null(report.AverageConfidence);
        Assert.Null(report.AverageLatencyMs);
        Assert.Equal(0, report.PromptTokens);
        Assert.Empty(report.ByModel);
        Assert.Empty(report.Daily);
    }

    [Fact]
    public async Task Interactions_with_no_token_counts_do_not_break_the_sum()
    {
        // Prompt and completion tokens are nullable - a provider that does not report them is legal.
        AddInteraction(AddConversation(), AiActionTaken.Replied, 0.9, 100, prompt: null, completion: null);

        var report = await _service.GetAiPerformanceAsync(30);

        Assert.Equal(0, report.PromptTokens);
    }

    // ── Seeding helpers ──────────────────────────────────────────────────────────────────

    private Customer AddCustomer()
    {
        var customer = new Customer { TenantId = TenantA, PhoneNumberE164 = "+91" + (_phone++) };
        _db.Customers.Add(customer);
        _db.SaveChanges();
        return customer;
    }

    private Campaign AddCampaign(string name, CampaignStatus status)
    {
        var campaign = new Campaign { TenantId = TenantA, Name = name, Status = status, CreatedBy = Guid.NewGuid() };
        _db.Campaigns.Add(campaign);
        _db.SaveChanges();
        return campaign;
    }

    private CampaignCustomer Enrol(Campaign campaign, bool contacted, bool responded = false, CampaignCustomerStatus status = CampaignCustomerStatus.Pending)
    {
        var enrolled = new CampaignCustomer
        {
            TenantId = TenantA, CampaignId = campaign.Id, CustomerId = AddCustomer().Id, Status = status,
            LastMessageSentAt = contacted ? Now.AddDays(-3) : null,
            LastCustomerResponseAt = responded ? Now.AddDays(-2) : null
        };
        _db.CampaignCustomers.Add(enrolled);
        _db.SaveChanges();
        return enrolled;
    }

    private void Send(CampaignCustomer enrolled, MessageStatus status, DateTime at, MessageDirection direction = MessageDirection.Outbound)
    {
        _db.Messages.Add(new Message
        {
            TenantId = TenantA, CustomerId = enrolled.CustomerId, CampaignCustomerId = enrolled.Id, Direction = direction,
            Status = status, IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAt = at
        });
        _db.SaveChanges();
    }

    private Lead AddLead(LeadStage stage, DateTime? createdAt = null, Guid? assignedTo = null, Guid? campaignId = null, bool hot = false)
    {
        var lead = new Lead
        {
            TenantId = TenantA, CustomerId = AddCustomer().Id, Stage = stage, AssignedTo = assignedTo, CampaignId = campaignId,
            HotLeadDetectedAt = hot ? Now : null, CreatedAt = createdAt ?? Now.AddDays(-1)
        };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
    }

    private Guid AddUser(string name)
    {
        var id = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser { Id = id, TenantId = TenantA, FullName = name, UserName = name.Replace(" ", ""), Email = name.Replace(" ", "") + "@x.com" });
        _db.SaveChanges();
        return id;
    }

    private Conversation AddConversation()
    {
        var conversation = new Conversation { TenantId = TenantA, CustomerId = AddCustomer().Id };
        _db.Conversations.Add(conversation);
        _db.SaveChanges();
        return conversation;
    }

    private void AddHandoff(Guid agent, HandoffStatus status, DateTime assignedAt, DateTime? resolvedAt = null)
    {
        var conversation = AddConversation();
        _db.HumanHandoffs.Add(new HumanHandoff
        {
            TenantId = TenantA, ConversationId = conversation.Id, Status = status, AssignedAgentId = agent,
            AssignedAt = assignedAt, ResolvedAt = resolvedAt
        });
        _db.SaveChanges();
    }

    private void AddInteraction(
        Conversation conversation, AiActionTaken action, double confidence, int latency, string model = "m",
        int? prompt = 0, int? completion = 0, bool buying = false, bool human = false, bool optOut = false, DateTime? at = null)
    {
        var inbound = new Message
        {
            TenantId = TenantA, CustomerId = conversation.CustomerId, ConversationId = conversation.Id,
            Direction = MessageDirection.Inbound, IdempotencyKey = Guid.NewGuid().ToString("N")
        };
        _db.Messages.Add(inbound);
        _db.SaveChanges();

        _db.AiInteractions.Add(new AiInteraction
        {
            TenantId = TenantA, ConversationId = conversation.Id, InboundMessageId = inbound.Id, ActionTaken = action,
            ConfidenceScore = confidence, LatencyMs = latency, ModelUsed = model, PromptTokens = prompt, CompletionTokens = completion,
            BuyingIntentReported = buying, HumanRequestReported = human, OptOutReported = optOut, CreatedAt = at ?? Now.AddDays(-1)
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class StubTenant : ITenantContext
    {
        public StubTenant(Guid? tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
