using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Application.Leads.FollowUps;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// What the three conversation modes do with one inbound message, and the two guarantees around the
/// question the agent asks: it never asks what the customer just answered, and it knows what the CRM
/// already holds.
/// </summary>
public sealed class ConversationOrchestratorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new();
    private readonly AiOptions _options = new() { EscalationIntents = new[] { "Complaint", "HumanRequest", "Negotiation", "Support" } };

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();

    private readonly ScriptedAi _ai = new();
    private readonly List<string> _sent = new();
    private bool _hot;
    private bool _serve;
    private readonly List<RetrievedChunk> _retrieved = new();
    private readonly List<(Guid LeadId, string? Reason, int? Months)> _suggested = new();

    public ConversationOrchestratorTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new NoUser()) { StampTenantId = _tenant };
        var tick = 0;
        _db.BeforeSave = db =>
        {
            // Production stamps CreatedAt on save; this test database does not, and "the previous turn" needs the turns in order.
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added && e.Entity.CreatedAt == default))
                entry.Entity.CreatedAt = DateTime.UtcNow.AddSeconds(++tick);
        };
        _db.Database.EnsureCreated();

        _db.Customers.Add(new Customer { Id = _customerId, TenantId = _tenant, FirstName = "Asha", PhoneNumberE164 = "+919000000001" });
        _db.Conversations.Add(new Conversation { Id = _conversationId, TenantId = _tenant, CustomerId = _customerId, Mode = ConversationMode.AI });
        _db.Leads.Add(new Lead { Id = _leadId, TenantId = _tenant, CustomerId = _customerId });
        foreach (var seed in QualificationDefaults.Fields)
        {
            _db.QualificationFields.Add(new QualificationField
            {
                TenantId = _tenant, FieldKey = seed.FieldKey, DisplayName = seed.DisplayName, Question = seed.Question,
                DataType = seed.DataType, IsRequired = seed.IsRequired, Priority = seed.Priority, SortOrder = seed.SortOrder
            });
        }
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ConversationOrchestrator Orchestrator()
    {
        var config = Fake.Of<ITenantConfigOverrideProvider>((m, _) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetAiOptionsAsync) ? Task.FromResult(_options) : throw new NotImplementedException(m.Name));
        var knowledge = Fake.Of<IKnowledgeBaseService>((m, _) =>
            m.Name == nameof(IKnowledgeBaseService.RetrieveRelevantChunksAsync)
                ? Task.FromResult<IReadOnlyList<RetrievedChunk>>(_retrieved.ToList())
                : throw new NotImplementedException(m.Name));
        var leads = Fake.Of<ILeadService>((m, _) =>
            m.Name == nameof(ILeadService.GetOrCreateActiveLeadIdAsync) ? Task.FromResult(_leadId) : throw new NotImplementedException(m.Name));
        var whatsApp = Fake.Of<IWhatsAppService>((m, a) =>
        {
            if (m.Name != nameof(IWhatsAppService.SendTextMessageAsync))
                throw new NotImplementedException(m.Name);
            _sent.Add((string)a![1]!);
            return Task.FromResult(WhatsAppSendResult.Succeeded("wamid." + _sent.Count));
        });
        var notifications = Fake.Of<INotificationService>((_, _) => Task.CompletedTask);
        var aiUsage = Fake.Of<IAiUsageService>((m, _) => m.Name switch
        {
            nameof(IAiUsageService.AuthorizeAsync) => Task.FromResult(new AiAuthorization(true, Guid.NewGuid(), AiDenialReason.None, null)),
            nameof(IAiUsageService.CompleteAsync) or nameof(IAiUsageService.FailAsync) => Task.CompletedTask,
            _ => throw new NotImplementedException(m.Name)
        });
        var scoring = Fake.Of<ILeadScoringService>((m, _) =>
            m.Name == nameof(ILeadScoringService.RecomputeAsync)
                ? Task.FromResult(new LeadScoreResult(_hot ? 80 : 10, _hot ? 80 : 10, _hot ? "Hot" : "Cold", _hot, _hot ? "asked to book" : null, Array.Empty<LeadScoreContributionDto>()))
                : throw new NotImplementedException(m.Name));

        var followUps = Fake.Of<ILeadFollowUpService>((m, a) =>
        {
            if (m.Name != nameof(ILeadFollowUpService.SuggestAsync))
                throw new NotImplementedException(m.Name);
            _suggested.Add(((Guid)a![0]!, (string?)a[1], (int?)a[2]));
            return Task.FromResult(true);
        });

        return new ConversationOrchestrator(
            _db, _clock, _ai, knowledge, leads,
            new HandoffService(_db, _clock, null!),
            new HandoffSummaryBuilder(_db, _clock, config),
            whatsApp, notifications, config, aiUsage,
            new QualificationPlanner(_db, config, NullLogger<QualificationPlanner>.Instance),
            scoring, new AiReplyValidator(), new CrmContextBuilder(_db), followUps,
            NullLogger<ConversationOrchestrator>.Instance, new ServingSwitch(() => _serve));
    }

    private async Task<Guid> ReceiveAsync(string text, ConversationMode mode)
    {
        var conversation = await _db.Conversations.SingleAsync();
        conversation.Mode = mode;
        var message = new Message
        {
            TenantId = _tenant, CustomerId = _customerId, ConversationId = _conversationId, Direction = MessageDirection.Inbound,
            MessageType = MessageType.Text, Text = text, IdempotencyKey = Guid.NewGuid().ToString()
        };
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        await Orchestrator().HandleInboundMessageAsync(_conversationId, _customerId, message.Id);
        return message.Id;
    }

    private static AiReplyResult Reply(
        string text, string intent = "PriceEnquiry", string? asked = null, bool buying = false,
        IReadOnlyList<AiExtractedField>? fields = null) => new(
        ResponseText: text, DetectedIntent: intent, ConfidenceScore: 0.9,
        ExtractedEntities: new AiExtractedEntities(null, null, null), UpdatedSummary: "Customer is asking about flats.",
        ModelUsed: "Test:fake", PromptTokens: 10, CompletionTokens: 5, LatencyMs: 1, CitedChunkIds: Array.Empty<Guid>(),
        ExtractedFields: fields, BuyingIntentDetected: buying, AskedFieldKey: asked);

    private static AiReplyResult CannotProceed(string text, string? reason, int? months, bool optOut = false) =>
        Reply(text, intent: "Interested") with { CannotProceedNow = true, CannotProceedReason = reason, FollowUpInMonths = months, OptOutRequested = optOut };

    // ── Interested but cannot go ahead ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_customer_who_cannot_proceed_now_gets_a_follow_up_suggested_and_a_normal_reply()
    {
        _ai.Replies.Enqueue(CannotProceed("No problem at all - whenever you are ready.", "Budget frozen until April", 2));

        await ReceiveAsync("We like it but the budget is frozen until April", ConversationMode.AI);

        Assert.Equal(new[] { (_leadId, (string?)"Budget frozen until April", (int?)2) }, _suggested);
        Assert.Equal(new[] { "No problem at all - whenever you are ready." }, _sent);
        Assert.Empty(await _db.HumanHandoffs.ToListAsync()); // not an escalation: nobody needs to jump in
    }

    [Fact]
    public async Task A_wait_outside_what_a_follow_up_can_be_is_clamped_before_it_is_suggested()
    {
        _ai.Replies.Enqueue(CannotProceed("Of course.", "After the move", 40));

        await ReceiveAsync("Ask me in about three years", ConversationMode.AI);

        Assert.Equal(12, _suggested.Single().Months);
    }

    [Fact]
    public async Task Someone_who_asks_us_to_stop_is_never_suggested_a_follow_up()
    {
        _ai.Replies.Enqueue(CannotProceed("Understood.", "Not now", 1, optOut: true));

        await ReceiveAsync("Not now, and please stop messaging me", ConversationMode.AI);

        Assert.Empty(_suggested);
    }

    [Fact]
    public async Task A_turn_that_does_not_report_it_suggests_nothing()
    {
        _ai.Replies.Enqueue(Reply("We have 2BHK flats in Baner and Wakad."));

        await ReceiveAsync("Do you have 2BHK flats?", ConversationMode.AI);

        Assert.Empty(_suggested);
    }

    // ── The three modes ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task In_AI_mode_a_safe_reply_is_sent_by_the_AI()
    {
        _ai.Replies.Enqueue(Reply("We have 2BHK flats in Baner and Wakad."));

        await ReceiveAsync("Do you have 2BHK flats?", ConversationMode.AI);

        Assert.Equal(new[] { "We have 2BHK flats in Baner and Wakad." }, _sent);
        Assert.Equal(AiActionTaken.Replied, (await _db.AiInteractions.SingleAsync()).ActionTaken);
    }

    [Fact]
    public async Task In_AI_mode_a_sensitive_moment_escalates_without_a_draft_being_kept_for_the_agent()
    {
        _hot = true;
        _ai.Replies.Enqueue(Reply("Happy to help with that.", buying: true));

        await ReceiveAsync("Can I book this week?", ConversationMode.AI);

        Assert.Empty(_sent);
        Assert.Equal(AiActionTaken.Escalated, (await _db.AiInteractions.SingleAsync()).ActionTaken);
        Assert.Single(await _db.HumanHandoffs.ToListAsync());
    }

    [Fact]
    public async Task In_Hybrid_mode_a_plain_question_is_still_answered_directly()
    {
        _ai.Replies.Enqueue(Reply("We have 2BHK flats in Baner and Wakad."));

        await ReceiveAsync("Do you have 2BHK flats?", ConversationMode.Hybrid);

        Assert.Single(_sent);
        Assert.Equal(AiActionTaken.Replied, (await _db.AiInteractions.SingleAsync()).ActionTaken);
        Assert.Empty(await _db.HumanHandoffs.ToListAsync());
    }

    [Fact]
    public async Task In_Hybrid_mode_a_buying_signal_holds_the_reply_as_a_draft_and_calls_in_the_agent()
    {
        _hot = true;
        _ai.Replies.Enqueue(Reply("Happy to look at that. Let me check what I can do for you.", buying: true));

        await ReceiveAsync("Can you give me a discount if I book this week?", ConversationMode.Hybrid);

        Assert.Empty(_sent);
        var turn = await _db.AiInteractions.SingleAsync();
        Assert.Equal(AiActionTaken.Drafted, turn.ActionTaken);
        Assert.Equal("Happy to look at that. Let me check what I can do for you.", turn.ProposedResponseText);

        var handoff = Assert.Single(await _db.HumanHandoffs.ToListAsync());
        Assert.Contains("drafted a reply", handoff.Notes);
        Assert.Equal(ConversationStatus.Escalated, (await _db.Conversations.SingleAsync()).Status);
    }

    [Fact]
    public async Task While_a_human_is_engaged_Hybrid_keeps_drafting_instead_of_sending()
    {
        _hot = true;
        _ai.Replies.Enqueue(Reply("Happy to look at that.", buying: true));
        await ReceiveAsync("Can I book this week?", ConversationMode.Hybrid);

        _hot = false;
        _ai.Replies.Enqueue(Reply("The site is open from morning to evening."));
        await ReceiveAsync("What are the site timings?", ConversationMode.Hybrid);

        Assert.Empty(_sent);
        Assert.Equal(2, await _db.AiInteractions.CountAsync(i => i.ActionTaken == AiActionTaken.Drafted));
        Assert.Single(await _db.HumanHandoffs.ToListAsync());
    }

    [Fact]
    public async Task A_draft_is_offered_to_the_agent_until_a_message_goes_out_after_it()
    {
        _hot = true;
        _ai.Replies.Enqueue(Reply("Happy to look at that.", buying: true));
        await ReceiveAsync("Can I book this week?", ConversationMode.Hybrid);
        var service = new ConversationService(_db, _clock, null!, null!, null!, null!, null!);

        Assert.Equal("Happy to look at that.", (await service.GetByIdAsync(_conversationId)).SuggestedReply);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        _db.Messages.Add(new Message
        {
            TenantId = _tenant, CustomerId = _customerId, ConversationId = _conversationId, Direction = MessageDirection.Outbound,
            MessageType = MessageType.Text, Text = "Hi Asha, this is Ravi.", IdempotencyKey = "agent-1"
        });
        await _db.SaveChangesAsync();

        Assert.Null((await service.GetByIdAsync(_conversationId)).SuggestedReply);
    }

    // ── Never asking what is already answered ────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_that_asks_for_what_the_customer_just_said_is_redone_knowing_it()
    {
        _ai.Replies.Enqueue(Reply(
            "Great. What is your budget?", asked: "budget",
            fields: new[] { new AiExtractedField("budget", "60 lakh", 0.95) }));
        _ai.Replies.Enqueue(Reply(
            "Great, 60 lakh noted. Are you buying soon or just exploring?", asked: "purchase_timeline",
            fields: new[] { new AiExtractedField("budget", "60 lakh", 0.95) }));

        await ReceiveAsync("Looking for a 2BHK, budget around 60 lakh", ConversationMode.AI);

        Assert.Equal(2, _ai.Contexts.Count);
        var retry = _ai.Contexts[1];
        Assert.Contains(retry.KnownFields, f => f.FieldKey == "budget" && f.RawValue == "60 lakh");
        Assert.DoesNotContain(retry.FieldsToAsk, f => f.FieldKey == "budget");
        Assert.Equal(new[] { "Great, 60 lakh noted. Are you buying soon or just exploring?" }, _sent);
    }

    [Fact]
    public async Task A_reply_that_asks_for_something_still_unknown_is_not_redone()
    {
        _ai.Replies.Enqueue(Reply("Sure. What is your budget?", asked: "budget"));

        await ReceiveAsync("Do you have 2BHK flats?", ConversationMode.AI);

        Assert.Single(_ai.Contexts);
        Assert.Equal(new[] { "Sure. What is your budget?" }, _sent);
    }

    // ── Using what the CRM already holds ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_AI_is_handed_the_CRM_context_before_it_is_asked_anything()
    {
        var tag = new CustomerTag { TenantId = _tenant, Name = "VIP" };
        tag.Customers.Add(await _db.Customers.SingleAsync());
        _db.CustomerTags.Add(tag);
        await _db.SaveChangesAsync();
        _ai.Replies.Enqueue(Reply("Welcome back, Asha."));

        await ReceiveAsync("Hi", ConversationMode.AI);

        var crm = _ai.Contexts.Single().Crm;
        Assert.NotNull(crm);
        Assert.Equal(new[] { "VIP" }, crm!.Tags);
    }

    // ── Published product knowledge served to chat (the tenant's switch) ──────────────────────────────

    private async Task<Guid> SeedChunkAsync(string text)
    {
        var article = new WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase.KnowledgeBaseArticle
        {
            TenantId = _tenant, ArticleKey = "pk-" + Guid.NewGuid().ToString("N"), Title = "Published product knowledge", Content = "x", ContentHash = "h",
            SourceType = KnowledgeSourceType.AdminConfiguredArticle, AuthorityRank = 30, SystemKey = ProductKnowledgeSystem.ArticleKey
        };
        _db.KnowledgeBaseArticles.Add(article);
        var chunk = new WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase.KnowledgeBaseChunk { TenantId = _tenant, ArticleId = article.Id, ChunkText = text, ContextHeader = "h", EmbeddingInput = text, SearchText = text };
        _db.KnowledgeBaseChunks.Add(chunk);
        await _db.SaveChangesAsync();
        return chunk.Id;
    }

    private async Task GroundedOnAsync(string text, bool material = false)
    {
        var chunkId = await SeedChunkAsync(text);
        _retrieved.Add(new RetrievedChunk(chunkId, Guid.NewGuid(), text, 0.9, KnowledgeItemId: Guid.NewGuid(), KnowledgeVersionId: Guid.NewGuid(), IsMaterialClaim: material));
    }

    [Fact]
    public async Task With_the_switch_off_the_chat_is_exactly_as_before_even_when_nothing_is_found()
    {
        _ai.Replies.Enqueue(Reply("Our fee is reasonable and our doctors are excellent."));

        await ReceiveAsync("What is the fee?", ConversationMode.AI);

        Assert.False(_ai.Contexts.Single().ProductKnowledgeServing);
        Assert.Equal(new[] { "Our fee is reasonable and our doctors are excellent." }, _sent); // sent as it always was
        Assert.Equal(AiActionTaken.Replied, (await _db.AiInteractions.SingleAsync()).ActionTaken);
        Assert.Empty(await _db.AiInteractionValidationFailures.ToListAsync());
    }

    [Fact]
    public async Task With_the_switch_on_and_nothing_approved_the_agent_asks_one_clarifying_question_and_the_miss_is_recorded()
    {
        _serve = true;
        _ai.Replies.Enqueue(Reply("Which treatment are you asking about?"));

        await ReceiveAsync("What is the fee?", ConversationMode.AI);

        Assert.True(_ai.Contexts.Single().ProductKnowledgeServing);
        Assert.Empty(_ai.Contexts.Single().GroundingChunks);
        Assert.Equal(new[] { "Which treatment are you asking about?" }, _sent);
        var turn = await _db.AiInteractions.SingleAsync();
        Assert.Equal(AiActionTaken.Replied, turn.ActionTaken);
        var miss = Assert.Single(await _db.AiInteractionValidationFailures.ToListAsync());
        Assert.Equal(("NoApprovedKnowledge", false), (miss.Code, miss.Blocking));
        Assert.Empty(await _db.HumanHandoffs.ToListAsync());
    }

    [Fact]
    public async Task With_the_switch_on_and_nothing_approved_an_answer_from_the_models_own_knowledge_is_not_sent_and_a_person_is_called_in()
    {
        _serve = true;
        _ai.Replies.Enqueue(Reply("Our fee is reasonable and our doctors are excellent."));

        await ReceiveAsync("What is the fee?", ConversationMode.AI);

        Assert.Empty(_sent);
        Assert.Equal(AiActionTaken.Escalated, (await _db.AiInteractions.SingleAsync()).ActionTaken);
        var handoff = Assert.Single(await _db.HumanHandoffs.ToListAsync());
        Assert.Equal(HandoffTriggerReason.CannotAnswer, handoff.TriggerReason);
        Assert.Contains(await _db.AiInteractionValidationFailures.ToListAsync(), f => f.Code == "NoApprovedKnowledge" && f.Blocking);
    }

    [Fact]
    public async Task A_second_miss_in_a_row_hands_the_conversation_to_a_person_instead_of_asking_again()
    {
        _serve = true;
        _ai.Replies.Enqueue(Reply("Which treatment are you asking about?"));
        await ReceiveAsync("What is the fee?", ConversationMode.AI);
        Assert.Single(_sent);

        _ai.Replies.Enqueue(Reply("And roughly when would you like to come?"));
        await ReceiveAsync("Something for my eyes", ConversationMode.AI);

        Assert.Single(_sent); // the second question was not sent
        var handoff = Assert.Single(await _db.HumanHandoffs.ToListAsync());
        Assert.Equal(HandoffTriggerReason.CannotAnswer, handoff.TriggerReason);
        Assert.Equal(AiActionTaken.Escalated, (await _db.AiInteractions.SingleAsync(i => i.ProposedResponseText == "And roughly when would you like to come?")).ActionTaken);
    }

    [Fact]
    public async Task A_turn_with_something_approved_in_between_resets_the_count_so_the_next_miss_asks_again()
    {
        _serve = true;
        _ai.Replies.Enqueue(Reply("Which treatment are you asking about?"));
        await ReceiveAsync("What is the fee?", ConversationMode.AI);

        await GroundedOnAsync("Parking is free for all patients.");
        _ai.Replies.Enqueue(Reply("Parking is free for all patients."));
        await ReceiveAsync("Is there parking?", ConversationMode.AI);

        _retrieved.Clear();
        _ai.Replies.Enqueue(Reply("Which department do you need?"));
        await ReceiveAsync("And something else", ConversationMode.AI);

        Assert.Equal(3, _sent.Count);
        Assert.Empty(await _db.HumanHandoffs.ToListAsync());
    }

    [Fact]
    public async Task With_the_switch_on_a_greeting_with_nothing_approved_is_answered_normally()
    {
        _serve = true;
        _ai.Replies.Enqueue(Reply("Hello! Welcome.", intent: "Qualification"));

        await ReceiveAsync("Hi", ConversationMode.AI);

        Assert.Equal(new[] { "Hello! Welcome." }, _sent);
        Assert.Empty(await _db.AiInteractionValidationFailures.ToListAsync());
    }

    [Fact]
    public async Task With_the_switch_on_an_answer_the_approved_knowledge_supports_is_sent_and_cites_the_item_and_version()
    {
        _serve = true;
        await GroundedOnAsync("The consultation fee is Rs 500 for the first visit.", material: true);
        var chunk = _retrieved.Single();
        _ai.Replies.Enqueue(Reply("The consultation fee is Rs 500 for the first visit.") with { CitedChunkIds = new[] { chunk.ChunkId } });

        await ReceiveAsync("What is the fee?", ConversationMode.AI);

        Assert.Equal(new[] { "The consultation fee is Rs 500 for the first visit." }, _sent);
        var snippet = _ai.Contexts.Single().GroundingChunks.Single();
        Assert.Equal((chunk.KnowledgeItemId, chunk.KnowledgeVersionId, true), (snippet.KnowledgeItemId, snippet.KnowledgeVersionId, snippet.IsMaterialClaim));
        var citation = await _db.AiInteractionSources.SingleAsync();
        Assert.Equal((chunk.ChunkId, chunk.KnowledgeItemId, chunk.KnowledgeVersionId), (citation.KnowledgeBaseChunkId, citation.KnowledgeItemId, citation.KnowledgeVersionId));
    }

    [Fact]
    public async Task With_the_switch_on_a_material_claim_the_approved_knowledge_does_not_state_is_not_sent()
    {
        _serve = true;
        await GroundedOnAsync("Parking is free for all patients.");
        _ai.Replies.Enqueue(Reply("Yes, and everything comes with a lifetime guarantee."));

        await ReceiveAsync("Is there parking and what is the warranty?", ConversationMode.AI);

        Assert.Empty(_sent);
        Assert.Equal(AiActionTaken.Escalated, (await _db.AiInteractions.SingleAsync()).ActionTaken);
        Assert.Contains(await _db.AiInteractionValidationFailures.ToListAsync(), f => f.Code == "UnbackedMaterialClaim" && f.Blocking);
        Assert.Equal(HandoffTriggerReason.CannotAnswer, Assert.Single(await _db.HumanHandoffs.ToListAsync()).TriggerReason);
    }

    [Fact]
    public async Task With_the_switch_off_the_same_unbacked_claim_is_sent_as_before()
    {
        await GroundedOnAsync("Parking is free for all patients.");
        _ai.Replies.Enqueue(Reply("Yes, and everything comes with a lifetime guarantee."));

        await ReceiveAsync("Is there parking and what is the warranty?", ConversationMode.AI);

        Assert.Single(_sent);
        Assert.Empty(await _db.AiInteractionValidationFailures.ToListAsync());
    }

    // ── Doubles ──────────────────────────────────────────────────────────────────────────────

    private sealed class ServingSwitch : WhatsAppSalesAutomation.Application.ProductKnowledge.IProductKnowledgeServing
    {
        private readonly Func<bool> _on;
        public ServingSwitch(Func<bool> on) => _on = on;
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(_on());
        public Task<bool> IsEnabledForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(_on());
    }

    private sealed class ScriptedAi : IAiService
    {
        public Queue<AiReplyResult> Replies { get; } = new();
        public List<AiConversationContext> Contexts { get; } = new();

        public Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(Replies.Dequeue());
        }
    }

    private sealed class AmbientTenant : ITenantContext
    {
        private readonly Guid _tenantId;
        public AmbientTenant(Guid tenantId) => _tenantId = tenantId;
        public Guid? TenantId => _tenantId;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
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
