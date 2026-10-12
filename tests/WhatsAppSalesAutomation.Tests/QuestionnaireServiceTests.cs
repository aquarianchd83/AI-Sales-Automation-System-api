using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>AI Content Studio phase 2 with a database: the tenant's questionnaire and business facts, and the platform's templates.</summary>
public sealed class QuestionnaireServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

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

    private sealed class RecordingAudit : IPlatformAuditService
    {
        public List<(string Action, string? Details)> Entries { get; } = new();

        public Task LogAsync(Guid actorUserId, string actorEmail, string action, Guid? targetTenantId = null, Guid? targetUserId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }

        public Task<PagedResult<PlatformAuditLogEntryDto>> GetPagedAsync(PlatformAuditLogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private const string Generic = """
    {
      "questions": [
        { "key": "business_name", "type": "ShortText", "prompt": "Name?", "required": true, "prefillFrom": "Name",
          "fact": { "category": "About", "template": "Called {value}." } },
        { "key": "has_offer", "type": "Boolean", "prompt": "Offer?" },
        { "key": "offer", "type": "ShortText", "prompt": "What offer?", "required": true,
          "showWhen": { "questionKey": "has_offer", "operator": "Equal", "value": true },
          "fact": { "category": "Offers", "template": "Offer: {value}" } }
      ]
    }
    """;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly RecordingAudit _audit = new();
    private readonly QuestionnaireTemplateService _templates;
    private readonly QuestionnaireService _questionnaire;
    private readonly BusinessFactService _facts;
    private readonly StudioConfig _studio = new() { Enabled = true };
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    /// <summary>The effective studio switch per tenant, as the override provider would resolve it.</summary>
    private sealed class StudioConfig
    {
        public bool Enabled { get; set; }

        public HashSet<Guid> Off { get; } = new();

        public ITenantConfigOverrideProvider Provider => Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = Enabled && !Off.Contains((Guid)args![0]!) })
                : throw new NotImplementedException(m.Name));
    }

    public QuestionnaireServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Trilok Eye Care", Slug = "trilok", Industry = "Eye care", Status = TenantStatus.Active });
        _db.Tenants.Add(new Tenant { Id = _other, Name = "Other Co", Slug = "other", Industry = "Retail", Status = TenantStatus.Active });
        _db.SaveChanges();

        _templates = new QuestionnaireTemplateService(_db, _clock, _audit);
        _questionnaire = new QuestionnaireService(_db, _studio.Provider, _clock);
        _facts = new BusinessFactService(_db, _studio.Provider, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SaveQuestionnaireAnswersRequest Save(params (string Key, string Json)[] answers) =>
        new(answers.ToDictionary(a => a.Key, a => J(a.Json)));

    private async Task<Guid> PublishAsync(string key, string json, string? industry = null)
    {
        var created = await _templates.CreateAsync(new CreateQuestionnaireTemplateRequest(key, key, null, industry, json), _admin, "root@platform.test");
        await _templates.PublishVersionAsync(created.Template.Id, 1, _admin, "root@platform.test");
        return created.Template.Id;
    }

    private async Task<QuestionnaireStateDto> CompleteSimpleAsync(string name = "Trilok Eye Care")
    {
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("business_name", JsonSerializer.Serialize(name)), ("has_offer", "false")));
        return (await _questionnaire.CompleteAsync(_tenant, _admin)).State;
    }

    // ----- which questionnaire a tenant gets -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tenant_with_no_published_questionnaire_is_told_so()
    {
        var state = await _questionnaire.GetStateAsync(_tenant);

        Assert.False(state.Available);
        Assert.False(string.IsNullOrWhiteSpace(state.Message));
        Assert.Equal(0, await _db.QuestionnaireResponses.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_draft_template_is_not_offered()
    {
        await _templates.CreateAsync(new CreateQuestionnaireTemplateRequest("draft-only", "Draft", null, null, Generic), _admin, "a@b.c");

        Assert.False((await _questionnaire.GetStateAsync(_tenant)).Available);
    }

    [Fact]
    public async Task The_template_for_the_tenants_industry_wins_over_the_generic_one()
    {
        await PublishAsync("generic", Generic);
        var eye = await PublishAsync("eye-care", Generic, industry: "eye CARE");

        var state = await _questionnaire.GetStateAsync(_tenant);
        var otherState = await _questionnaire.GetStateAsync(_other);

        Assert.Equal(eye, state.TemplateId);
        Assert.NotEqual(eye, otherState.TemplateId);
    }

    [Fact]
    public async Task A_deactivated_template_is_skipped()
    {
        var eye = await PublishAsync("eye-care", Generic, industry: "Eye care");
        var generic = await PublishAsync("generic", Generic);
        await _templates.UpdateAsync(eye, new UpdateQuestionnaireTemplateRequest("Eye", null, "Eye care", IsActive: false), _admin, "a@b.c");

        Assert.Equal(generic, (await _questionnaire.GetStateAsync(_tenant)).TemplateId);
    }

    [Fact]
    public async Task The_first_look_starts_the_questionnaire_and_fills_in_what_the_profile_already_says()
    {
        await PublishAsync("generic", Generic);

        var state = await _questionnaire.GetStateAsync(_tenant);

        Assert.True(state.Available);
        Assert.Equal(QuestionnaireResponseStatus.InProgress, state.Status);
        Assert.Equal("Trilok Eye Care", state.Answers["business_name"].GetString());

        // Asking again does not start a second one.
        await _questionnaire.GetStateAsync(_tenant);
        Assert.Equal(1, await _db.QuestionnaireResponses.IgnoreQueryFilters().CountAsync(r => r.TenantId == _tenant));
    }

    // ----- answering -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_merges_answers_and_the_server_says_which_questions_are_asked()
    {
        await PublishAsync("generic", Generic);

        var first = await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "true")));
        Assert.Contains("offer", first.VisibleKeys);

        var second = await _questionnaire.SaveAnswersAsync(_tenant, Save(("offer", "\"10% off\"")));
        Assert.Equal("true", second.Answers["has_offer"].ToString().ToLowerInvariant());
        Assert.Equal("10% off", second.Answers["offer"].GetString());
    }

    [Fact]
    public async Task Turning_a_condition_off_removes_the_answer_it_hid()
    {
        await PublishAsync("generic", Generic);
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "true"), ("offer", "\"10% off\"")));

        var state = await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "false")));

        Assert.DoesNotContain("offer", state.VisibleKeys);
        Assert.False(state.Answers.ContainsKey("offer"));
        var stored = await _db.QuestionnaireResponses.IgnoreQueryFilters().SingleAsync(r => r.TenantId == _tenant);
        Assert.DoesNotContain("10% off", stored.AnswersJson);
    }

    [Fact]
    public async Task A_null_or_blank_value_clears_that_answer()
    {
        await PublishAsync("generic", Generic);

        var state = await _questionnaire.SaveAnswersAsync(_tenant, Save(("business_name", "null")));

        Assert.False(state.Answers.ContainsKey("business_name"));
    }

    [Fact]
    public async Task An_answer_of_the_wrong_kind_is_refused_and_nothing_is_saved()
    {
        await PublishAsync("generic", Generic);

        var ex = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "\"maybe\""))));

        Assert.Contains(ex.Errors, e => e.PropertyName == "has_offer");
        Assert.False((await _questionnaire.GetStateAsync(_tenant)).Answers.ContainsKey("has_offer"));
    }

    [Fact]
    public async Task Completing_needs_every_asked_required_question_and_ignores_the_hidden_ones()
    {
        await PublishAsync("generic", Generic);
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "true")));

        var ex = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _questionnaire.CompleteAsync(_tenant, _admin));
        Assert.Contains(ex.Errors, e => e.PropertyName == "offer");

        await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "false")));
        var done = await _questionnaire.CompleteAsync(_tenant, _admin);
        Assert.Equal(QuestionnaireResponseStatus.Completed, done.State.Status);
        Assert.NotNull(done.State.CompletedAtUtc);
    }

    [Fact]
    public async Task A_completed_questionnaire_cannot_be_edited_in_place()
    {
        await PublishAsync("generic", Generic);
        await CompleteSimpleAsync();

        await Assert.ThrowsAsync<ConflictException>(() => _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "true"))));
        await Assert.ThrowsAsync<ConflictException>(() => _questionnaire.CompleteAsync(_tenant, _admin));
    }

    // ----- versions ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_a_newer_version_leaves_a_completed_response_on_the_version_it_answered()
    {
        var id = await PublishAsync("generic", Generic);
        var done = await CompleteSimpleAsync();
        Assert.False(done.NewerVersionAvailable);

        await _templates.CreateVersionAsync(id, _admin, "a@b.c");
        await _templates.SaveVersionAsync(id, 2, new SaveQuestionnaireVersionRequest(Generic.Replace("\"Name?\"", "\"Business name?\"")), _admin, "a@b.c");
        await _templates.PublishVersionAsync(id, 2, _admin, "a@b.c");

        var state = await _questionnaire.GetStateAsync(_tenant);
        Assert.Equal(1, state.VersionNumber);
        Assert.Equal(QuestionnaireResponseStatus.Completed, state.Status);
        Assert.True(state.NewerVersionAvailable);
        Assert.Equal("Name?", state.Definition!.Questions[0].Prompt);
    }

    [Fact]
    public async Task Restarting_moves_to_the_newest_version_and_carries_over_the_answers_that_still_fit()
    {
        var id = await PublishAsync("generic", Generic);
        await CompleteSimpleAsync("Carried Over");

        await _templates.CreateVersionAsync(id, _admin, "a@b.c");
        // Version 2 drops the offer questions and keeps business_name.
        await _templates.SaveVersionAsync(id, 2, new SaveQuestionnaireVersionRequest("""{ "questions": [ { "key": "business_name", "type": "ShortText", "prompt": "Name?", "required": true } ] }"""), _admin, "a@b.c");
        await _templates.PublishVersionAsync(id, 2, _admin, "a@b.c");

        var state = await _questionnaire.RestartAsync(_tenant);

        Assert.Equal(2, state.VersionNumber);
        Assert.Equal(QuestionnaireResponseStatus.InProgress, state.Status);
        Assert.Equal("Carried Over", state.Answers["business_name"].GetString());
        Assert.False(state.Answers.ContainsKey("has_offer"));
        Assert.Equal(2, await _db.QuestionnaireResponses.IgnoreQueryFilters().CountAsync(r => r.TenantId == _tenant)); // the old one is kept
    }

    [Fact]
    public async Task An_unfinished_response_moves_up_to_a_newer_version_by_itself_without_losing_answers()
    {
        var id = await PublishAsync("generic", Generic);
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("business_name", "\"Mid Way\"")));

        await _templates.CreateVersionAsync(id, _admin, "a@b.c");
        await _templates.PublishVersionAsync(id, 2, _admin, "a@b.c");

        var state = await _questionnaire.GetStateAsync(_tenant);
        Assert.Equal(2, state.VersionNumber);
        Assert.Equal("Mid Way", state.Answers["business_name"].GetString());
        Assert.False(state.NewerVersionAvailable);
    }

    // ----- facts ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Completing_drafts_a_fact_for_each_answer_and_nothing_is_verified_yet()
    {
        await PublishAsync("generic", Generic);

        var result = await _questionnaire.CompleteAsync(await StartAndAnswerAsync(), _admin);

        Assert.Equal(1, result.FactsCreated);
        var fact = await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(BusinessFactStatus.Draft, fact.Status);
        Assert.Equal(BusinessFactSource.Questionnaire, fact.SourceType);
        Assert.Equal("business_name", fact.SourceRef);
        Assert.Equal("Called Trilok Eye Care.", fact.Statement);
    }

    private async Task<Guid> StartAndAnswerAsync()
    {
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("business_name", "\"Trilok Eye Care\""), ("has_offer", "false")));
        return _tenant;
    }

    [Fact]
    public async Task Re_answering_changes_the_same_fact_and_a_verified_fact_whose_words_change_needs_verifying_again()
    {
        await PublishAsync("generic", Generic);
        await CompleteSimpleAsync("Old Name");
        var fact = await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync();
        await _facts.VerifyAsync(_tenant, fact.Id, _admin);

        await _questionnaire.RestartAsync(_tenant);
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("business_name", "\"New Name\"")));
        var again = await _questionnaire.CompleteAsync(_tenant, _admin);

        Assert.Equal(1, again.FactsUpdated);
        Assert.Equal(0, again.FactsCreated);
        var updated = await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(fact.Id, updated.Id);
        Assert.Equal("Called New Name.", updated.Statement);
        Assert.Equal(BusinessFactStatus.Draft, updated.Status);
        Assert.Null(updated.VerifiedBy);
    }

    [Fact]
    public async Task Re_completing_with_the_same_answer_leaves_a_verified_fact_verified()
    {
        await PublishAsync("generic", Generic);
        await CompleteSimpleAsync();
        await _facts.VerifyAsync(_tenant, (await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync()).Id, _admin);

        await _questionnaire.RestartAsync(_tenant);
        var again = await _questionnaire.CompleteAsync(_tenant, _admin);

        Assert.Equal(0, again.FactsUpdated);
        Assert.Equal(BusinessFactStatus.Verified, (await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_fact_whose_question_is_no_longer_asked_is_retired()
    {
        await PublishAsync("generic", Generic);
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "true"), ("offer", "\"Free check-up\"")));
        await _questionnaire.CompleteAsync(_tenant, _admin);
        Assert.Equal(2, await _db.BusinessFacts.IgnoreQueryFilters().CountAsync(f => f.Status == BusinessFactStatus.Draft));

        await _questionnaire.RestartAsync(_tenant);
        await _questionnaire.SaveAnswersAsync(_tenant, Save(("has_offer", "false")));
        var again = await _questionnaire.CompleteAsync(_tenant, _admin);

        Assert.Equal(1, again.FactsRetired);
        Assert.Equal(BusinessFactStatus.Retired, (await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync(f => f.SourceRef == "offer")).Status);
    }

    [Fact]
    public async Task Verifying_records_who_and_when_and_is_idempotent()
    {
        var fact = await _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("Services", "We do cataract surgery."));
        Assert.Equal(BusinessFactStatus.Draft, fact.Status);

        var verified = await _facts.VerifyAsync(_tenant, fact.Id, _admin);
        _clock.UtcNow = Now.AddDays(1);
        var again = await _facts.VerifyAsync(_tenant, fact.Id, Guid.NewGuid());

        Assert.Equal(BusinessFactStatus.Verified, verified.Status);
        Assert.Equal(_admin, verified.VerifiedBy);
        Assert.Equal(Now, verified.VerifiedAtUtc);
        Assert.Equal(_admin, again.VerifiedBy); // the first verification stands
        Assert.Equal(Now, again.VerifiedAtUtc);
    }

    [Fact]
    public async Task Editing_a_verified_manual_fact_sends_it_back_to_draft_but_only_if_the_words_changed()
    {
        var fact = await _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("Services", "We do cataract surgery."));
        await _facts.VerifyAsync(_tenant, fact.Id, _admin);

        var same = await _facts.UpdateAsync(_tenant, fact.Id, _admin, new SaveBusinessFactRequest("Services", "  We do cataract surgery.  "));
        Assert.Equal(BusinessFactStatus.Verified, same.Status);

        var changed = await _facts.UpdateAsync(_tenant, fact.Id, _admin, new SaveBusinessFactRequest("Services", "We do cataract and LASIK surgery."));
        Assert.Equal(BusinessFactStatus.Draft, changed.Status);
        Assert.Null(changed.VerifiedBy);
    }

    [Fact]
    public async Task A_questionnaire_fact_is_changed_by_answering_again_not_by_editing_it()
    {
        await PublishAsync("generic", Generic);
        await CompleteSimpleAsync();
        var fact = await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync();

        await Assert.ThrowsAsync<ConflictException>(() => _facts.UpdateAsync(_tenant, fact.Id, _admin, new SaveBusinessFactRequest("About", "Something else.")));
    }

    [Fact]
    public async Task A_retired_fact_cannot_be_verified_or_edited()
    {
        var fact = await _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("Services", "Old service."));
        await _facts.RetireAsync(_tenant, fact.Id, _admin);

        await Assert.ThrowsAsync<ConflictException>(() => _facts.VerifyAsync(_tenant, fact.Id, _admin));
        await Assert.ThrowsAsync<ConflictException>(() => _facts.UpdateAsync(_tenant, fact.Id, _admin, new SaveBusinessFactRequest("Services", "New wording.")));
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("   ", "text")]
    [InlineData("Cat", "")]
    [InlineData("Cat", "   ")]
    public async Task A_fact_needs_a_category_and_a_statement(string category, string statement)
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest(category, statement)));
    }

    [Fact]
    public async Task Facts_can_be_listed_filtered_and_counted()
    {
        var a = await _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("Services", "We do cataract surgery."));
        await _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("Contact", "We are in Delhi."));
        var c = await _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("Contact", "Old phone."));
        await _facts.VerifyAsync(_tenant, a.Id, _admin);
        await _facts.RetireAsync(_tenant, c.Id, _admin);

        var verified = await _facts.ListAsync(_tenant, new BusinessFactQuery { Status = BusinessFactStatus.Verified });
        var contact = await _facts.ListAsync(_tenant, new BusinessFactQuery { Category = "Contact" });
        var search = await _facts.ListAsync(_tenant, new BusinessFactQuery { Search = "Delhi" });
        var summary = await _facts.GetSummaryAsync(_tenant);

        Assert.Equal("Verified", verified.Items.Single().Status.ToString());
        Assert.Equal(2, contact.TotalCount);
        Assert.Single(search.Items);
        Assert.Equal((1, 1, 1), (summary.Draft, summary.Verified, summary.Retired));
        Assert.Equal(new[] { "Contact", "Services" }, summary.Categories);
    }

    // ----- isolation and the switch --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_tenants_facts_and_answers_are_invisible_and_untouchable_to_another()
    {
        await PublishAsync("generic", Generic);
        var theirs = await _facts.CreateAsync(_other, _admin, new SaveBusinessFactRequest("Services", "Their fact."));
        await _questionnaire.SaveAnswersAsync(_other, Save(("business_name", "\"Other Co\"")));

        Assert.Equal(0, (await _facts.ListAsync(_tenant, new BusinessFactQuery())).TotalCount);
        await Assert.ThrowsAsync<NotFoundException>(() => _facts.VerifyAsync(_tenant, theirs.Id, _admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _facts.RetireAsync(_tenant, theirs.Id, _admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _facts.UpdateAsync(_tenant, theirs.Id, _admin, new SaveBusinessFactRequest("x", "y")));

        var mine = await _questionnaire.GetStateAsync(_tenant);
        Assert.Equal("Trilok Eye Care", mine.Answers["business_name"].GetString());
        Assert.Equal("Other Co", (await _questionnaire.GetStateAsync(_other)).Answers["business_name"].GetString());
    }

    [Fact]
    public async Task Every_tenant_action_is_refused_while_the_studio_is_off_for_that_tenant()
    {
        await PublishAsync("generic", Generic);
        _studio.Off.Add(_tenant);

        await Assert.ThrowsAsync<FeatureDisabledException>(() => _questionnaire.GetStateAsync(_tenant));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _questionnaire.SaveAnswersAsync(_tenant, Save()));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _questionnaire.CompleteAsync(_tenant, _admin));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _questionnaire.RestartAsync(_tenant));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _facts.ListAsync(_tenant, new BusinessFactQuery()));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _facts.GetSummaryAsync(_tenant));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _facts.CreateAsync(_tenant, _admin, new SaveBusinessFactRequest("a", "b")));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _facts.VerifyAsync(_tenant, Guid.NewGuid(), _admin));

        Assert.True((await _questionnaire.GetStateAsync(_other)).Available); // other tenants are unaffected
    }

    // ----- the platform's templates --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_template_starts_as_an_unpublished_draft_and_is_audited()
    {
        var created = await _templates.CreateAsync(new CreateQuestionnaireTemplateRequest("eye-care", "Eye care", "For clinics", "Eye care", Generic), _admin, "root@platform.test");

        Assert.Equal(1, created.Template.LatestVersion);
        Assert.Null(created.Template.PublishedVersion);
        Assert.Equal(QuestionnaireVersionStatus.Draft, created.Versions.Single().Status);
        Assert.Contains(_audit.Entries, e => e.Action == "StudioQuestionnaireCreated" && e.Details == "eye-care");
    }

    [Theory]
    [InlineData("Bad Key", "Title")]
    [InlineData("a", "Title")]
    [InlineData("good-key", "")]
    public async Task A_template_needs_a_proper_key_and_a_title(string key, string title)
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _templates.CreateAsync(new CreateQuestionnaireTemplateRequest(key, title, null, null, Generic), _admin, "a@b.c"));
    }

    [Fact]
    public async Task A_template_with_an_invalid_definition_cannot_be_created_and_a_key_cannot_be_reused()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _templates.CreateAsync(new CreateQuestionnaireTemplateRequest("broken", "Broken", null, null, """{ "questions": [] }"""), _admin, "a@b.c"));

        await _templates.CreateAsync(new CreateQuestionnaireTemplateRequest("same", "One", null, null, Generic), _admin, "a@b.c");
        await Assert.ThrowsAsync<ConflictException>(() =>
            _templates.CreateAsync(new CreateQuestionnaireTemplateRequest("same", "Two", null, null, Generic), _admin, "a@b.c"));
    }

    [Fact]
    public async Task A_draft_can_be_saved_unfinished_but_cannot_be_published_until_it_is_sound()
    {
        var created = await _templates.CreateAsync(new CreateQuestionnaireTemplateRequest("wip", "WIP", null, null, Generic), _admin, "a@b.c");

        await _templates.SaveVersionAsync(created.Template.Id, 1, new SaveQuestionnaireVersionRequest("""{ "questions": [] }"""), _admin, "a@b.c");
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _templates.PublishVersionAsync(created.Template.Id, 1, _admin, "a@b.c"));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _templates.SaveVersionAsync(created.Template.Id, 1, new SaveQuestionnaireVersionRequest("not json"), _admin, "a@b.c"));

        await _templates.SaveVersionAsync(created.Template.Id, 1, new SaveQuestionnaireVersionRequest(Generic), _admin, "a@b.c");
        var published = await _templates.PublishVersionAsync(created.Template.Id, 1, _admin, "a@b.c");

        Assert.Equal(1, published.Template.PublishedVersion);
        Assert.Contains(_audit.Entries, e => e.Action == "StudioQuestionnairePublished");
    }

    [Fact]
    public async Task A_published_version_can_never_be_edited_or_published_again()
    {
        var id = await PublishAsync("locked", Generic);

        await Assert.ThrowsAsync<ConflictException>(() => _templates.SaveVersionAsync(id, 1, new SaveQuestionnaireVersionRequest(Generic), _admin, "a@b.c"));
        await Assert.ThrowsAsync<ConflictException>(() => _templates.PublishVersionAsync(id, 1, _admin, "a@b.c"));
    }

    [Fact]
    public async Task A_new_version_is_a_copy_of_the_latest_and_only_one_draft_can_exist()
    {
        var id = await PublishAsync("versioned", Generic);

        var detail = await _templates.CreateVersionAsync(id, _admin, "a@b.c");

        Assert.Equal(2, detail.Template.LatestVersion);
        Assert.Equal(1, detail.Template.PublishedVersion);
        Assert.Equal(detail.Versions.Single(v => v.Number == 1).DefinitionJson, detail.Versions.Single(v => v.Number == 2).DefinitionJson);
        await Assert.ThrowsAsync<ConflictException>(() => _templates.CreateVersionAsync(id, _admin, "a@b.c"));
    }

    [Fact]
    public void Validation_reports_problems_and_the_starter_is_offered()
    {
        Assert.False(_templates.Validate("""{ "questions": [] }""").Valid);
        var good = _templates.Validate(_templates.GetStarterDefinition());
        Assert.True(good.Valid);
        Assert.True(good.QuestionCount >= 5);
    }

    // ----- who may do what -----------------------------------------------------------------------------------------------------------

    private static string? RolesOf(MethodInfo method) =>
        method.GetCustomAttributes<AuthorizeAttribute>().FirstOrDefault()?.Roles;

    [Fact]
    public void Only_an_admin_may_verify_a_fact_while_admins_and_sales_managers_do_the_rest()
    {
        var controller = typeof(ContentStudioQuestionnaireController);

        Assert.Equal(AppRoles.Admin, RolesOf(controller.GetMethod(nameof(ContentStudioQuestionnaireController.VerifyFact))!));

        var classRoles = controller.GetCustomAttribute<AuthorizeAttribute>()!.Roles!;
        Assert.Contains(AppRoles.Admin, classRoles);
        Assert.Contains(AppRoles.SalesManager, classRoles);
        Assert.DoesNotContain(AppRoles.SalesAgent, classRoles);

        // Every other action relies on the class-level gate, so none of them can be left wide open or accidentally narrowed to nothing.
        foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name != nameof(ContentStudioQuestionnaireController.VerifyFact)))
            Assert.Null(RolesOf(action));
    }

    [Fact]
    public void The_platform_templates_are_for_the_platform_operator_alone()
    {
        var roles = typeof(PlatformStudioQuestionnaireController).GetCustomAttribute<AuthorizeAttribute>()!.Roles;

        Assert.Equal(AppRoles.PlatformSuperAdmin, roles);
        foreach (var action in typeof(PlatformStudioQuestionnaireController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Null(RolesOf(action));
    }
}
