using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Pins the briefing an agent gets when the AI steps back.
///
/// Two of these matter more than the rest. <see cref="Breakdown_includes_this_turns_uncommitted_points"/>
/// covers the reason <c>HandoffTurnContext</c> carries score lines at all: at escalation the current
/// turn's contributions are still on the change tracker, so a builder left to query for them would omit
/// exactly the points that caused the escalation. <see cref="A_value_the_model_was_unsure_of_is_not_presented_as_known"/>
/// covers the confidence floor: a guess must not reach an agent looking like something the customer said.
/// </summary>
public sealed class HandoffSummaryBuilderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly HandoffSummaryBuilder _builder;
    private readonly TestClock _clock = new() { UtcNow = Now };
    private readonly AiOptions _aiOptions = new();

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();

    public HandoffSummaryBuilderTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        var config = Fake.Of<ITenantConfigOverrideProvider>((m, _) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetAiOptionsAsync)
                ? Task.FromResult(_aiOptions)
                : throw new NotImplementedException(m.Name));

        _builder = new HandoffSummaryBuilder(_db, _clock, config);

        _db.Customers.Add(new Customer
        {
            Id = _customerId,
            TenantId = _tenant,
            FirstName = "Ramesh",
            LastName = "Gupta",
            PhoneNumberE164 = "+919000000000"
        });

        _db.Conversations.Add(new Conversation
        {
            Id = _conversationId,
            TenantId = _tenant,
            CustomerId = _customerId,
            Summary = "Wants a 3BHK in Mohali, ready to visit this weekend.",
            CreatedAt = Now.AddHours(-2)
        });

        _db.Leads.Add(new Lead { Id = _leadId, TenantId = _tenant, CustomerId = _customerId });

        foreach (var seed in QualificationDefaults.Fields)
        {
            _db.QualificationFields.Add(new QualificationField
            {
                TenantId = _tenant,
                FieldKey = seed.FieldKey,
                DisplayName = seed.DisplayName,
                Question = seed.Question,
                DataType = seed.DataType,
                IsRequired = seed.IsRequired,
                Priority = seed.Priority,
                ScoreWeight = seed.ScoreWeight,
                SortOrder = seed.SortOrder
            });
        }

        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── What the agent is told ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Briefing_carries_the_customer_the_requirement_and_the_transcript_size()
    {
        AddMessage(MessageDirection.Inbound, "Do you have anything in Sector 82?", Now.AddMinutes(-10));
        AddMessage(MessageDirection.Outbound, "Yes, two units are available.", Now.AddMinutes(-9));
        AddMessage(MessageDirection.Inbound, "Can I speak to someone?", Now.AddMinutes(-1));
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        Assert.Equal("Ramesh Gupta", summary.CustomerName);
        Assert.Equal("+919000000000", summary.CustomerPhoneNumberE164);
        Assert.Equal("Wants a 3BHK in Mohali, ready to visit this weekend.", summary.Requirement);
        Assert.Equal(3, summary.MessageCount);
        Assert.Equal("Can I speak to someone?", summary.LastCustomerMessage);
        Assert.Equal(Now.AddHours(-2), summary.ConversationStartedAt);
        Assert.Equal(Now, summary.EscalatedAt);
    }

    [Fact]
    public async Task Last_customer_message_ignores_what_the_agent_said_after_it()
    {
        AddMessage(MessageDirection.Inbound, "What is the price?", Now.AddMinutes(-5));
        AddMessage(MessageDirection.Outbound, "Around 85 lakh.", Now.AddMinutes(-4));
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        Assert.Equal("What is the price?", summary.LastCustomerMessage);
    }

    [Fact]
    public async Task A_very_long_message_is_cut_rather_than_shown_whole()
    {
        AddMessage(MessageDirection.Inbound, new string('x', 900), Now.AddMinutes(-1));
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        Assert.NotNull(summary.LastCustomerMessage);
        Assert.Equal(503, summary.LastCustomerMessage!.Length); // 500 + the ellipsis
        Assert.EndsWith("...", summary.LastCustomerMessage);
    }

    // ── Qualification ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Known_answers_and_the_gaps_between_them_are_both_reported()
    {
        Capture(QualificationDefaults.BudgetKey, "50 lakh");
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        var captured = Assert.Single(summary.Qualification);
        Assert.Equal("Budget", captured.DisplayName);
        Assert.Equal("50 lakh", captured.RawValue);

        // The gaps are the useful half: they tell whoever takes over what is left to cover.
        Assert.Equal(new[] { "Interest", "Purchase timeline" }, summary.StillUnknown.OrderBy(s => s).ToArray());
    }

    [Fact]
    public async Task A_value_the_model_was_unsure_of_is_not_presented_as_known()
    {
        Capture(QualificationDefaults.BudgetKey, "maybe 50 lakh?", confidence: 0.3);
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        // On record, but not presented to an agent as something the customer told us - and so still
        // listed as a gap, because it is one.
        Assert.Empty(summary.Qualification);
        Assert.Contains("Budget", summary.StillUnknown);
    }

    [Fact]
    public async Task A_superseded_answer_does_not_survive_the_one_that_replaced_it()
    {
        Capture(QualificationDefaults.BudgetKey, "30 lakh", superseded: true);
        Capture(QualificationDefaults.BudgetKey, "50 lakh");
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        var captured = Assert.Single(summary.Qualification);
        Assert.Equal("50 lakh", captured.RawValue);
    }

    [Fact]
    public async Task A_value_a_colleague_typed_is_marked_as_such()
    {
        Capture(QualificationDefaults.BudgetKey, "50 lakh", capturedByUserId: Guid.NewGuid());
        Capture(QualificationDefaults.InterestKey, "3BHK");
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        Assert.True(summary.Qualification.Single(q => q.DisplayName == "Budget").EnteredByHuman);
        Assert.False(summary.Qualification.Single(q => q.DisplayName == "Interest").EnteredByHuman);
    }

    // ── The score, with its workings ────────────────────────────────────────────────────────

    [Fact]
    public async Task Breakdown_includes_this_turns_uncommitted_points()
    {
        AddContribution("field:budget", "Budget", 30, Now.AddMinutes(-30));
        await _db.SaveChangesAsync();

        // The demo request that made this lead hot is staged, not saved - exactly the state the
        // orchestrator is in when it escalates.
        var summary = await _builder.BuildAsync(
            _leadId,
            _conversationId,
            Turn(newScoreLines: new[] { new HandoffScoreLine("Demo requested", 25) }));

        Assert.Equal(new[] { "Demo requested", "Budget" }, summary.ScoreBreakdown.Select(l => l.Label).ToArray());
        Assert.Equal(25, summary.ScoreBreakdown[0].Points);
    }

    [Fact]
    public async Task Breakdown_stops_at_twelve_lines_newest_first()
    {
        for (var i = 0; i < 20; i++)
            AddContribution($"rule:r{i}", $"Rule {i}", 1, Now.AddMinutes(-i));
        await _db.SaveChangesAsync();

        var summary = await _builder.BuildAsync(_leadId, _conversationId, Turn());

        Assert.Equal(12, summary.ScoreBreakdown.Count);
        Assert.Equal("Rule 0", summary.ScoreBreakdown[0].Label);
    }

    [Fact]
    public async Task The_turns_own_verdict_travels_verbatim()
    {
        var summary = await _builder.BuildAsync(
            _leadId,
            _conversationId,
            new HandoffTurnContext(
                HandoffTriggerReason.HotLead,
                nameof(CustomerIntent.DemoRequest),
                "Customer wants a site visit on Saturday.",
                BlockedReason: "ResponseTooLong",
                ScoreNumeric: 85,
                ScoreBand: "Hot",
                IsHot: true,
                HotReason: "Asked to book a visit",
                NewScoreLines: Array.Empty<HandoffScoreLine>()));

        Assert.Equal("HotLead", summary.TriggerReason);
        Assert.Equal(nameof(CustomerIntent.DemoRequest), summary.DetectedIntent);
        Assert.Equal("Customer wants a site visit on Saturday.", summary.AgentNote);
        Assert.Equal("ResponseTooLong", summary.BlockedReason);
        Assert.Equal(85, summary.ScoreNumeric);
        Assert.Equal("Hot", summary.Temperature);
        Assert.True(summary.IsHot);
        Assert.Equal("Asked to book a visit", summary.HotReason);
    }

    // ── Defensive ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_conversation_that_cannot_be_read_still_produces_a_briefing()
    {
        // Not a normal code path - the orchestrator just loaded this conversation. But a briefing that
        // throws would take down the escalation itself, which is the one thing that must still happen.
        var summary = await _builder.BuildAsync(_leadId, Guid.NewGuid(), Turn());

        Assert.Equal("Unknown customer", summary.CustomerName);
        Assert.Equal(string.Empty, summary.CustomerPhoneNumberE164);
        Assert.Null(summary.Requirement);
        Assert.Equal(0, summary.MessageCount);
        Assert.Equal(Now, summary.ConversationStartedAt);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static HandoffTurnContext Turn(IReadOnlyList<HandoffScoreLine>? newScoreLines = null) =>
        new(
            HandoffTriggerReason.LowConfidence,
            nameof(CustomerIntent.Information),
            AgentNote: null,
            BlockedReason: null,
            ScoreNumeric: 40,
            ScoreBand: "Warm",
            IsHot: false,
            HotReason: null,
            newScoreLines ?? Array.Empty<HandoffScoreLine>());

    private void AddMessage(MessageDirection direction, string text, DateTime createdAt) =>
        _db.Messages.Add(new Message
        {
            TenantId = _tenant,
            CustomerId = _customerId,
            ConversationId = _conversationId,
            Direction = direction,
            MessageType = MessageType.Text,
            Text = text,
            IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = createdAt
        });

    private void Capture(
        string fieldKey,
        string raw,
        double confidence = 1.0,
        bool superseded = false,
        Guid? capturedByUserId = null)
    {
        var field = _db.QualificationFields.Single(f => f.FieldKey == fieldKey);

        _db.LeadQualificationValues.Add(new LeadQualificationValue
        {
            TenantId = _tenant,
            LeadId = _leadId,
            FieldId = field.Id,
            FieldKey = fieldKey,
            RawValue = raw,
            ExtractionConfidence = confidence,
            IsSuperseded = superseded,
            CapturedByUserId = capturedByUserId,
            CreatedAt = Now
        });
    }

    private void AddContribution(string sourceKey, string displayName, int points, DateTime appliedAt) =>
        _db.LeadScoreContributions.Add(new LeadScoreContribution
        {
            TenantId = _tenant,
            LeadId = _leadId,
            SourceKey = sourceKey,
            DisplayName = displayName,
            Points = points,
            AppliedAt = appliedAt
        });

    private sealed class AmbientTenant : ITenantContext
    {
        public AmbientTenant(Guid tenantId) => TenantId = tenantId;

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
