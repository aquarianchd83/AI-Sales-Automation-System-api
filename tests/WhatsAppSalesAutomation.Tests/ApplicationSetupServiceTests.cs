using System.Text.Json;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Setup;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The plan-driven setup flow end to end: pick a plan, answer what it asks, confirm, run - plus plan change,
/// versioning, the audit trail and the admin editor that authors the questions. Each test names the acceptance
/// criterion it proves.</summary>
public sealed class ApplicationSetupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly ApplicationSetupService _service;
    private readonly SetupAdminService _admin;

    private Plan _basic = null!;
    private Plan _leads = null!;

    public ApplicationSetupServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new User(_user)) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        _service = new ApplicationSetupService(_db, new Ambient(_tenant), new User(_user), _clock);
        _admin = new SetupAdminService(
            _db, new SaveRequirementRequestValidator(), new SaveSetupVersionRequestValidator(), new CreateSetupVersionRequestValidator(),
            new User(_user), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------

    private static SaveRequirementRequest Req(
        string key, SetupFieldType type = SetupFieldType.Text, bool required = true, string section = "business", int order = 10,
        SetupConditionDto? when = null, IReadOnlyList<SetupOptionDto>? options = null, string? metric = null, string? def = null) =>
        new(key, key.Replace('_', ' '), null, type, required, def, options, null, order, section, when, metric, true);

    private static IReadOnlyList<SetupOptionDto> YesNo => new[] { new SetupOptionDto("yes", "Yes"), new SetupOptionDto("no", "No") };

    /// <summary>Builds and publishes a first version of the plan's requirements through the same admin service the
    /// console uses - so every test also exercises the "no code change" path.</summary>
    private async Task<Guid> PublishV1Async(Plan plan, params SaveRequirementRequest[] fields)
    {
        var draft = await _admin.CreateVersionAsync(plan.Id, new CreateSetupVersionRequest(null, null));
        foreach (var field in fields)
            await _admin.AddRequirementAsync(draft.Id, field);
        await _admin.PublishVersionAsync(draft.Id);
        return draft.Id;
    }

    private async Task SeedPlansAsync()
    {
        _basic = new Plan { Code = "basic", Name = "Basic Social", IsActive = true };
        _leads = new Plan { Code = "leads", Name = "Lead Generation", IsActive = true };
        _db.Plans.AddRange(_basic, _leads);
        await _db.SaveChangesAsync();

        await PublishV1Async(_basic,
            Req("brand_name", order: 10),
            Req("contact_email", SetupFieldType.Email, order: 20),
            Req("website", SetupFieldType.Url, required: false, order: 30),
            Req("run_ads", SetupFieldType.Radio, order: 40, options: YesNo),
            Req("ad_budget", SetupFieldType.Currency, order: 50, when: new SetupConditionDto("run_ads", SetupConditionOperator.Equals, "yes")));

        await PublishV1Async(_leads,
            Req("brand_name", order: 10),
            Req("contact_email", SetupFieldType.Email, order: 20),
            Req("target_location", section: "audience", order: 30),
            Req("lead_source", SetupFieldType.MultiSelect, section: "campaign", order: 40,
                options: new[] { new SetupOptionDto("search", "Search"), new SetupOptionDto("social", "Social") }));
    }

    private static IReadOnlyDictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private async Task<ApplicationDto> NewAppAsync(Plan plan, string? name = null) =>
        await _service.CreateAsync(new CreateApplicationRequest(plan.Id, name));

    private Task<SaveSetupResultDto> SaveAsync(Guid appId, object values, bool complete = false, string? reason = null) =>
        _service.SaveSetupAsync(appId, new SaveSetupRequest(Values(values), complete, reason));

    private Task<SaveSetupResultDto> FillBasicAsync(Guid appId, bool complete = true) =>
        SaveAsync(appId, new { brand_name = "ABC Software", contact_email = "owner@abc.test", run_ads = "no" }, complete);

    // ---- AC1 / AC2: the plan drives the fields ----------------------------------------------------------------------

    [Fact]
    public async Task AC1_AC2_Opening_an_application_loads_only_the_requirements_of_its_plan()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        var setup = await _service.GetSetupAsync(app.Id);

        Assert.Equal("basic", setup.Definition.PlanCode);
        Assert.Equal(1, setup.Definition.VersionNumber);
        var keys = setup.Definition.Sections.SelectMany(s => s.Fields).Select(f => f.FieldKey).ToList();
        Assert.Equal(new[] { "brand_name", "contact_email", "website", "run_ads", "ad_budget" }, keys);
        Assert.DoesNotContain("target_location", keys);
        Assert.Equal(ApplicationSetupStatus.NotStarted, setup.Application.SetupStatus);
    }

    [Fact]
    public async Task A_plan_that_does_not_need_a_section_never_shows_it()
    {
        await SeedPlansAsync();
        var basic = await _service.GetSetupAsync((await NewAppAsync(_basic)).Id);
        var leads = await _service.GetSetupAsync((await NewAppAsync(_leads)).Id);

        Assert.Equal(new[] { "business" }, basic.Definition.Sections.Select(s => s.Key));
        Assert.Equal(new[] { "business", "audience", "campaign" }, leads.Definition.Sections.Select(s => s.Key));
    }

    [Fact]
    public async Task Only_plans_with_a_published_setup_can_be_started()
    {
        await SeedPlansAsync();
        var bare = new Plan { Code = "bare", Name = "Bare", IsActive = true };
        _db.Plans.Add(bare);
        await _db.SaveChangesAsync();

        var available = await _service.GetAvailablePlansAsync();

        Assert.Equal(new[] { "basic", "leads" }, available.Select(p => p.Code).OrderBy(c => c));
        await Assert.ThrowsAsync<ConflictException>(() => NewAppAsync(bare));
    }

    // ---- AC3: mandatory validation ----------------------------------------------------------------------------------

    [Fact]
    public async Task AC3_Completing_with_a_mandatory_field_empty_is_refused_with_a_message_for_that_field()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        var result = await SaveAsync(app.Id, new { brand_name = "ABC", run_ads = "no" }, complete: true);

        Assert.False(result.Completed);
        Assert.Equal(ApplicationSetupStatus.InProgress, result.Setup.Application.SetupStatus);
        var missing = Assert.Single(result.Setup.Evaluation.Missing);
        Assert.Equal("contact_email", missing.FieldKey);
        Assert.Contains("required", missing.Message);
    }

    [Fact]
    public async Task A_wrong_format_blocks_completion_with_a_message_next_to_the_field()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        var result = await SaveAsync(app.Id, new { brand_name = "ABC", contact_email = "nope", run_ads = "no", website = "abc" }, complete: true);

        Assert.False(result.Completed);
        Assert.Equal(new[] { "contact_email", "website" }, result.Setup.Evaluation.Invalid.Select(i => i.FieldKey).OrderBy(k => k));
    }

    [Fact]
    public async Task Saving_a_key_the_plan_does_not_ask_is_rejected()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        await Assert.ThrowsAsync<ValidationException>(() => SaveAsync(app.Id, new { target_location = "Mohali" }));
    }

    [Fact]
    public async Task Save_and_continue_keeps_partial_answers_and_moves_to_in_progress()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        var saved = await SaveAsync(app.Id, new { brand_name = "ABC" });
        var reopened = await _service.GetSetupAsync(app.Id);

        Assert.False(saved.Completed);
        Assert.Equal(1, saved.ChangedCount);
        Assert.Equal(ApplicationSetupStatus.InProgress, reopened.Application.SetupStatus);
        Assert.Equal("ABC", reopened.Values["brand_name"]);
    }

    // ---- AC4: conditional fields ------------------------------------------------------------------------------------

    [Fact]
    public async Task AC4_A_dependent_field_is_neither_shown_nor_required_until_its_condition_is_met()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        var no = await FillBasicAsync(app.Id);
        Assert.True(no.Completed);
        Assert.DoesNotContain("ad_budget", no.Setup.Evaluation.VisibleFieldKeys);

        var yes = await SaveAsync(app.Id, new { run_ads = "yes" });
        Assert.Contains("ad_budget", yes.Setup.Evaluation.VisibleFieldKeys);
        Assert.Equal("ad_budget", Assert.Single(yes.Setup.Evaluation.Missing).FieldKey);
    }

    // ---- AC5 / AC6: execution gate ----------------------------------------------------------------------------------

    [Fact]
    public async Task AC5_An_incomplete_application_cannot_run_and_says_what_is_outstanding()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await SaveAsync(app.Id, new { brand_name = "ABC" });

        var blocked = await Assert.ThrowsAsync<SetupIncompleteException>(() => _service.ExecuteAsync(app.Id));

        Assert.Equal("Please complete the required setup before running this application.", blocked.Message);
        Assert.Contains("contact email", blocked.Missing);
        Assert.Empty(await _service.GetExecutionsAsync(app.Id));
        Assert.Contains(
            (await _service.GetAuditAsync(app.Id, new PagedRequest())).Items,
            e => e.Action == SetupAuditAction.ExecutionBlocked);
    }

    [Fact]
    public async Task AC5_Filled_in_but_never_confirmed_still_cannot_run()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await FillBasicAsync(app.Id, complete: false);

        await Assert.ThrowsAsync<SetupIncompleteException>(() => _service.ExecuteAsync(app.Id));
    }

    [Fact]
    public async Task AC6_A_complete_application_runs_with_a_frozen_copy_of_only_the_answers_that_applied()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await SaveAsync(app.Id, new { brand_name = "ABC", contact_email = "o@abc.test", run_ads = "no", ad_budget = 999 }, complete: true);

        var run = await _service.ExecuteAsync(app.Id);

        Assert.Equal("Basic Social v1", run.PlanVersionLabel);
        var stored = await _db.ApplicationExecutions.SingleAsync();
        var snapshot = JsonSerializer.Deserialize<Dictionary<string, string?>>(stored.SetupSnapshotJson)!;
        Assert.Equal("ABC", snapshot["brand_name"]);
        Assert.False(snapshot.ContainsKey("ad_budget")); // hidden by "no ads", so not part of the run
        Assert.Equal(1, (await _service.GetApplicationAsync(app.Id)).ExecutionCount);
    }

    [Fact]
    public async Task Editing_after_a_run_does_not_change_that_run_but_applies_to_the_next_one()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await FillBasicAsync(app.Id);
        await _service.ExecuteAsync(app.Id);

        await SaveAsync(app.Id, new { brand_name = "Renamed Co" });
        await _service.ExecuteAsync(app.Id);

        var runs = await _db.ApplicationExecutions.OrderBy(e => e.StartedAt).ThenBy(e => e.CreatedAt).ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Contains("ABC Software", runs.Single(r => r.SetupSnapshotJson.Contains("ABC Software")).SetupSnapshotJson);
        Assert.Contains("Renamed Co", runs.Single(r => r.SetupSnapshotJson.Contains("Renamed Co")).SetupSnapshotJson);
    }

    // ---- Status rules (section 8 and 15) -----------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_completed_setup_keeps_it_completed_while_valid_and_marks_it_incomplete_when_not()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await FillBasicAsync(app.Id);

        var stillValid = await SaveAsync(app.Id, new { brand_name = "ABC Ltd" });
        Assert.Equal(ApplicationSetupStatus.Completed, stillValid.Setup.Application.SetupStatus);

        var broken = await SaveAsync(app.Id, new { contact_email = (string?)null });
        Assert.Equal(ApplicationSetupStatus.Incomplete, broken.Setup.Application.SetupStatus);
        Assert.False(broken.Setup.Application.CanExecute);
        await Assert.ThrowsAsync<SetupIncompleteException>(() => _service.ExecuteAsync(app.Id));

        var fixedAgain = await SaveAsync(app.Id, new { contact_email = "new@abc.test" });
        Assert.Equal(ApplicationSetupStatus.Completed, fixedAgain.Setup.Application.SetupStatus);
        await _service.ExecuteAsync(app.Id);
    }

    [Fact]
    public async Task A_setup_older_than_the_versions_validity_period_expires_until_it_is_confirmed_again()
    {
        var plan = new Plan { Code = "timed", Name = "Timed", IsActive = true };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();
        var draft = await _admin.CreateVersionAsync(plan.Id, new CreateSetupVersionRequest(null, null));
        await _admin.UpdateVersionAsync(draft.Id, new SaveSetupVersionRequest(null, 30));
        await _admin.AddRequirementAsync(draft.Id, Req("brand_name"));
        await _admin.PublishVersionAsync(draft.Id);

        var app = await NewAppAsync(plan);
        await SaveAsync(app.Id, new { brand_name = "ABC" }, complete: true);
        await _service.ExecuteAsync(app.Id);

        _clock.UtcNow = _clock.UtcNow.AddDays(31);
        Assert.Equal(ApplicationSetupStatus.Expired, (await _service.GetApplicationAsync(app.Id)).SetupStatus);
        await Assert.ThrowsAsync<SetupIncompleteException>(() => _service.ExecuteAsync(app.Id));

        var reconfirmed = await SaveAsync(app.Id, new { }, complete: true);
        Assert.Equal(ApplicationSetupStatus.Completed, reconfirmed.Setup.Application.SetupStatus);
        await _service.ExecuteAsync(app.Id);
    }

    // ---- AC7: plan change -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AC7_Changing_plan_keeps_the_answers_that_still_apply_and_asks_only_for_the_new_ones()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await SaveAsync(app.Id, new { brand_name = "ABC Software", contact_email = "o@abc.test", run_ads = "yes", ad_budget = 5000, website = "https://abc.test" }, complete: true);

        var moved = await _service.ChangePlanAsync(app.Id, new ChangePlanRequest(_leads.Id, "Growing"));

        Assert.Equal("leads", moved.Application.PlanCode);
        Assert.Equal("ABC Software", moved.Values["brand_name"]); // carried over
        Assert.Equal(ApplicationSetupStatus.RequiresUpdate, moved.Application.SetupStatus);
        Assert.Equal(new[] { "target_location", "lead_source" }, moved.Evaluation.Missing.Select(m => m.FieldKey));
        Assert.DoesNotContain("ad_budget", moved.Values.Keys); // not asked by the new plan, so ignored
        await Assert.ThrowsAsync<SetupIncompleteException>(() => _service.ExecuteAsync(app.Id));

        var done = await SaveAsync(app.Id, new { target_location = "Chandigarh", lead_source = new[] { "search" } }, complete: true);
        Assert.Equal(ApplicationSetupStatus.Completed, done.Setup.Application.SetupStatus);
    }

    [Fact]
    public async Task Moving_back_to_the_earlier_plan_restores_answers_that_had_been_set_aside()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await SaveAsync(app.Id, new { brand_name = "ABC", contact_email = "o@abc.test", run_ads = "yes", ad_budget = 5000 }, complete: true);

        await _service.ChangePlanAsync(app.Id, new ChangePlanRequest(_leads.Id, null));
        var back = await _service.ChangePlanAsync(app.Id, new ChangePlanRequest(_basic.Id, null));

        Assert.Equal(5000m, back.Values["ad_budget"]);
        Assert.Equal(ApplicationSetupStatus.Completed, back.Application.SetupStatus);
    }

    [Fact]
    public async Task A_plan_change_that_needs_nothing_new_leaves_a_completed_setup_completed()
    {
        await SeedPlansAsync();
        var narrow = new Plan { Code = "narrow", Name = "Narrow", IsActive = true };
        _db.Plans.Add(narrow);
        await _db.SaveChangesAsync();
        await PublishV1Async(narrow, Req("brand_name"));

        var app = await NewAppAsync(_basic);
        await FillBasicAsync(app.Id);

        var moved = await _service.ChangePlanAsync(app.Id, new ChangePlanRequest(narrow.Id, null));

        Assert.Equal(ApplicationSetupStatus.Completed, moved.Application.SetupStatus);
        await _service.ExecuteAsync(app.Id);
    }

    [Fact]
    public async Task Changing_to_the_same_plan_is_refused()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        await Assert.ThrowsAsync<ConflictException>(() => _service.ChangePlanAsync(app.Id, new ChangePlanRequest(_basic.Id, null)));
    }

    // ---- AC8 / AC10: versioning and admin-defined requirements ---------------------------------------------------------

    [Fact]
    public async Task AC8_AC10_A_new_version_reaches_new_applications_only_and_existing_ones_move_when_migrated()
    {
        await SeedPlansAsync();
        var existing = await NewAppAsync(_basic);
        await FillBasicAsync(existing.Id);

        // The admin adds a question to a new draft and publishes it as v2 - no UI or code change involved.
        var v1 = (await _admin.GetPlansAsync()).Single(p => p.PlanId == _basic.Id).Versions.Single();
        var draft = await _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, "Ask for a phone number"));
        Assert.Equal(2, draft.VersionNumber);
        await _admin.AddRequirementAsync(draft.Id, Req("contact_phone", SetupFieldType.Phone, order: 25));
        await _admin.PublishVersionAsync(draft.Id);

        var stale = await _service.GetSetupAsync(existing.Id);
        Assert.Equal(1, stale.Definition.VersionNumber);
        Assert.Equal(v1.Id, stale.Application.PlanSetupVersionId);
        Assert.True(stale.Application.NewerVersionAvailable);
        Assert.DoesNotContain(stale.Definition.Sections.SelectMany(s => s.Fields), f => f.FieldKey == "contact_phone");
        Assert.Equal(ApplicationSetupStatus.Completed, stale.Application.SetupStatus);
        await _service.ExecuteAsync(existing.Id); // v1 applications keep running on v1

        var fresh = await _service.GetSetupAsync((await NewAppAsync(_basic)).Id);
        Assert.Equal(2, fresh.Definition.VersionNumber);
        Assert.Contains(fresh.Definition.Sections.SelectMany(s => s.Fields), f => f.FieldKey == "contact_phone");

        var migrated = await _service.MigrateToLatestVersionAsync(existing.Id);
        Assert.Equal(2, migrated.Definition.VersionNumber);
        Assert.False(migrated.Application.NewerVersionAvailable);
        Assert.Equal(ApplicationSetupStatus.RequiresUpdate, migrated.Application.SetupStatus);
        Assert.Equal("contact_phone", Assert.Single(migrated.Evaluation.Missing).FieldKey);
        await Assert.ThrowsAsync<ConflictException>(() => _service.MigrateToLatestVersionAsync(existing.Id));
    }

    [Fact]
    public async Task A_published_version_is_frozen_and_only_one_draft_may_exist()
    {
        await SeedPlansAsync();
        var published = (await _admin.GetPlansAsync()).Single(p => p.PlanId == _basic.Id).Versions.Single();
        var field = (await _admin.GetVersionAsync(published.Id)).Fields.First();

        await Assert.ThrowsAsync<ConflictException>(() => _admin.AddRequirementAsync(published.Id, Req("extra")));
        await Assert.ThrowsAsync<ConflictException>(() => _admin.UpdateRequirementAsync(field.Id, Req(field.FieldKey)));
        await Assert.ThrowsAsync<ConflictException>(() => _admin.DeleteRequirementAsync(field.Id));
        await Assert.ThrowsAsync<ConflictException>(() => _admin.DeleteVersionAsync(published.Id));

        await _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, null));
        await Assert.ThrowsAsync<ConflictException>(() => _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, null)));
    }

    [Fact]
    public async Task Publishing_supersedes_the_previous_version_and_a_draft_can_be_discarded()
    {
        await SeedPlansAsync();
        var draft = await _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, null));
        await _admin.PublishVersionAsync(draft.Id);

        var statuses = (await _admin.GetPlansAsync()).Single(p => p.PlanId == _basic.Id).Versions.OrderBy(v => v.VersionNumber).Select(v => v.Status);
        Assert.Equal(new[] { SetupVersionStatus.Superseded, SetupVersionStatus.Published }, statuses);

        var next = await _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, null));
        await _admin.DeleteVersionAsync(next.Id);
        Assert.Equal(2, (await _admin.GetPlansAsync()).Single(p => p.PlanId == _basic.Id).Versions.Count);
    }

    [Fact]
    public async Task The_editor_rejects_unsafe_requirements()
    {
        await SeedPlansAsync();
        var draft = await _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, null));
        var version = await _admin.GetVersionAsync(draft.Id);
        var brand = version.Fields.Single(f => f.FieldKey == "brand_name");

        // Duplicate key, bad key, choice with no options, missing parent, self dependency.
        await Assert.ThrowsAsync<ConflictException>(() => _admin.AddRequirementAsync(draft.Id, Req("brand_name")));
        await Assert.ThrowsAsync<ValidationException>(() => _admin.AddRequirementAsync(draft.Id, Req("Bad Key!")));
        await Assert.ThrowsAsync<ValidationException>(() => _admin.AddRequirementAsync(draft.Id, Req("pick", SetupFieldType.Dropdown)));
        await Assert.ThrowsAsync<ValidationException>(() => _admin.AddRequirementAsync(draft.Id,
            Req("child", when: new SetupConditionDto("ghost", SetupConditionOperator.Equals, "x"))));
        await Assert.ThrowsAsync<ValidationException>(() => _admin.AddRequirementAsync(draft.Id,
            Req("loop", when: new SetupConditionDto("loop", SetupConditionOperator.Equals, "x"))));
        await Assert.ThrowsAsync<ValidationException>(() => _admin.AddRequirementAsync(draft.Id,
            Req("fee", SetupFieldType.Currency, required: false, def: "-5")));

        // A -> B -> A loop; and a field other fields depend on cannot be deleted or renamed.
        var parent = await _admin.AddRequirementAsync(draft.Id, Req("parent", required: false));
        await _admin.AddRequirementAsync(draft.Id, Req("kid", required: false, when: new SetupConditionDto("parent", SetupConditionOperator.NotEmpty, null)));
        await Assert.ThrowsAsync<ValidationException>(() => _admin.UpdateRequirementAsync(parent.Id,
            Req("parent", required: false, when: new SetupConditionDto("kid", SetupConditionOperator.NotEmpty, null))));
        await Assert.ThrowsAsync<ConflictException>(() => _admin.DeleteRequirementAsync(parent.Id));
        await Assert.ThrowsAsync<ConflictException>(() => _admin.UpdateRequirementAsync(parent.Id, Req("parent_renamed", required: false)));
        Assert.NotNull(brand);
    }

    [Fact]
    public async Task A_deactivated_requirement_disappears_from_the_wizard_without_deleting_it()
    {
        await SeedPlansAsync();
        var draft = await _admin.CreateVersionAsync(_basic.Id, new CreateSetupVersionRequest(null, null));
        var website = (await _admin.GetVersionAsync(draft.Id)).Fields.Single(f => f.FieldKey == "website");

        await _admin.UpdateRequirementAsync(website.Id, Req("website", SetupFieldType.Url, required: false, order: 30) with { IsActive = false });

        var preview = await _admin.GetPreviewAsync(draft.Id);
        Assert.DoesNotContain(preview.Sections.SelectMany(s => s.Fields), f => f.FieldKey == "website");
        Assert.Contains((await _admin.GetVersionAsync(draft.Id)).Fields, f => f.FieldKey == "website" && !f.IsActive);
    }

    [Fact]
    public async Task A_version_with_no_active_fields_cannot_be_published()
    {
        var plan = new Plan { Code = "empty", Name = "Empty", IsActive = true };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();
        var draft = await _admin.CreateVersionAsync(plan.Id, new CreateSetupVersionRequest(null, null));

        await Assert.ThrowsAsync<ValidationException>(() => _admin.PublishVersionAsync(draft.Id));
    }

    // ---- AC9: audit ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AC9_Every_change_is_recorded_with_who_when_previous_new_reason_and_plan_version()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await SaveAsync(app.Id, new { brand_name = "ABC" });
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await SaveAsync(app.Id, new { brand_name = "ABC Software" }, reason: "Rebrand");
        await SaveAsync(app.Id, new { brand_name = "ABC Software" }); // unchanged: nothing recorded

        var audit = (await _service.GetAuditAsync(app.Id, new PagedRequest())).Items;

        var change = audit.Single(e => e.Action == SetupAuditAction.ValueSet && e.PreviousValue == "ABC");
        Assert.Equal("ABC Software", change.NewValue);
        Assert.Equal("brand_name", change.FieldKey);
        Assert.Equal("brand name", change.FieldLabel);
        Assert.Equal("Rebrand", change.Reason);
        Assert.Equal("Basic Social v1", change.PlanVersionLabel);
        Assert.Equal(_user, change.PerformedBy);
        Assert.Equal(_clock.UtcNow, change.PerformedAt);
        Assert.Equal(1, audit.Count(e => e.Action == SetupAuditAction.ValueSet && e.FieldKey == "brand_name" && e.PreviousValue is null));
        Assert.Contains(audit, e => e.Action == SetupAuditAction.ApplicationCreated);
    }

    [Fact]
    public async Task Clearing_completion_plan_change_migration_and_execution_all_land_in_the_trail()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);
        await FillBasicAsync(app.Id);
        await SaveAsync(app.Id, new { website = "https://abc.test" });
        await SaveAsync(app.Id, new { website = (string?)null });
        await _service.ExecuteAsync(app.Id);
        await _service.ChangePlanAsync(app.Id, new ChangePlanRequest(_leads.Id, "Upgrade"));

        var actions = (await _service.GetAuditAsync(app.Id, new PagedRequest { PageSize = 100 })).Items.Select(e => e.Action).ToList();

        Assert.Contains(SetupAuditAction.SetupCompleted, actions);
        Assert.Contains(SetupAuditAction.ValueCleared, actions);
        Assert.Contains(SetupAuditAction.Executed, actions);
        Assert.Contains(SetupAuditAction.PlanChanged, actions);
    }

    // ---- Housekeeping -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Defaults_count_as_answers_until_replaced()
    {
        var plan = new Plan { Code = "defaults", Name = "Defaults", IsActive = true };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();
        await PublishV1Async(plan, Req("delay_hours", SetupFieldType.Number, def: "24"), Req("brand_name"));
        var app = await NewAppAsync(plan);

        var setup = await _service.GetSetupAsync(app.Id);
        Assert.Equal(24m, setup.Values["delay_hours"]);
        Assert.Equal(ApplicationSetupStatus.NotStarted, setup.Application.SetupStatus); // a default is not the Talent's work
        Assert.Equal("brand_name", Assert.Single(setup.Evaluation.Missing).FieldKey);
    }

    [Fact]
    public async Task Application_names_are_unique_per_tenant_and_defaulted_from_the_plan()
    {
        await SeedPlansAsync();

        var first = await NewAppAsync(_basic);
        var second = await NewAppAsync(_basic);

        Assert.Equal("Basic Social", first.Name);
        Assert.Equal("Basic Social (2)", second.Name);
        await Assert.ThrowsAsync<ConflictException>(() => NewAppAsync(_basic, "basic social"));
    }

    [Fact]
    public async Task The_projection_follows_the_tagged_answers_of_the_plan()
    {
        var plan = new Plan { Code = "roi", Name = "ROI", IsActive = true };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();
        await PublishV1Async(plan,
            Req("price", SetupFieldType.Currency, metric: SetupMetrics.PackagePrice),
            Req("customers", SetupFieldType.Number, metric: SetupMetrics.ExpectedCustomers),
            Req("budget", SetupFieldType.Currency, metric: SetupMetrics.MarketingCost));
        var app = await NewAppAsync(plan);

        var saved = await SaveAsync(app.Id, new { price = 5000, customers = 10, budget = 20000 });

        var projection = saved.Setup.Projection;
        Assert.Equal(50000m, projection.ExpectedRevenue);
        Assert.Equal(30000m, projection.ExpectedProfit);
        Assert.Equal(150m, projection.RoiPercent);
    }

    [Fact]
    public async Task One_tenants_application_is_invisible_to_another()
    {
        await SeedPlansAsync();
        var app = await NewAppAsync(_basic);

        var other = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        await using var otherDb = new SqliteApplicationDbContext(options, new Ambient(other), new User(Guid.NewGuid())) { StampTenantId = other };
        var otherService = new ApplicationSetupService(otherDb, new Ambient(other), new User(Guid.NewGuid()), _clock);

        await Assert.ThrowsAsync<NotFoundException>(() => otherService.GetSetupAsync(app.Id));
        Assert.Empty(await otherService.GetApplicationsAsync());
    }

    [Fact]
    public async Task The_list_reports_status_percent_and_whether_it_can_run()
    {
        await SeedPlansAsync();
        var ready = await NewAppAsync(_basic);
        await FillBasicAsync(ready.Id);
        await NewAppAsync(_leads);

        var list = await _service.GetApplicationsAsync();

        var done = list.Single(a => a.Id == ready.Id);
        Assert.Equal(ApplicationSetupStatus.Completed, done.SetupStatus);
        Assert.Equal(100, done.SetupPercent);
        Assert.True(done.CanExecute);
        var fresh = list.Single(a => a.PlanCode == "leads");
        Assert.Equal(ApplicationSetupStatus.NotStarted, fresh.SetupStatus);
        Assert.False(fresh.CanExecute);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;

        public Guid? TenantId { get; private set; }

        public bool IsPlatformSuperAdmin => false;

        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class User : ICurrentUserService
    {
        public User(Guid id) => UserId = id;

        public Guid? UserId { get; }

        public string? Email => "talent@test";

        public IReadOnlyList<string> Roles => new[] { "Admin" };

        public Guid? TenantId => null;

        public Guid? ImpersonatorUserId => null;
    }
}
