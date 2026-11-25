using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Simulator;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Verification;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Simulator dummy data page: the admin's rows are what the simulators use, and add/edit/delete/restore keep them in hand.</summary>
public sealed class SimulatorDummyDataTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly SimulatorDataProvider _provider;
    private readonly SimulatorDummyDataService _service;
    private readonly Guid _actor = Guid.NewGuid();

    public SimulatorDummyDataTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _provider = new SimulatorDataProvider(_db, new MemoryCache(new MemoryCacheOptions()), NullLogger<SimulatorDataProvider>.Instance);
        _service = new SimulatorDummyDataService(_db, new PlatformAuditService(_db), _provider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveSimulatorDummyRecordRequest Business(string name, string phone, SimulatorLeadVariant variant = SimulatorLeadVariant.Qualified) =>
        new(SimulatorDataCategory.LeadBusiness, name, null, "Dental clinic", "Dr. Test", "SCO 1, Sector 9", "Mohali", phone, "hi@test-clinic.example",
            "https://test-clinic.example", 81, variant, true, 1);

    private static SaveSimulatorDummyRecordRequest Keyed(SimulatorDataCategory category, string key, string value) =>
        new(category, key, value, null, null, null, null, null, null, null, null, null, true, 0);

    private static LeadDiscoveryAgentRequest Ask(int count) => new(
        "dental clinic", new[] { "dentist" }, new[] { "Chandigarh" }, count, Array.Empty<string>(), true, false, false, 60, Array.Empty<string>(), Array.Empty<string>());

    private async Task Set(string key, string value) =>
        await _service.CreateAsync(Keyed(SimulatorDataCategory.Setting, key, value), _actor, "a@b.c");

    [Fact]
    public async Task Lead_discovery_hands_back_the_admins_businesses_exactly_when_unique_per_run_is_off()
    {
        await _service.CreateAsync(Business("Test Dental One", "+91 98140 55501"), _actor, "a@b.c");
        await _service.CreateAsync(Business("Test Dental Two", "+91 98140 55502"), _actor, "a@b.c");
        await Set(SimulatorDataCatalog.Settings.UniquePerRun, "false");

        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance, _provider);
        var result = await agent.DiscoverAsync(Ask(10));

        // Only the two rows there are - nothing invented, nothing repeated.
        Assert.Equal(new[] { "[Simulated] Test Dental One", "[Simulated] Test Dental Two" }, result.Candidates.Select(c => c.BusinessName));
        Assert.Equal("+91 98140 55501", result.Candidates[0].Phone);
        Assert.Equal("Mohali", result.Candidates[0].City);
        Assert.Equal(81, result.Candidates[0].LeadScore);

        var assessed = LeadQualification.AssessAll(result.Candidates, result.Evidence,
            new QualificationRules(PhoneRequired: true, EmailRequired: true, IndependentBusiness: false, MinimumLeadScore: 60, RequiredFields: Array.Empty<string>()));
        Assert.All(assessed, a => Assert.NotNull(a.Lead));
    }

    [Fact]
    public async Task With_unique_per_run_on_the_rows_are_reused_with_a_run_tag_so_a_rerun_is_not_all_duplicates()
    {
        await _service.CreateAsync(Business("Only Clinic", "+91 98140 55501"), _actor, "a@b.c");

        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance, _provider);
        var result = await agent.DiscoverAsync(Ask(3));

        Assert.Equal(3, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.StartsWith("[Simulated] Only Clinic ", c.BusinessName));
        Assert.Equal(3, result.Candidates.Select(c => c.BusinessName).Distinct().Count());
        Assert.All(result.Candidates, c => Assert.StartsWith("+91 98140 5", c.Phone));
    }

    [Fact]
    public async Task A_business_whose_phone_is_on_the_dnd_list_keeps_that_number_even_with_unique_per_run_on()
    {
        await _service.CreateAsync(Business("Dnd Clinic", "+91 98140 55501"), _actor, "a@b.c");
        await _service.CreateAsync(new SaveSimulatorDummyRecordRequest(SimulatorDataCategory.DndNumber, "+919814055501", null,
            null, null, null, null, null, null, null, null, null, true, 0), _actor, "a@b.c");

        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance, _provider);
        var result = await agent.DiscoverAsync(Ask(2));

        Assert.All(result.Candidates, c => Assert.Equal("+91 98140 55501", c.Phone));
        var dnd = await new SimulatedPhoneVerificationProvider(_provider).VerifyAsync(Guid.NewGuid(),
            new[] { new PhoneVerificationItem(Guid.NewGuid(), "+919814055501") });
        Assert.Equal(LeadDiscoveryVerificationStatus.Fail, dnd.Results.Single().Status);
    }

    [Fact]
    public async Task An_inactive_or_deleted_business_is_not_used()
    {
        var off = Business("Switched Off Clinic", "+91 98140 55509") with { IsActive = false };
        await _service.CreateAsync(off, _actor, "a@b.c");
        var gone = await _service.CreateAsync(Business("Deleted Clinic", "+91 98140 55508"), _actor, "a@b.c");
        await _service.DeleteAsync(gone.Id, _actor, "a@b.c");

        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance, _provider);
        var result = await agent.DiscoverAsync(Ask(5));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task An_edit_is_used_on_the_very_next_call()
    {
        var row = await _service.CreateAsync(Business("Before Edit", "+91 98140 55501"), _actor, "a@b.c");
        await Set(SimulatorDataCatalog.Settings.UniquePerRun, "false");
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance, _provider);
        Assert.Equal("[Simulated] Before Edit", (await agent.DiscoverAsync(Ask(1))).Candidates.Single().BusinessName);

        await _service.UpdateAsync(row.Id, Business("After Edit", "+91 98140 55501"), _actor, "a@b.c");

        Assert.Equal("[Simulated] After Edit", (await agent.DiscoverAsync(Ask(1))).Candidates.Single().BusinessName);
    }

    [Fact]
    public async Task Task_answers_come_from_the_dummy_data_and_stay_marked_as_test_data()
    {
        await _service.CreateAsync(Keyed(SimulatorDataCategory.TaskAnswer, "answer", "Hamara clinic 9 se 6 khula hai."), _actor, "a@b.c");

        var reply = SimulatedTaskEngine.Respond("product-answer", AiSimulatorScenarios.Success, expectJson: true, await _provider.GetAsync());

        Assert.Contains($"{SimulatedTaskEngine.Marker} Hamara clinic 9 se 6 khula hai.", reply);
    }

    [Fact]
    public async Task A_keyed_value_that_is_missing_falls_back_to_the_built_in_one()
    {
        var data = await _provider.GetAsync();   // empty table

        Assert.Equal(SimulatorDataCatalog.Find(SimulatorDataCategory.AiReply, SimulatorDataCatalog.Replies.Default)!.DefaultValue,
            data.Reply(SimulatorDataCatalog.Replies.Default));
        Assert.Contains("refund", data.Keywords(SimulatorDataCatalog.Intents.Complaint));
        Assert.Empty(data.Businesses);
    }

    [Fact]
    public async Task A_listed_dnd_number_is_rejected_and_the_digit_rule_can_be_switched_off()
    {
        await _service.CreateAsync(new SaveSimulatorDummyRecordRequest(SimulatorDataCategory.DndNumber, "+91 98140 55501", null,
            null, null, null, null, null, null, null, null, null, true, 0), _actor, "a@b.c");
        await Set(SimulatorDataCatalog.Settings.DndDigitSumRule, "false");

        var dnd = new SimulatedPhoneVerificationProvider(_provider);
        var listed = new PhoneVerificationItem(Guid.NewGuid(), "+919814055501");
        var digitSumMultipleOfFour = new PhoneVerificationItem(Guid.NewGuid(), "+919800000001");   // 9+1+9+8+1 = 28, DND under the digit rule

        var result = await dnd.VerifyAsync(Guid.NewGuid(), new[] { listed, digitSumMultipleOfFour });

        Assert.Equal(LeadDiscoveryVerificationStatus.Fail, result.Results.Single(r => r.ReferenceId == listed.ReferenceId).Status);
        Assert.Equal(LeadDiscoveryVerificationStatus.Pass, result.Results.Single(r => r.ReferenceId == digitSumMultipleOfFour.ReferenceId).Status);
    }

    [Fact]
    public async Task A_keyed_row_must_use_a_listed_key_and_a_key_cannot_be_added_twice()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Keyed(SimulatorDataCategory.AiReply, "NotAKey", "x"), _actor, "a@b.c"));

        await _service.CreateAsync(Keyed(SimulatorDataCategory.AiReply, "default", "Namaste!"), _actor, "a@b.c");
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Keyed(SimulatorDataCategory.AiReply, "Default", "Again"), _actor, "a@b.c"));

        var stored = (await _service.GetAllAsync()).Records.Single();
        Assert.Equal(SimulatorDataCatalog.Replies.Default, stored.Name);   // kept in the catalog spelling
    }

    [Fact]
    public async Task Settings_are_checked()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Set(SimulatorDataCatalog.Settings.FlawedSharePercent, "95"));
        await Assert.ThrowsAsync<ValidationException>(() => Set(SimulatorDataCatalog.Settings.UniquePerRun, "maybe"));
        await Set(SimulatorDataCatalog.Settings.FlawedSharePercent, "50");
    }

    [Fact]
    public async Task Restore_defaults_adds_back_only_the_missing_starter_rows()
    {
        var first = await _service.RestoreDefaultsAsync(_actor, "a@b.c");
        Assert.Equal(SimulatorDataCatalog.DefaultRecords().Count, first.Added);

        var all = await _service.GetAllAsync();
        var edited = all.Records.First(r => r.Category == SimulatorDataCategory.LeadBusiness);
        await _service.UpdateAsync(edited.Id, Business(edited.Name, "+91 98140 99999"), _actor, "a@b.c");
        var deleted = all.Records.First(r => r.Category == SimulatorDataCategory.AiReply);
        await _service.DeleteAsync(deleted.Id, _actor, "a@b.c");

        var second = await _service.RestoreDefaultsAsync(_actor, "a@b.c");

        Assert.Equal(1, second.Added);
        Assert.Equal("+91 98140 99999", (await _db.SimulatorDummyRecords.SingleAsync(r => r.Id == edited.Id)).Phone);   // the edit is kept
    }
}
