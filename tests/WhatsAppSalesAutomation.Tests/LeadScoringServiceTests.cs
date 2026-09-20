using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Pins the scoring rules that replaced <c>LeadService.ComputeScoreNumeric</c>'s hardcoded heuristic.
///
/// The headline case is <see cref="Seeded_field_weights_reproduce_the_old_heuristic"/>: on the seeded
/// configuration the field weights must still total 30 + 30 + 20, because an upgraded tenant's leads
/// must not quietly re-rank themselves the day this ships.
/// </summary>
public sealed class LeadScoringServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly LeadScoringService _service;
    private readonly TestClock _clock = new() { UtcNow = Now };
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();
    private readonly AiOptions _aiOptions = new();

    public LeadScoringServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        var config = Fake.Of<ITenantConfigOverrideProvider>((m, _) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetAiOptionsAsync)
                ? Task.FromResult(_aiOptions)
                : throw new NotImplementedException(m.Name));

        _service = new LeadScoringService(_db, _clock, config);

        var customerId = Guid.NewGuid();
        _db.Customers.Add(new Customer { Id = customerId, TenantId = _tenant, PhoneNumberE164 = "+919000000000" });
        _db.Leads.Add(new Lead { Id = _leadId, TenantId = _tenant, CustomerId = customerId });

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

        foreach (var seed in QualificationDefaults.ScoringRules)
        {
            _db.LeadScoringRules.Add(new LeadScoringRule
            {
                TenantId = _tenant,
                RuleKey = seed.RuleKey,
                DisplayName = seed.DisplayName,
                RuleType = seed.RuleType,
                MatchValue = seed.MatchValue,
                Points = seed.Points,
                OncePerLead = seed.OncePerLead,
                MarksLeadHot = seed.MarksLeadHot,
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

    // ── The equivalence that lets this ship without re-ranking anyone's pipeline ──────────────

    [Fact]
    public async Task Seeded_field_weights_reproduce_the_old_heuristic()
    {
        Capture(QualificationDefaults.BudgetKey, "50 lakh");
        Capture(QualificationDefaults.InterestKey, "3BHK in Mohali");
        Capture(QualificationDefaults.PurchaseTimelineKey, "within 3 months");
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals());

        Assert.Equal(80, result.ScoreNumeric);   // 30 + 30 + 20, exactly as before
        Assert.Equal("Hot", result.Band);
    }

    [Theory]
    [InlineData(new[] { QualificationDefaults.BudgetKey }, 30, "Cold")]
    [InlineData(new[] { QualificationDefaults.BudgetKey, QualificationDefaults.InterestKey }, 60, "Warm")]
    [InlineData(new[] { QualificationDefaults.PurchaseTimelineKey }, 20, "Cold")]
    public async Task Partial_capture_scores_and_bands_as_before(string[] keys, int expected, string band)
    {
        foreach (var key in keys)
            Capture(key, "something");
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals());

        Assert.Equal(expected, result.ScoreNumeric);
        Assert.Equal(band, result.Band);
    }

    // ── Accumulation: the one deliberate departure from the old heuristic ────────────────────

    [Fact]
    public async Task Intent_bonus_is_earned_once_and_then_persists()
    {
        var first = await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.Negotiation)));
        await _db.SaveChangesAsync();

        Assert.Equal(20, first.ScoreNumeric);

        // The old heuristic recomputed from scratch, so a following turn about anything else dropped
        // this back to zero. Accumulating is the intended behaviour change: a signal, once earned,
        // stays earned.
        var second = await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.Information)));
        await _db.SaveChangesAsync();

        Assert.Equal(20, second.ScoreNumeric);
        Assert.Empty(second.NewContributions);
    }

    [Fact]
    public async Task Once_per_lead_rule_does_not_fire_twice_on_a_repeated_intent()
    {
        await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.Negotiation)));
        await _db.SaveChangesAsync();

        var again = await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.Negotiation)));
        await _db.SaveChangesAsync();

        Assert.Equal(20, again.ScoreNumeric);
        Assert.Empty(again.NewContributions);
    }

    [Fact]
    public async Task Repeatable_rule_fires_every_turn_it_matches()
    {
        _db.LeadScoringRules.Add(new LeadScoringRule
        {
            TenantId = _tenant,
            RuleKey = "asked_price",
            DisplayName = "Asked about price",
            RuleType = LeadScoringRuleType.IntentMatch,
            MatchValue = nameof(CustomerIntent.PriceEnquiry),
            Points = 10,
            OncePerLead = false
        });
        await _db.SaveChangesAsync();

        var signals = new ScoringSignals(DetectedIntent: nameof(CustomerIntent.PriceEnquiry));

        await _service.RecomputeAsync(_leadId, signals);
        await _db.SaveChangesAsync();

        var second = await _service.RecomputeAsync(_leadId, signals);
        await _db.SaveChangesAsync();

        Assert.Equal(20, second.ScoreNumeric);
    }

    // ── Confidence gate ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Low_confidence_value_is_on_record_but_earns_nothing()
    {
        Capture(QualificationDefaults.BudgetKey, "maybe 50 lakh?", confidence: 0.4);
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals());

        Assert.Equal(0, result.ScoreNumeric);
        Assert.Single(await _db.LeadQualificationValues.Where(v => v.LeadId == _leadId).ToListAsync());
    }

    [Fact]
    public async Task Backfilled_value_earns_its_weight_the_first_time_scoring_runs()
    {
        // What the startup backfill writes: confidence 1.0, no message, no user - captured long before
        // any scoring ran. It must still be worth its weight, or an upgraded tenant's existing leads
        // would all read zero.
        Capture(QualificationDefaults.BudgetKey, "50 lakh");
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals());

        Assert.Equal(30, result.ScoreNumeric);
    }

    // ── Clamping ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Score_is_clamped_but_the_raw_total_is_still_reported()
    {
        for (var i = 0; i < 4; i++)
        {
            _db.LeadScoringRules.Add(new LeadScoringRule
            {
                TenantId = _tenant,
                RuleKey = $"keyword_{i}",
                DisplayName = $"Keyword {i}",
                RuleType = LeadScoringRuleType.MessageKeyword,
                MatchValue = $"word{i}",
                Points = 50
            });
        }
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(
            _leadId, new ScoringSignals(InboundMessageText: "word0 word1 word2 word3"));

        Assert.Equal(100, result.ScoreNumeric);
        Assert.Equal(200, result.RawTotal);
    }

    [Fact]
    public async Task Negative_rules_cannot_push_the_score_below_zero()
    {
        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.Complaint)));

        Assert.Equal(0, result.ScoreNumeric);
        Assert.Equal(-20, result.RawTotal);
    }

    // ── Hot lead ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Demo_request_makes_a_lead_hot_even_with_nothing_else_known()
    {
        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.DemoRequest)));
        await _db.SaveChangesAsync();

        Assert.True(result.IsHot);
        Assert.Equal(0, result.ScoreNumeric);   // hot is not the same as high-scoring

        var lead = await _db.Leads.SingleAsync(l => l.Id == _leadId);
        Assert.Equal(Now, lead.HotLeadDetectedAt);
    }

    [Fact]
    public async Task Hot_flag_is_not_re_stamped_or_cleared_by_a_later_cooler_turn()
    {
        await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.DemoRequest)));
        await _db.SaveChangesAsync();

        _clock.UtcNow = Now.AddHours(3);

        var later = await _service.RecomputeAsync(_leadId, new ScoringSignals(DetectedIntent: nameof(CustomerIntent.Information)));
        await _db.SaveChangesAsync();

        Assert.True(later.IsHot);

        var lead = await _db.Leads.SingleAsync(l => l.Id == _leadId);
        Assert.Equal(Now, lead.HotLeadDetectedAt);   // the original moment, not the latest one
    }

    [Fact]
    public async Task A_rule_flagged_hot_names_itself_as_the_reason()
    {
        _db.LeadScoringRules.Add(new LeadScoringRule
        {
            TenantId = _tenant,
            RuleKey = "asked_to_pay",
            DisplayName = "Asked how to pay",
            RuleType = LeadScoringRuleType.MessageKeyword,
            MatchValue = "payment",
            Points = 10,
            MarksLeadHot = true
        });
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(
            _leadId, new ScoringSignals(InboundMessageText: "how do I make the payment?"));

        Assert.True(result.IsHot);
        Assert.Equal("Asked how to pay", result.HotReason);
    }

    // ── Rule matching ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Keyword_rules_match_whole_words_only()
    {
        _db.LeadScoringRules.Add(new LeadScoringRule
        {
            TenantId = _tenant,
            RuleKey = "buy",
            DisplayName = "Said buy",
            RuleType = LeadScoringRuleType.MessageKeyword,
            MatchValue = "buy",
            Points = 15
        });
        await _db.SaveChangesAsync();

        // "buying" contains "buy" but is not the word - a substring match here would fire on half the
        // messages a sales agent ever receives.
        var near = await _service.RecomputeAsync(_leadId, new ScoringSignals(InboundMessageText: "just buying time"));
        Assert.Equal(0, near.ScoreNumeric);

        var exact = await _service.RecomputeAsync(_leadId, new ScoringSignals(InboundMessageText: "I want to buy it"));
        Assert.Equal(15, exact.ScoreNumeric);
    }

    [Fact]
    public async Task Field_value_rule_matches_on_the_normalized_form()
    {
        _db.LeadScoringRules.Add(new LeadScoringRule
        {
            TenantId = _tenant,
            RuleKey = "big_budget",
            DisplayName = "Budget of a crore",
            RuleType = LeadScoringRuleType.FieldValueMatch,
            MatchValue = $"{QualificationDefaults.BudgetKey}=10000000",
            Points = 25
        });

        // The customer said "1 cr"; the rule is written in digits. Normalization is what lets those
        // two meet.
        Capture(QualificationDefaults.BudgetKey, "1 cr", normalized: "10000000");
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals());

        Assert.Equal(55, result.ScoreNumeric);   // 30 field weight + 25 rule
    }

    [Fact]
    public async Task Timeline_rule_ignores_a_value_that_never_became_a_date()
    {
        _db.LeadScoringRules.Add(new LeadScoringRule
        {
            TenantId = _tenant,
            RuleKey = "soon",
            DisplayName = "Buying within 30 days",
            RuleType = LeadScoringRuleType.TimelineWithinDays,
            MatchValue = "30",
            Points = 30
        });

        // "soon" is a real answer but not a comparable one - turning it into a day count would be
        // inventing the precision this rule tests for.
        Capture(QualificationDefaults.PurchaseTimelineKey, "soon", normalized: null);
        await _db.SaveChangesAsync();

        var vague = await _service.RecomputeAsync(_leadId, new ScoringSignals());
        Assert.Equal(20, vague.ScoreNumeric);   // field weight only

        Supersede(QualificationDefaults.PurchaseTimelineKey);
        Capture(QualificationDefaults.PurchaseTimelineKey, "next month", normalized: Now.AddDays(20).ToString("yyyy-MM-dd"));
        await _db.SaveChangesAsync();

        var dated = await _service.RecomputeAsync(_leadId, new ScoringSignals());
        Assert.Equal(50, dated.ScoreNumeric);
    }

    // ── The collision the SourceKey prefix exists to prevent ─────────────────────────────────

    [Fact]
    public async Task A_rule_named_after_a_field_still_scores_separately()
    {
        _db.LeadScoringRules.Add(new LeadScoringRule
        {
            TenantId = _tenant,
            RuleKey = QualificationDefaults.BudgetKey,   // deliberately the same name as the field
            DisplayName = "Budget mentioned",
            RuleType = LeadScoringRuleType.FieldPresent,
            MatchValue = QualificationDefaults.BudgetKey,
            Points = 15
        });

        Capture(QualificationDefaults.BudgetKey, "50 lakh");
        await _db.SaveChangesAsync();

        var result = await _service.RecomputeAsync(_leadId, new ScoringSignals());

        // 30 from the field weight and 15 from the rule. Without the field:/rule: prefixes these two
        // would share a SourceKey and the once-per-lead index would swallow one of them.
        Assert.Equal(45, result.ScoreNumeric);
        Assert.Equal(2, result.NewContributions.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private void Capture(string fieldKey, string raw, string? normalized = null, double confidence = 1.0)
    {
        var field = _db.QualificationFields.Single(f => f.FieldKey == fieldKey);

        _db.LeadQualificationValues.Add(new LeadQualificationValue
        {
            TenantId = _tenant,
            LeadId = _leadId,
            FieldId = field.Id,
            FieldKey = fieldKey,
            RawValue = raw,
            NormalizedValue = normalized,
            ExtractionConfidence = confidence
        });
    }

    private void Supersede(string fieldKey)
    {
        foreach (var value in _db.LeadQualificationValues.Where(v => v.LeadId == _leadId && v.FieldKey == fieldKey && !v.IsSuperseded))
            value.IsSuperseded = true;
    }

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
