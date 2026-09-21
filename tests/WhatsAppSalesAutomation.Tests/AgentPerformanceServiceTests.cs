using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Pins the four questions the report exists to answer.
///
/// The one that earns its keep is <see cref="A_value_volunteered_before_the_question_does_not_credit_the_question"/>.
/// Ask effectiveness is the number a tenant would rewrite a question over, and counting a value the
/// customer offered unprompted as proof the question works would send them rewriting the wrong one.
/// </summary>
public sealed class AgentPerformanceServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly AgentPerformanceService _service;
    private readonly TestClock _clock = new() { UtcNow = Now };

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();

    public AgentPerformanceServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        _service = new AgentPerformanceService(_db, _clock);

        _db.Customers.Add(new Customer { Id = _customerId, TenantId = _tenant, PhoneNumberE164 = "+919000000000" });

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

    // ── Turns ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Containment_is_replies_over_all_turns()
    {
        var conversation = AddConversation();
        AddTurn(conversation, AiActionTaken.Replied, confidence: 0.9, minutesAgo: 60);
        AddTurn(conversation, AiActionTaken.Replied, confidence: 0.8, minutesAgo: 50);
        AddTurn(conversation, AiActionTaken.Escalated, confidence: 0.3, minutesAgo: 40);
        await _db.SaveChangesAsync();

        var report = await _service.GetReportAsync(30);

        Assert.Equal(3, report.Turns.TotalTurns);
        Assert.Equal(2, report.Turns.RepliedTurns);
        Assert.Equal(1, report.Turns.EscalatedTurns);
        Assert.Equal(0.6667, report.Turns.ContainmentRate, 4);
        Assert.Equal(0.667, report.Turns.AverageConfidence, 3);
    }

    [Fact]
    public async Task Turns_outside_the_window_are_not_counted()
    {
        var conversation = AddConversation();
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60);
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60 * 24 * 45);
        await _db.SaveChangesAsync();

        Assert.Equal(1, (await _service.GetReportAsync(30)).Turns.TotalTurns);
        Assert.Equal(2, (await _service.GetReportAsync(60)).Turns.TotalTurns);
    }

    [Fact]
    public async Task An_empty_window_reports_zeroes_rather_than_failing()
    {
        var report = await _service.GetReportAsync(30);

        Assert.Equal(0, report.Turns.TotalTurns);
        Assert.Equal(0d, report.Turns.ContainmentRate);
        Assert.Empty(report.ValidationFailures);
        // The fields are still listed, with nothing against them - "never asked" is an answer.
        Assert.Equal(3, report.Fields.Count);
        Assert.All(report.Fields, f => Assert.Equal(0, f.ConversationsAsked));
    }

    // ── Field effectiveness: the point of the whole report ──────────────────────────────────

    [Fact]
    public async Task A_question_that_is_asked_and_answered_reads_as_effective()
    {
        var conversation = AddConversation();
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60, asked: QualificationDefaults.BudgetKey);
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 50, captured: new[] { QualificationDefaults.BudgetKey });
        await _db.SaveChangesAsync();

        var budget = Field(await _service.GetReportAsync(30), QualificationDefaults.BudgetKey);

        Assert.Equal(1, budget.TimesAsked);
        Assert.Equal(1, budget.ConversationsAsked);
        Assert.Equal(1, budget.ConversationsCaptured);
        Assert.Equal(1, budget.ConversationsCapturedAfterAsk);
        Assert.Equal(1d, budget.AskEffectiveness);
        Assert.Equal(2d, budget.AverageTurnsToCapture);
    }

    [Fact]
    public async Task A_question_asked_repeatedly_and_never_answered_reads_as_the_problem_it_is()
    {
        var a = AddConversation();
        var b = AddConversation();
        foreach (var conversation in new[] { a, b })
        {
            AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60, asked: QualificationDefaults.BudgetKey);
            AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 50, asked: QualificationDefaults.BudgetKey);
            AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 40, asked: QualificationDefaults.BudgetKey);
        }
        await _db.SaveChangesAsync();

        var budget = Field(await _service.GetReportAsync(30), QualificationDefaults.BudgetKey);

        Assert.Equal(6, budget.TimesAsked);
        Assert.Equal(2, budget.ConversationsAsked);
        Assert.Equal(0, budget.ConversationsCapturedAfterAsk);
        Assert.Equal(0d, budget.AskEffectiveness);
        Assert.Null(budget.AverageTurnsToCapture);
    }

    [Fact]
    public async Task A_value_volunteered_before_the_question_does_not_credit_the_question()
    {
        var conversation = AddConversation();
        // The customer led with their budget; the agent asked anyway two turns later.
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60, captured: new[] { QualificationDefaults.BudgetKey });
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 50, asked: QualificationDefaults.BudgetKey);
        await _db.SaveChangesAsync();

        var budget = Field(await _service.GetReportAsync(30), QualificationDefaults.BudgetKey);

        Assert.Equal(1, budget.ConversationsCaptured);
        Assert.Equal(1, budget.ConversationsAsked);
        // The value arrived first, so the question proved nothing.
        Assert.Equal(0, budget.ConversationsCapturedAfterAsk);
        Assert.Equal(0d, budget.AskEffectiveness);
    }

    [Fact]
    public async Task Restating_an_answer_does_not_count_as_a_second_capture()
    {
        var conversation = AddConversation();
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60, asked: QualificationDefaults.BudgetKey);
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 50, captured: new[] { QualificationDefaults.BudgetKey });
        AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 40, captured: new[] { QualificationDefaults.BudgetKey });
        await _db.SaveChangesAsync();

        var budget = Field(await _service.GetReportAsync(30), QualificationDefaults.BudgetKey);

        Assert.Equal(1, budget.ConversationsCaptured);
        Assert.Equal(2d, budget.AverageTurnsToCapture); // the first capture, not the repeat
    }

    [Fact]
    public async Task Unreadable_captured_json_is_read_as_no_capture_rather_than_throwing()
    {
        var conversation = AddConversation();
        var turn = AddTurn(conversation, AiActionTaken.Replied, minutesAgo: 60);
        turn.CapturedFieldKeysJson = "{not json";
        await _db.SaveChangesAsync();

        var report = await _service.GetReportAsync(30);

        Assert.Equal(1, report.Turns.TotalTurns);
        Assert.All(report.Fields, f => Assert.Equal(0, f.ConversationsCaptured));
    }

    // ── Validation failures ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Failures_are_grouped_by_code_with_a_rate_against_the_windows_turns()
    {
        var a = AddConversation();
        var b = AddConversation();
        var t1 = AddTurn(a, AiActionTaken.Escalated, minutesAgo: 60);
        var t2 = AddTurn(a, AiActionTaken.Replied, minutesAgo: 50);
        var t3 = AddTurn(b, AiActionTaken.Replied, minutesAgo: 40);
        var t4 = AddTurn(b, AiActionTaken.Replied, minutesAgo: 30);

        AddFailure(t1, "UngroundedNumber", blocking: true);
        AddFailure(t2, "UngroundedNumber", blocking: true);
        AddFailure(t3, "UngroundedNumber", blocking: true);
        AddFailure(t4, "UnknownField", blocking: false);
        await _db.SaveChangesAsync();

        var report = await _service.GetReportAsync(30);

        var ungrounded = report.ValidationFailures[0];
        Assert.Equal("UngroundedNumber", ungrounded.Code);
        Assert.True(ungrounded.Blocking);
        Assert.Equal(3, ungrounded.Occurrences);
        // Two of the three were the same conversation - one conversation with a problem, not two.
        Assert.Equal(2, ungrounded.AffectedConversations);
        Assert.Equal(0.75d, ungrounded.ShareOfTurns);

        Assert.Equal("UnknownField", report.ValidationFailures[1].Code);
        Assert.False(report.ValidationFailures[1].Blocking);
    }

    [Fact]
    public async Task Blocked_replies_count_turns_not_failures()
    {
        var conversation = AddConversation();
        var turn = AddTurn(conversation, AiActionTaken.Escalated, minutesAgo: 60);

        // One turn, three blocking checks - the customer lost one answer, not three.
        AddFailure(turn, "EmptyResponse", blocking: true);
        AddFailure(turn, "InternalTermLeak", blocking: true);
        AddFailure(turn, "UnknownField", blocking: false);
        await _db.SaveChangesAsync();

        Assert.Equal(1, (await _service.GetReportAsync(30)).Turns.BlockedReplies);
    }

    // ── Lead outcomes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hot_conversion_is_compared_against_everyone_else()
    {
        AddLead(LeadScoreBand.Hot, LeadStage.Won, hot: true);
        AddLead(LeadScoreBand.Hot, LeadStage.Won, hot: true);
        AddLead(LeadScoreBand.Hot, LeadStage.Lost, hot: true);
        AddLead(LeadScoreBand.Hot, LeadStage.Qualifying, hot: true);
        AddLead(LeadScoreBand.Warm, LeadStage.Won, hot: false);
        AddLead(LeadScoreBand.Cold, LeadStage.Lost, hot: false);
        AddLead(LeadScoreBand.Cold, LeadStage.New, hot: false);
        AddLead(LeadScoreBand.Cold, LeadStage.New, hot: false);
        await _db.SaveChangesAsync();

        var leads = (await _service.GetReportAsync(30)).Leads;

        Assert.Equal(8, leads.TotalLeads);
        Assert.Equal(4, leads.Hot);
        Assert.Equal(1, leads.Warm);
        Assert.Equal(3, leads.Cold);
        Assert.Equal(4, leads.HotLeadsDetected);
        Assert.Equal(2, leads.HotLeadsWon);
        Assert.Equal(0.5d, leads.HotConversionRate);
        Assert.Equal(1, leads.OtherLeadsWon);
        Assert.Equal(0.25d, leads.OtherConversionRate);
    }

    [Fact]
    public async Task A_lead_that_was_hot_once_counts_as_hot_even_if_its_band_has_moved()
    {
        // The stamp is permanent by design; the band is current state. The report says so.
        AddLead(LeadScoreBand.Cold, LeadStage.Won, hot: true);
        await _db.SaveChangesAsync();

        var leads = (await _service.GetReportAsync(30)).Leads;

        Assert.Equal(1, leads.Cold);
        Assert.Equal(1, leads.HotLeadsDetected);
        Assert.Equal(1d, leads.HotConversionRate);
    }

    // ── Window ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 30)]     // nonsense falls back to the default
    [InlineData(-5, 30)]
    [InlineData(7, 7)]
    [InlineData(365, 90)]   // clamped
    public async Task The_window_is_clamped(int requested, int expectedDays)
    {
        var report = await _service.GetReportAsync(requested);

        Assert.Equal(Now, report.ToUtc);
        Assert.Equal(Now.AddDays(-expectedDays), report.FromUtc);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static QualificationFieldStatsDto Field(AgentPerformanceReportDto report, string fieldKey) =>
        report.Fields.Single(f => f.FieldKey == fieldKey);

    private Guid AddConversation()
    {
        var id = Guid.NewGuid();
        _db.Conversations.Add(new Conversation
        {
            Id = id,
            TenantId = _tenant,
            CustomerId = _customerId,
            CreatedAt = Now.AddDays(-1)
        });
        return id;
    }

    private AiInteraction AddTurn(
        Guid conversationId,
        AiActionTaken action,
        int minutesAgo,
        double confidence = 0.8,
        string? asked = null,
        string[]? captured = null)
    {
        var message = new Message
        {
            TenantId = _tenant,
            CustomerId = _customerId,
            ConversationId = conversationId,
            Direction = MessageDirection.Inbound,
            MessageType = MessageType.Text,
            Text = "hi",
            IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = Now.AddMinutes(-minutesAgo)
        };
        _db.Messages.Add(message);

        var interaction = new AiInteraction
        {
            TenantId = _tenant,
            ConversationId = conversationId,
            InboundMessageId = message.Id,
            ConfidenceScore = confidence,
            ActionTaken = action,
            ModelUsed = "Simulated:rule-based",
            LatencyMs = 250,
            PromptTokens = 100,
            CompletionTokens = 40,
            AskedFieldKey = asked,
            CapturedFieldKeysJson = captured is null ? null : JsonSerializer.Serialize(captured),
            CreatedAt = Now.AddMinutes(-minutesAgo)
        };
        _db.AiInteractions.Add(interaction);

        return interaction;
    }

    private void AddFailure(AiInteraction interaction, string code, bool blocking) =>
        _db.AiInteractionValidationFailures.Add(new AiInteractionValidationFailure
        {
            TenantId = _tenant,
            AiInteractionId = interaction.Id,
            Code = code,
            Blocking = blocking,
            CreatedAt = interaction.CreatedAt
        });

    private void AddLead(LeadScoreBand band, LeadStage stage, bool hot) =>
        _db.Leads.Add(new Lead
        {
            TenantId = _tenant,
            CustomerId = _customerId,
            Score = band,
            Stage = stage,
            HotLeadDetectedAt = hot ? Now.AddDays(-2) : null,
            CreatedAt = Now.AddDays(-3)
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
