using System.Reflection;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Phases 1 and 2 of the AI Content Studio with a database: the questionnaires every industry starts with, saving and resuming, the audit
/// trail of answers, and the knowledge built from them - facts with provenance and history, readiness, the follow-up workflow, claim checks
/// and retrieval for the AI side. Each tenant's data stays its own throughout.
/// </summary>
public sealed class StudioKnowledgeServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = Now;
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class NoAudit : IPlatformAuditService
    {
        public Task LogAsync(Guid actorUserId, string actorEmail, string action, Guid? targetTenantId = null, Guid? targetUserId = null, string? details = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PagedResult<PlatformAuditLogEntryDto>> GetPagedAsync(PlatformAuditLogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StudioConfig
    {
        public bool Enabled { get; set; } = true;

        public string ReadinessRules { get; set; } = string.Empty;

        public HashSet<Guid> Off { get; } = new();

        public ITenantConfigOverrideProvider Provider => Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = Enabled && !Off.Contains((Guid)args![0]!), ReadinessRules = ReadinessRules })
                : throw new NotImplementedException(m.Name));
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly StudioConfig _studio = new();
    private readonly QuestionnaireTemplateService _templates;
    private readonly QuestionnaireService _questionnaire;
    private readonly BusinessFactService _facts;
    private readonly KnowledgeService _knowledge;

    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _software = Guid.NewGuid();
    private readonly Guid _retail = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();

    public StudioKnowledgeServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();

        // The production interceptor stamps CreatedAt; here a rising stamp keeps "newest first" deterministic, even within one save.
        var tick = 0;
        _db.BeforeSave = db =>
        {
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added))
                entry.Entity.CreatedAt = Now.AddMilliseconds(++tick);
        };

        _db.Tenants.Add(new Tenant
        {
            Id = _clinic, Name = "Sunrise Eye Clinic", Slug = "sunrise", Industry = "Healthcare", BusinessDescription = "An eye clinic in Pune.",
            TargetAudience = "Adults in Pune.", TargetLocation = "Pune", Status = TenantStatus.Active
        });
        _db.Tenants.Add(new Tenant { Id = _software, Name = "Flowdesk", Slug = "flowdesk", Industry = "Technology & software", Status = TenantStatus.Active });
        _db.Tenants.Add(new Tenant { Id = _retail, Name = "Corner Store", Slug = "corner", Industry = "Retail & e-commerce", Status = TenantStatus.Active });
        _db.SaveChanges();

        _templates = new QuestionnaireTemplateService(_db, _clock, new NoAudit());
        _questionnaire = new QuestionnaireService(_db, _studio.Provider, _clock);
        _facts = new BusinessFactService(_db, _studio.Provider, _clock);
        _knowledge = new KnowledgeService(_db, _studio.Provider, _clock);

        SeedStarterTemplates();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void SeedStarterTemplates()
    {
        var services = new ServiceCollection().AddSingleton<ApplicationDbContext>(_db).BuildServiceProvider();
        QuestionnaireTemplateSeeder.SeedAsync(services).GetAwaiter().GetResult();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SaveQuestionnaireAnswersRequest Save(params (string Key, string Json)[] answers) =>
        new(answers.ToDictionary(a => a.Key, a => J(a.Json)));

    private static string S(string text) => JsonSerializer.Serialize(text);

    private async Task<QuestionnaireStateDto> AnswerGenericAsync(Guid tenant, Guid? actor = null, params (string Key, string Json)[] extra)
    {
        var answers = new List<(string, string)>
        {
            ("business_name", S("Sunrise Eye Clinic")),
            ("industry", S("Healthcare")),
            ("business_description", S("We are an eye clinic in Pune.")),
            ("target_audience", S("Working adults aged 30 to 60.")),
            ("products_services", S("Groceries and household items.")),
            ("service_locations", S("Pune")),
            ("content_language", """["english","hindi"]""")
        };
        answers.AddRange(extra);
        return await _questionnaire.SaveAnswersAsync(tenant, Save(answers.ToArray()), actor);
    }

    private IQueryable<BusinessFact> AllFacts(Guid tenant) => _db.BusinessFacts.IgnoreQueryFilters().Where(f => f.TenantId == tenant);

    // ===== Phase 1: the questionnaires ===========================================================================================================

    [Fact]
    public void Every_starter_questionnaire_is_a_valid_definition_with_examples_and_groups()
    {
        Assert.Equal(4, StarterTemplates.All().Count);
        foreach (var starter in StarterTemplates.All())
        {
            var json = QuestionnaireEngine.Serialize(starter.Definition);
            var (definition, issues) = QuestionnaireEngine.TryParse(json);

            Assert.True(issues.Count == 0, $"{starter.Key}: {string.Join("; ", issues.Select(i => $"{i.Key}: {i.Message}"))}");
            Assert.All(definition!.Questions, q => Assert.False(string.IsNullOrWhiteSpace(q.Group), $"{starter.Key}.{q.Key} has no group"));
            Assert.NotEmpty(definition.Questions.Where(q => !string.IsNullOrWhiteSpace(q.Example)));
            Assert.Contains(definition.Questions, q => q.PrefillFrom == "Name");
        }
    }

    [Fact]
    public void The_business_profile_is_collected_by_every_starter_questionnaire()
    {
        var wanted = new[] { "business_name", "industry", "business_description", "target_audience", "products_services", "service_locations", "brand_tone", "brand_identity", "content_language" };

        foreach (var starter in StarterTemplates.All())
            Assert.All(wanted, key => Assert.Contains(starter.Definition.Questions, q => q.Key == key));
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_never_touches_a_template_the_platform_has_since_edited()
    {
        var generic = await _db.QuestionnaireTemplates.SingleAsync(t => t.Key == "generic-business");
        generic.Title = "Our own title";
        generic.IsActive = false;
        await _db.SaveChangesAsync();

        SeedStarterTemplates();

        Assert.Equal(4, await _db.QuestionnaireTemplates.CountAsync());
        Assert.Equal(4, await _db.QuestionnaireTemplateVersions.CountAsync(v => v.Status == QuestionnaireVersionStatus.Published));
        var after = await _db.QuestionnaireTemplates.SingleAsync(t => t.Key == "generic-business");
        Assert.Equal("Our own title", after.Title);
        Assert.False(after.IsActive);
    }

    [Fact]
    public async Task Different_industries_are_asked_different_relevant_questions()
    {
        var clinic = await _questionnaire.GetStateAsync(_clinic);
        var software = await _questionnaire.GetStateAsync(_software);
        var retail = await _questionnaire.GetStateAsync(_retail);

        Assert.Equal("Healthcare questionnaire", clinic.TemplateTitle);
        Assert.Equal("Software questionnaire", software.TemplateTitle);
        Assert.Equal("Business questionnaire", retail.TemplateTitle); // no template for its industry, so the generic one

        var clinicKeys = clinic.Definition!.Questions.Select(q => q.Key).ToHashSet();
        var softwareKeys = software.Definition!.Questions.Select(q => q.Key).ToHashSet();
        var retailKeys = retail.Definition!.Questions.Select(q => q.Key).ToHashSet();

        Assert.Contains("appointment_booking", clinicKeys); // consultation and appointments
        Assert.Contains("eye_services", clinicKeys);
        Assert.Contains("key_features", softwareKeys);     // features, workflows, integrations, pricing
        Assert.Contains("integrations", softwareKeys);
        Assert.Contains("pricing_model", softwareKeys);
        Assert.DoesNotContain("appointment_booking", softwareKeys);
        Assert.DoesNotContain("key_features", clinicKeys);
        Assert.DoesNotContain("appointment_booking", retailKeys);
        Assert.DoesNotContain("key_features", retailKeys);
    }

    [Fact]
    public async Task A_manufacturer_is_asked_about_specifications_applications_and_delivery()
    {
        _db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "SteelWorks", Slug = "steelworks", Industry = "manufacturing", Status = TenantStatus.Active });
        await _db.SaveChangesAsync();
        var tenant = (await _db.Tenants.SingleAsync(t => t.Slug == "steelworks")).Id;

        var keys = (await _questionnaire.GetStateAsync(tenant)).Definition!.Questions.Select(q => q.Key).ToHashSet();

        Assert.Contains("product_specs", keys);
        Assert.Contains("applications", keys);
        Assert.Contains("lead_time", keys);
    }

    [Fact]
    public async Task Services_chosen_earlier_decide_which_follow_up_questions_are_asked()
    {
        var state = await _questionnaire.GetStateAsync(_clinic);
        Assert.DoesNotContain("eye_services", state.VisibleKeys);
        Assert.DoesNotContain("consultation_fee", state.VisibleKeys);

        state = await _questionnaire.SaveAnswersAsync(_clinic, Save(("products_services", """["eye_care"]""")));
        Assert.Contains("eye_services", state.VisibleKeys);
        Assert.Contains("consultation_fee", state.VisibleKeys);
        Assert.DoesNotContain("cataract_details", state.VisibleKeys);

        state = await _questionnaire.SaveAnswersAsync(_clinic, Save(("eye_services", """["cataract"]""")));
        Assert.Contains("cataract_details", state.VisibleKeys);

        // Taking eye care out again removes the questions that followed from it, and their answers.
        state = await _questionnaire.SaveAnswersAsync(_clinic, Save(("products_services", """["dental"]""")));
        Assert.DoesNotContain("eye_services", state.VisibleKeys);
        Assert.DoesNotContain("cataract_details", state.VisibleKeys);
        Assert.False(state.Answers.ContainsKey("eye_services"));
    }

    [Fact]
    public async Task The_business_profile_starts_the_questionnaire_off_so_nobody_is_asked_twice()
    {
        var state = await _questionnaire.GetStateAsync(_clinic);

        Assert.Equal("Sunrise Eye Clinic", state.Answers["business_name"].GetString());
        Assert.Equal("Healthcare", state.Answers["industry"].GetString());
        Assert.Equal("An eye clinic in Pune.", state.Answers["business_description"].GetString());
        Assert.Equal("Adults in Pune.", state.Answers["target_audience"].GetString());
        Assert.Equal("Pune", state.Answers["service_locations"].GetString());
    }

    [Fact]
    public async Task A_tenant_can_start_save_part_way_resume_later_and_complete()
    {
        await _questionnaire.GetStateAsync(_software);
        await _questionnaire.SaveAnswersAsync(_software, Save(("business_name", S("Flowdesk"))));

        // "Later": a new request, a new service, the same database.
        var resumed = await new QuestionnaireService(_db, _studio.Provider, _clock).GetStateAsync(_software);
        Assert.Equal(QuestionnaireResponseStatus.InProgress, resumed.Status);
        Assert.Equal("Flowdesk", resumed.Answers["business_name"].GetString());

        await _questionnaire.SaveAnswersAsync(_software, Save(
            ("industry", S("Technology & software")), ("business_description", S("Shared inbox for sales teams.")), ("target_audience", S("Small sales teams.")),
            ("products_services", """["saas"]"""), ("service_locations", S("Online across India")), ("content_language", """["english"]"""),
            ("key_features", S("Shared inbox and reports."))));
        var done = await _questionnaire.CompleteAsync(_software, _admin);

        Assert.Equal(QuestionnaireResponseStatus.Completed, done.State.Status);
        Assert.True(done.FactsCreated >= 8);
    }

    [Fact]
    public async Task Completing_names_every_mandatory_question_still_unanswered()
    {
        await _questionnaire.GetStateAsync(_software);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => _questionnaire.CompleteAsync(_software, _admin));

        var missing = ex.Errors.Select(e => e.PropertyName).ToHashSet();
        Assert.Contains("business_description", missing);
        Assert.Contains("products_services", missing);
        Assert.Contains("key_features", missing);
        Assert.DoesNotContain("brand_tone", missing); // optional questions never block
    }

    [Fact]
    public async Task I_dont_know_does_not_block_progress_unless_the_question_requires_a_real_answer()
    {
        await _questionnaire.GetStateAsync(_retail);
        const string unknown = """{ "$special": "unknown" }""";

        await _questionnaire.SaveAnswersAsync(_retail, Save(
            ("business_name", S("Corner Store")), ("industry", S("Retail & e-commerce")), ("business_description", unknown), ("target_audience", """{ "$special": "need_help" }"""),
            ("products_services", unknown), ("service_locations", """{ "$special": "not_applicable" }"""), ("content_language", unknown)));
        var done = await _questionnaire.CompleteAsync(_retail, _admin);

        Assert.Equal(QuestionnaireResponseStatus.Completed, done.State.Status);
        var refs = await AllFacts(_retail).Select(f => f.SourceRef).ToListAsync();
        Assert.Contains("business_name", refs);
        Assert.Contains("industry", refs);
        Assert.DoesNotContain("business_description", refs); // "I don't know" states nothing, so it makes no fact

        // The business name is the one question that insists on a real answer.
        await _questionnaire.RestartAsync(_retail);
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _questionnaire.SaveAnswersAsync(_retail, Save(("business_name", unknown))));
        Assert.Contains(ex.Errors, e => e.PropertyName == "business_name");
    }

    [Fact]
    public async Task A_special_answer_makes_no_fact_but_shows_up_as_a_gap_with_its_reason()
    {
        await _questionnaire.GetStateAsync(_retail);
        await _questionnaire.SaveAnswersAsync(_retail, Save(
            ("business_name", S("Corner Store")), ("industry", S("Retail & e-commerce")), ("business_description", S("A grocery store.")),
            ("target_audience", """{ "$special": "unknown" }"""), ("products_services", S("Groceries.")), ("service_locations", S("Pune")), ("content_language", """["english"]""")));
        await _questionnaire.CompleteAsync(_retail, _admin);

        Assert.DoesNotContain(await AllFacts(_retail).ToListAsync(), f => f.SourceRef == "target_audience");

        var readiness = await _knowledge.GetReadinessAsync(_retail);
        Assert.Equal(KnowledgeGapReason.Unknown, readiness.Gaps.Single(g => g.Key == "audience").Reason);
    }

    [Fact]
    public async Task A_secret_in_an_answer_is_refused_and_never_stored()
    {
        await _questionnaire.GetStateAsync(_software);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _questionnaire.SaveAnswersAsync(_software, Save(("business_description", S("Our admin password: hunter2hunter2")))));

        Assert.Contains(ex.Errors, e => e.PropertyName == "business_description");
        Assert.DoesNotContain("hunter2", (await _db.QuestionnaireResponses.IgnoreQueryFilters().SingleAsync(r => r.TenantId == _software)).AnswersJson);
        Assert.Empty(await _db.QuestionnaireAnswerRevisions.IgnoreQueryFilters().Where(r => r.TenantId == _software).ToListAsync());
    }

    // ----- the audit trail of answers ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_answer_change_is_recorded_with_who_made_it_and_what_it_was()
    {
        await _questionnaire.GetStateAsync(_software);
        // The business name starts off filled in from the profile, so the first edit is a change, not a first answer.
        await _questionnaire.SaveAnswersAsync(_software, Save(("business_name", S("Flowdesk Labs")), ("brand_identity", S("Blue and white"))), _admin);
        await _questionnaire.SaveAnswersAsync(_software, Save(("business_name", S("Flowdesk Ltd"))), _manager);
        await _questionnaire.SaveAnswersAsync(_software, Save(("business_name", "null")), _manager);

        var history = await _questionnaire.GetHistoryAsync(_software, "business_name", 10);

        Assert.Equal(new[] { "Cleared", "Changed", "Changed" }, history.Select(h => h.Kind));
        Assert.Equal("Flowdesk Ltd", history[0].Previous!.Value.GetString());
        Assert.Null(history[0].New);
        Assert.Equal("Flowdesk", history[2].Previous!.Value.GetString());
        Assert.Equal("Flowdesk Labs", history[2].New!.Value.GetString());
        Assert.Equal(new Guid?[] { _manager, _manager, _admin }, history.Select(h => h.ChangedBy));

        var first = Assert.Single(await _questionnaire.GetHistoryAsync(_software, "brand_identity", 10));
        Assert.Equal("Set", first.Kind);
        Assert.Null(first.Previous);
    }

    [Fact]
    public async Task Saving_the_same_answer_again_records_nothing_and_a_hidden_answer_is_recorded_as_hidden()
    {
        await _questionnaire.GetStateAsync(_software);
        await _questionnaire.SaveAnswersAsync(_software, Save(("free_trial", "true"), ("trial_days", "14")), _admin);
        var count = await _db.QuestionnaireAnswerRevisions.IgnoreQueryFilters().CountAsync(r => r.TenantId == _software);

        await _questionnaire.SaveAnswersAsync(_software, Save(("free_trial", "true"), ("trial_days", "14")), _admin);
        Assert.Equal(count, await _db.QuestionnaireAnswerRevisions.IgnoreQueryFilters().CountAsync(r => r.TenantId == _software));

        await _questionnaire.SaveAnswersAsync(_software, Save(("free_trial", "false")), _admin);
        var hidden = (await _questionnaire.GetHistoryAsync(_software, "trial_days", 10)).First();
        Assert.Equal("Hidden", hidden.Kind);
    }

    [Fact]
    public async Task Answer_history_belongs_to_one_tenant()
    {
        await _questionnaire.GetStateAsync(_software);
        await _questionnaire.GetStateAsync(_clinic);
        await _questionnaire.SaveAnswersAsync(_software, Save(("brand_identity", S("Blue and white"))), _admin);

        Assert.Single(await _questionnaire.GetHistoryAsync(_software, null, 50));
        Assert.Empty(await _questionnaire.GetHistoryAsync(_clinic, null, 50));
    }

    [Fact]
    public async Task One_tenants_questionnaire_session_cannot_be_read_or_changed_by_another()
    {
        await AnswerGenericAsync(_retail);
        var theirs = await _questionnaire.GetStateAsync(_retail);

        // The clinic is a different tenant: it gets its own session, with none of the retailer's answers, and saving changes only its own.
        var mine = await _questionnaire.GetStateAsync(_clinic);
        await _questionnaire.SaveAnswersAsync(_clinic, Save(("brand_identity", S("Blue and white"))));

        Assert.NotEqual(theirs.ResponseId, mine.ResponseId);
        Assert.False(mine.Answers.ContainsKey("brand_identity") && mine.ResponseId == theirs.ResponseId);
        Assert.False((await _questionnaire.GetStateAsync(_retail)).Answers.ContainsKey("brand_identity"));
        Assert.Equal(2, await _db.QuestionnaireResponses.IgnoreQueryFilters().CountAsync(r => r.TenantId == _retail || r.TenantId == _clinic));
    }

    // ===== Phase 2: facts from answers =======================================================================================================

    [Fact]
    public async Task Completing_turns_answers_into_traceable_draft_facts()
    {
        await AnswerGenericAsync(_retail, _admin, ("price_info", S("Groceries from Rs 10.")));
        var state = await _questionnaire.GetStateAsync(_retail);
        var done = await _questionnaire.CompleteAsync(_retail, _admin);

        var price = await AllFacts(_retail).SingleAsync(f => f.SourceRef == "price_info");
        Assert.Equal("Pricing", price.Category);
        Assert.Equal(BusinessFactSource.Questionnaire, price.SourceType);
        Assert.Equal(BusinessFactOrigin.TenantProvided, price.Origin);
        Assert.Equal(BusinessFactStatus.Draft, price.Status);
        Assert.Equal(state.ResponseId, price.SourceResponseId);
        Assert.Equal(180, price.ReviewEveryDays);
        Assert.StartsWith("Prices:", price.Statement);

        var history = await _facts.GetHistoryAsync(_retail, price.Id);
        Assert.Single(history);
        Assert.Equal(BusinessFactChangeKind.Created, history[0].Kind);
        Assert.True(done.FactsCreated >= 6);
    }

    [Fact]
    public async Task Changing_an_answer_straight_away_sends_the_verified_fact_behind_it_back_to_draft_with_a_reason()
    {
        await AnswerGenericAsync(_retail, _admin);
        await _questionnaire.CompleteAsync(_retail, _admin);
        var fact = await AllFacts(_retail).SingleAsync(f => f.SourceRef == "service_locations");
        await _facts.VerifyAsync(_retail, fact.Id, _admin);

        await _questionnaire.RestartAsync(_retail);
        await _questionnaire.SaveAnswersAsync(_retail, Save(("service_locations", S("Pune and Mumbai"))), _manager);

        var changed = await AllFacts(_retail).SingleAsync(f => f.Id == fact.Id);
        Assert.Equal(BusinessFactStatus.Draft, changed.Status);
        Assert.Null(changed.VerifiedBy);
        Assert.Equal("We serve: Pune and Mumbai", changed.Statement);

        var history = await _facts.GetHistoryAsync(_retail, fact.Id);
        Assert.Equal(new[] { BusinessFactChangeKind.Invalidated, BusinessFactChangeKind.Verified, BusinessFactChangeKind.Created }, history.Select(h => h.Kind));
        Assert.Contains("needs confirming again", history[0].Note);
        Assert.Equal(_manager, history[0].ChangedBy);
    }

    [Fact]
    public async Task Clearing_an_answer_retires_the_fact_behind_it_and_leaves_the_others_alone()
    {
        await AnswerGenericAsync(_retail, _admin, ("brand_identity", S("Green and gold")));
        await _questionnaire.CompleteAsync(_retail, _admin);

        await _questionnaire.RestartAsync(_retail);
        await _questionnaire.SaveAnswersAsync(_retail, Save(("brand_identity", "null")), _admin);

        Assert.Equal(BusinessFactStatus.Retired, (await AllFacts(_retail).SingleAsync(f => f.SourceRef == "brand_identity")).Status);
        Assert.Equal(BusinessFactStatus.Draft, (await AllFacts(_retail).SingleAsync(f => f.SourceRef == "business_description")).Status);
    }

    [Fact]
    public async Task Answers_not_yet_completed_make_no_facts_so_nothing_half_finished_is_ever_drafted()
    {
        await AnswerGenericAsync(_retail, _admin);

        Assert.Empty(await AllFacts(_retail).ToListAsync());
    }

    // ----- verification, expiry, proof ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fact_that_goes_stale_expires_after_the_days_its_rule_sets_and_stops_being_usable()
    {
        await AnswerGenericAsync(_retail, _admin, ("price_info", S("Groceries from Rs 10.")));
        await _questionnaire.CompleteAsync(_retail, _admin);
        var price = await AllFacts(_retail).SingleAsync(f => f.SourceRef == "price_info");

        var verified = await _facts.VerifyAsync(_retail, price.Id, _admin);
        Assert.Equal(Now.AddDays(180), verified.ExpiresAtUtc);
        Assert.Equal(KnowledgeClassification.TenantApproved, verified.Classification);
        Assert.Contains((await _knowledge.RetrieveAsync(_retail, "prices groceries")).Facts, f => f.Id == price.Id);

        _clock.UtcNow = Now.AddDays(181);
        var expired = (await _facts.ListAsync(_retail, new BusinessFactQuery { Classification = KnowledgeClassification.Expired })).Items.Single();
        Assert.Equal(price.Id, expired.Id);
        Assert.Equal(KnowledgeClassification.Expired, expired.Classification);
        Assert.DoesNotContain((await _knowledge.RetrieveAsync(_retail, "prices groceries")).Facts, f => f.Id == price.Id);

        // Confirming it again starts a new clock.
        var again = await _facts.VerifyAsync(_retail, price.Id, _admin);
        Assert.Equal(_clock.UtcNow.AddDays(180), again.ExpiresAtUtc);
        Assert.Equal(KnowledgeClassification.TenantApproved, again.Classification);
    }

    [Fact]
    public async Task Verifying_twice_changes_nothing_unless_it_is_an_explicit_reconfirmation()
    {
        var fact = await _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Services", "We sell groceries."));
        var first = await _facts.VerifyAsync(_retail, fact.Id, _admin);
        _clock.UtcNow = Now.AddDays(10);

        var second = await _facts.VerifyAsync(_retail, fact.Id, _manager);
        Assert.Equal(first.VerifiedAtUtc, second.VerifiedAtUtc);
        Assert.Equal(_admin, second.VerifiedBy);

        var reconfirmed = await _facts.VerifyAsync(_retail, fact.Id, _manager, reconfirm: true);
        Assert.Equal(Now.AddDays(10), reconfirmed.VerifiedAtUtc);
        Assert.Equal(_manager, reconfirmed.VerifiedBy);
    }

    [Fact]
    public async Task A_fact_whose_own_end_date_has_passed_cannot_be_verified_until_a_new_date_is_set()
    {
        var fact = await _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Offers", "Free delivery.", ExpiresAtUtc: Now.AddDays(5)));
        await _facts.VerifyAsync(_retail, fact.Id, _admin);
        _clock.UtcNow = Now.AddDays(6);

        await Assert.ThrowsAsync<ConflictException>(() => _facts.VerifyAsync(_retail, fact.Id, _admin));

        await _facts.UpdateDetailsAsync(_retail, fact.Id, _admin, new UpdateFactDetailsRequest(null, _clock.UtcNow.AddDays(30)));
        var again = await _facts.VerifyAsync(_retail, fact.Id, _admin);
        Assert.Equal(KnowledgeClassification.TenantApproved, again.Classification);
    }

    [Fact]
    public async Task Adding_proof_or_a_date_to_a_verified_fact_does_not_undo_its_verification()
    {
        var fact = await _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Results", "Vision improved from 6/36 to 6/9."));
        await _facts.VerifyAsync(_retail, fact.Id, _admin);

        var updated = await _facts.UpdateDetailsAsync(_retail, fact.Id, _admin, new UpdateFactDetailsRequest("Signed outcome report on file.", Now.AddDays(365)));

        Assert.Equal(BusinessFactStatus.Verified, updated.Status);
        Assert.Equal("Signed outcome report on file.", updated.Evidence);
        Assert.Equal((await _facts.GetHistoryAsync(_retail, fact.Id)).First().Kind, BusinessFactChangeKind.DetailsChanged);
    }

    [Fact]
    public async Task Proof_and_dates_are_checked_and_secrets_are_refused_everywhere_a_fact_is_written()
    {
        var fact = await _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Services", "We sell groceries."));

        await Assert.ThrowsAsync<ValidationException>(() => _facts.UpdateDetailsAsync(_retail, fact.Id, _admin, new UpdateFactDetailsRequest(null, Now.AddDays(-1))));
        await Assert.ThrowsAsync<ValidationException>(() => _facts.UpdateDetailsAsync(_retail, fact.Id, _admin, new UpdateFactDetailsRequest(new string('x', 501), null)));
        await Assert.ThrowsAsync<ValidationException>(() => _facts.UpdateDetailsAsync(_retail, fact.Id, _admin, new UpdateFactDetailsRequest("password: hunter2hunter2", null)));
        await Assert.ThrowsAsync<ValidationException>(() => _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Services", "Card 4111 1111 1111 1111")));
        await Assert.ThrowsAsync<ValidationException>(() => _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Services", "Fine.", Subject: new string('s', 121))));
    }

    [Fact]
    public async Task Editing_a_manual_facts_context_sends_a_verified_fact_back_to_draft()
    {
        var fact = await _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Offers", "Free delivery.", Context: "In Pune"));
        await _facts.VerifyAsync(_retail, fact.Id, _admin);

        var changed = await _facts.UpdateAsync(_retail, fact.Id, _admin, new SaveBusinessFactRequest("Offers", "Free delivery.", Context: "Everywhere"));

        Assert.Equal(BusinessFactStatus.Draft, changed.Status);
        Assert.Equal("Everywhere", changed.Context);
    }

    // ----- AI inferences are never silently trusted ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_ai_proposal_enters_as_an_unverified_claim_and_is_never_counted_or_retrieved_until_a_person_verifies_it()
    {
        var proposed = await _facts.ProposeAsync(_retail, new ProposeFactRequest("About", "The store is the best in town.", null, null, BusinessFactOrigin.AiInferred, "Inferred from the store's description."));

        Assert.Equal(BusinessFactStatus.Draft, proposed.Status);
        Assert.Equal(KnowledgeClassification.UnverifiedClaim, proposed.Classification);
        Assert.Empty((await _knowledge.RetrieveAsync(_retail, "best store")).Facts);
        Assert.Equal(RequirementState.Missing, (await _knowledge.GetReadinessAsync(_retail)).ContentTypes.Single(t => t.Key == "general_education").Requirements.Single(r => r.Key == "identity").State);
        Assert.Contains("Proposed by the AI", (await _facts.GetHistoryAsync(_retail, proposed.Id)).Single().Note);

        var verified = await _facts.VerifyAsync(_retail, proposed.Id, _admin);
        Assert.Equal(KnowledgeClassification.TenantApproved, verified.Classification);
        Assert.Single((await _knowledge.RetrieveAsync(_retail, "best store")).Facts);
    }

    [Fact]
    public async Task A_proposal_cannot_pass_itself_off_as_the_tenants_own_word()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _facts.ProposeAsync(_retail, new ProposeFactRequest("About", "Anything.", null, null, BusinessFactOrigin.TenantProvided, null)));
    }

    // ===== Phase 2: readiness and the follow-up workflow ===============================================================================

    [Fact]
    public async Task Readiness_is_built_from_the_tenants_facts_and_explains_itself()
    {
        await AnswerGenericAsync(_retail, _admin);
        await _questionnaire.CompleteAsync(_retail, _admin);

        var before = await _knowledge.GetReadinessAsync(_retail);
        var general = before.ContentTypes.Single(t => t.Key == "general_education");
        Assert.Equal(ContentReadinessStatus.NeedsConfirmation, general.Status); // told, but not yet verified
        Assert.All(general.Requirements, line => Assert.False(string.IsNullOrWhiteSpace(line.Explanation)));

        foreach (var id in await AllFacts(_retail).Select(f => f.Id).ToListAsync())
            await _facts.VerifyAsync(_retail, id, _admin);

        var after = await _knowledge.GetReadinessAsync(_retail);
        Assert.Equal(ContentReadinessStatus.Ready, after.ContentTypes.Single(t => t.Key == "general_education").Status);
        Assert.True(after.OverallScore > before.OverallScore);
        Assert.Equal(ContentReadinessStatus.NotReady, after.ContentTypes.Single(t => t.Key == "case_studies").Status);
    }

    [Fact]
    public async Task Answering_a_follow_up_question_records_an_unverified_fact_and_recalculates_readiness()
    {
        var before = await _knowledge.GetReadinessAsync(_retail);
        Assert.Contains(before.Gaps, g => g.Key == "pricing");

        var after = await _knowledge.AnswerGapAsync(_retail, _admin, "pricing", new AnswerGapRequest("Groceries from Rs 10.", null, "Pune store"));

        var fact = await AllFacts(_retail).SingleAsync();
        Assert.Equal(BusinessFactSource.FollowUp, fact.SourceType);
        Assert.Equal("Pricing", fact.Category);
        Assert.Equal("Pune store", fact.Context);
        Assert.Equal(BusinessFactStatus.Draft, fact.Status);
        Assert.Equal(KnowledgeGapReason.NeedsConfirmation, after.Gaps.Single(g => g.Key == "pricing").Reason);
        Assert.True(after.ContentTypes.Single(t => t.Key == "offers_pricing").Score > before.ContentTypes.Single(t => t.Key == "offers_pricing").Score);
        Assert.Equal(new[] { fact.Id }, after.Gaps.Single(g => g.Key == "pricing").FactsToConfirm);
    }

    [Fact]
    public async Task A_claim_that_needs_proof_cannot_be_answered_without_it_and_the_proof_can_be_added_after()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _knowledge.AnswerGapAsync(_retail, _admin, "customer_results", new AnswerGapRequest("Vision improved from 6/36 to 6/9.", null, null)));
        Assert.Contains(ex.Errors, e => e.PropertyName == "Evidence");

        await _knowledge.AnswerGapAsync(_retail, _admin, "customer_results", new AnswerGapRequest("Vision improved from 6/36 to 6/9.", "Signed outcome report on file.", null));
        var fact = await AllFacts(_retail).SingleAsync();
        Assert.Equal("Signed outcome report on file.", fact.Evidence);
        Assert.Equal("Results", fact.Category);
    }

    [Fact]
    public async Task Proof_alone_is_attached_to_the_facts_already_told_instead_of_duplicating_them()
    {
        await _facts.CreateAsync(_retail, _admin, new SaveBusinessFactRequest("Results", "Vision improved from 6/36 to 6/9."));
        var gap = (await _knowledge.GetReadinessAsync(_retail)).Gaps.Single(g => g.Key == "customer_results");
        Assert.Equal(KnowledgeGapReason.NeedsEvidence, gap.Reason);

        await _knowledge.AnswerGapAsync(_retail, _admin, "customer_results", new AnswerGapRequest("", "Signed outcome report on file.", null));

        var fact = await AllFacts(_retail).SingleAsync();
        Assert.Equal("Signed outcome report on file.", fact.Evidence);
        Assert.Equal(KnowledgeGapReason.NeedsConfirmation, (await _knowledge.GetReadinessAsync(_retail)).Gaps.Single(g => g.Key == "customer_results").Reason);
    }

    [Fact]
    public async Task A_follow_up_answer_is_validated_like_any_other_input()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.AnswerGapAsync(_retail, _admin, "pricing", new AnswerGapRequest("  ", null, null)));
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.AnswerGapAsync(_retail, _admin, "pricing", new AnswerGapRequest(new string('a', 1001), null, null)));
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.AnswerGapAsync(_retail, _admin, "pricing", new AnswerGapRequest("password: hunter2hunter2", null, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => _knowledge.AnswerGapAsync(_retail, _admin, "no_such_gap", new AnswerGapRequest("x", null, null)));
        Assert.Empty(await AllFacts(_retail).ToListAsync());
    }

    [Fact]
    public async Task A_gap_can_be_skipped_only_when_the_rules_allow_it_and_can_be_reopened()
    {
        var skipped = await _knowledge.SkipGapAsync(_retail, _admin, "brand_voice");
        Assert.DoesNotContain(skipped.Gaps, g => g.Key == "brand_voice");
        Assert.Equal(RequirementState.Skipped, skipped.ContentTypes.Single(t => t.Key == "general_education").Requirements.Single(r => r.Key == "brand_voice").State);

        await Assert.ThrowsAsync<ConflictException>(() => _knowledge.SkipGapAsync(_retail, _admin, "pricing"));

        var reopened = await _knowledge.ReopenGapAsync(_retail, "brand_voice");
        Assert.Contains(reopened.Gaps, g => g.Key == "brand_voice");
    }

    [Fact]
    public async Task A_gap_can_be_put_off_to_a_date_within_limits_and_comes_back_by_itself()
    {
        var deferred = await _knowledge.DeferGapAsync(_retail, _admin, "pricing", new DeferGapRequest(null));
        var gap = deferred.Gaps.Single(g => g.Key == "pricing");
        Assert.Equal(KnowledgeGapState.Deferred, gap.State);
        Assert.Equal(Now.AddDays(7), gap.DeferredUntilUtc);

        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.DeferGapAsync(_retail, _admin, "pricing", new DeferGapRequest(Now.AddDays(-1))));
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.DeferGapAsync(_retail, _admin, "pricing", new DeferGapRequest(Now.AddDays(91))));

        _clock.UtcNow = Now.AddDays(8);
        Assert.Equal(KnowledgeGapState.Open, (await _knowledge.GetReadinessAsync(_retail)).Gaps.Single(g => g.Key == "pricing").State);
    }

    [Fact]
    public async Task Answering_a_gap_clears_an_earlier_decision_to_skip_or_defer_it()
    {
        await _knowledge.DeferGapAsync(_retail, _admin, "pricing", new DeferGapRequest(null));

        await _knowledge.AnswerGapAsync(_retail, _admin, "pricing", new AnswerGapRequest("Groceries from Rs 10.", null, null));

        Assert.Empty(await _db.KnowledgeGapDecisions.IgnoreQueryFilters().Where(d => d.TenantId == _retail).ToListAsync());
    }

    [Fact]
    public async Task The_platform_can_change_the_rules_and_a_broken_setting_falls_back_with_a_warning()
    {
        _studio.ReadinessRules = """
        { "draftCredit": 0.0,
          "requirements": [ { "key": "only_one", "label": "Only one", "categories": ["About"], "question": "Q?", "why": "W." } ],
          "contentTypes": [ { "key": "tiny", "label": "Tiny", "minScore": 50, "requirements": [ { "key": "only_one", "weight": 10, "critical": true } ] } ] }
        """;
        var custom = await _knowledge.GetReadinessAsync(_retail);
        Assert.Equal("tiny", Assert.Single(custom.ContentTypes).Key);
        Assert.Null(custom.RulesWarning);

        _studio.ReadinessRules = "{ not json";
        var broken = await _knowledge.GetReadinessAsync(_retail);
        Assert.Equal(5, broken.ContentTypes.Count);
        Assert.NotNull(broken.RulesWarning);
    }

    // ===== Phase 2: retrieval and claims, and isolation ========================================================================================

    private async Task<Guid> VerifiedFactAsync(Guid tenant, string category, string statement, string? subject = null)
    {
        var fact = await _facts.CreateAsync(tenant, _admin, new SaveBusinessFactRequest(category, statement, subject));
        await _facts.VerifyAsync(tenant, fact.Id, _admin);
        return fact.Id;
    }

    [Fact]
    public async Task Retrieval_returns_only_verified_current_facts_ranked_by_relevance_with_a_prompt_that_forbids_anything_else()
    {
        var cataract = await VerifiedFactAsync(_clinic, "Services", "We perform cataract surgery.", "Cataract");
        await VerifiedFactAsync(_clinic, "Contact", "Open Monday to Saturday, 9 to 6.");
        await _facts.CreateAsync(_clinic, _admin, new SaveBusinessFactRequest("Services", "We do free LASIK.")); // unverified
        var retired = await VerifiedFactAsync(_clinic, "Services", "We used to do hearing tests.");
        await _facts.RetireAsync(_clinic, retired, _admin);

        var result = await _knowledge.RetrieveAsync(_clinic, "cataract surgery");

        Assert.Equal(cataract, Assert.Single(result.Facts).Id);
        Assert.Contains("We perform cataract surgery.", result.PromptBlock);
        Assert.Contains("[1] Services / Cataract", result.PromptBlock);
        Assert.DoesNotContain("LASIK", result.PromptBlock);
        Assert.DoesNotContain("hearing", result.PromptBlock);
        Assert.Contains("only facts", result.PromptBlock);

        var everything = await _knowledge.RetrieveAsync(_clinic, null);
        Assert.Equal(2, everything.Facts.Count);

        Assert.Equal(1, (await _knowledge.RetrieveAsync(_clinic, null, 1)).Facts.Count);
        Assert.Empty((await _knowledge.RetrieveAsync(_clinic, "zebra")).Facts);
        Assert.Contains("No verified facts", (await _knowledge.RetrieveAsync(_clinic, "zebra")).PromptBlock);
    }

    [Fact]
    public async Task One_tenants_knowledge_never_appears_in_another_tenants_retrieval_readiness_or_claim_checks()
    {
        await VerifiedFactAsync(_clinic, "Services", "We perform cataract surgery at our Pune clinic.");
        await VerifiedFactAsync(_clinic, "About", "A clinic in Pune.");
        await _knowledge.AnswerGapAsync(_clinic, _admin, "pricing", new AnswerGapRequest("Consultation Rs 500.", null, null));
        await _knowledge.SkipGapAsync(_clinic, _admin, "brand_voice");

        var theirRetrieval = await _knowledge.RetrieveAsync(_software, "cataract surgery clinic Pune");
        Assert.Empty(theirRetrieval.Facts);
        Assert.DoesNotContain("cataract", theirRetrieval.PromptBlock);

        var theirReadiness = await _knowledge.GetReadinessAsync(_software);
        Assert.Equal(0, theirReadiness.OverallScore);
        Assert.Equal(0, theirReadiness.Facts.Verified);
        Assert.Contains(theirReadiness.Gaps, g => g.Key == "brand_voice"); // the clinic's skip is the clinic's

        var claim = await _knowledge.CheckClaimsAsync(_software, new CheckClaimsRequest(new[] { "We perform cataract surgery at our Pune clinic." }));
        Assert.Equal(ClaimVerdict.Unsupported, claim.Claims.Single().Verdict);

        Assert.Equal(0, (await _facts.ListAsync(_software, new BusinessFactQuery())).TotalCount);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_the_history_of_or_change_a_fact_it_does_not_own()
    {
        var theirs = await VerifiedFactAsync(_clinic, "Services", "We perform cataract surgery.");

        await Assert.ThrowsAsync<NotFoundException>(() => _facts.GetHistoryAsync(_software, theirs));
        await Assert.ThrowsAsync<NotFoundException>(() => _facts.UpdateDetailsAsync(_software, theirs, _admin, new UpdateFactDetailsRequest("x", null)));
        await Assert.ThrowsAsync<NotFoundException>(() => _facts.VerifyAsync(_software, theirs, _admin, reconfirm: true));
    }

    [Fact]
    public async Task The_claim_check_flags_what_the_verified_facts_do_not_support()
    {
        await VerifiedFactAsync(_clinic, "Services", "We perform cataract surgery.");
        await _facts.CreateAsync(_clinic, _admin, new SaveBusinessFactRequest("Services", "We offer free LASIK for everyone."));

        var result = await _knowledge.CheckClaimsAsync(_clinic, new CheckClaimsRequest(new[]
        {
            "We perform cataract surgery.", "We offer free LASIK for everyone.", "We have the best doctors in India."
        }));

        Assert.Equal(new[] { ClaimVerdict.Supported, ClaimVerdict.Unverified, ClaimVerdict.Unsupported }, result.Claims.Select(c => c.Verdict));
        Assert.Equal(1, result.Supported);
        Assert.Equal(2, result.Flagged);
        Assert.True(result.Claims[2].IsRisky);
    }

    [Fact]
    public async Task The_claim_check_refuses_an_empty_or_oversized_request()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.CheckClaimsAsync(_clinic, new CheckClaimsRequest(Array.Empty<string>())));
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.CheckClaimsAsync(_clinic, new CheckClaimsRequest(new[] { new string('a', 501) })));
        await Assert.ThrowsAsync<ValidationException>(() => _knowledge.CheckClaimsAsync(_clinic, new CheckClaimsRequest(Enumerable.Range(0, 51).Select(i => $"claim {i}").ToArray())));
    }

    [Fact]
    public async Task Every_knowledge_action_is_refused_while_the_studio_is_off_for_the_tenant()
    {
        _studio.Off.Add(_clinic);

        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.GetReadinessAsync(_clinic));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.AnswerGapAsync(_clinic, _admin, "pricing", new AnswerGapRequest("x", null, null)));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.SkipGapAsync(_clinic, _admin, "brand_voice"));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.DeferGapAsync(_clinic, _admin, "pricing", new DeferGapRequest(null)));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.ReopenGapAsync(_clinic, "pricing"));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.CheckClaimsAsync(_clinic, new CheckClaimsRequest(new[] { "x" })));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _knowledge.RetrieveAsync(_clinic, "x"));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _questionnaire.GetHistoryAsync(_clinic, null, 10));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _facts.GetHistoryAsync(_clinic, Guid.NewGuid()));
    }

    // ----- the API surface ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_new_endpoints_sit_behind_the_studio_roles_and_verifying_stays_admin_only()
    {
        var controller = typeof(ContentStudioQuestionnaireController);
        var classAuth = controller.GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(StudioRoles.Contributors, classAuth!.Roles);

        foreach (var name in new[] { "GetReadiness", "AnswerGap", "SkipGap", "DeferGap", "ReopenGap", "CheckClaims", "GetAnswerHistory", "UpdateFactDetails", "GetFactHistory" })
        {
            var method = controller.GetMethod(name)!;
            Assert.NotNull(method);
            Assert.NotNull(method.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name.StartsWith("Http")));
            // None of them widens access beyond the class-level roles.
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        Assert.Equal(AppRoles.Admin, controller.GetMethod("VerifyFact")!.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
    }

    [Fact]
    public void The_platform_can_fetch_the_built_in_rules_and_check_a_rule_set_before_saving_it()
    {
        Assert.Equal(AppRoles.PlatformSuperAdmin, typeof(PlatformStudioReadinessController).GetCustomAttribute<AuthorizeAttribute>()!.Roles);

        var controller = new PlatformStudioReadinessController();
        var defaults = ((DefaultReadinessRulesDto)((Microsoft.AspNetCore.Mvc.OkObjectResult)controller.Default().Result!).Value!).Json;
        Assert.Equal(ReadinessRules.DefaultJson, defaults);

        ReadinessRulesValidationDto Check(string? json) =>
            (ReadinessRulesValidationDto)((Microsoft.AspNetCore.Mvc.OkObjectResult)controller.Validate(new ValidateReadinessRulesRequest(json)).Result!).Value!;

        Assert.True(Check(defaults).Valid);
        Assert.True(Check("   ").Valid); // empty = built-in
        Assert.False(Check("{ not json").Valid);

        var bad = Check("""{ "requirements": [], "contentTypes": [] }""");
        Assert.False(bad.Valid);
        Assert.NotEmpty(bad.Problems);
    }
}
