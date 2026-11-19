using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Discovery settings: how hard one lead discovery run may work, managed by a platform admin from Settings > Tenant Settings.
/// Each carries a default that matches the code's own, and a range that a save cannot step outside.</summary>
public sealed class DiscoverySettingsCatalogTests
{
    private sealed class FakeStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Saved { get; } = new();

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Saved);

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values) Saved[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static readonly IReadOnlyList<AppSettingDefinition> Discovery = AppSettingCatalog.All.Where(d => d.Category == "Discovery").ToList();

    private static Task Save(SettingsService service, string key, string? value) =>
        service.UpdateCategoryAsync("Discovery", new UpdateSettingsRequest(new() { [key] = value }), null);

    [Fact]
    public void The_Discovery_category_exists_and_holds_exactly_the_eight_limits()
    {
        Assert.Contains("Discovery", AppSettingCatalog.Categories);
        Assert.Equal(
            new[]
            {
                "LeadDiscovery:Agent:MaxFetchesPerRound", "LeadDiscovery:Agent:MaxSearchesPerRound", "LeadDiscovery:Agent:MaxTurns",
                "LeadDiscovery:CandidateHeadroomFactor", "LeadDiscovery:InitialRejectionRatePercent", "LeadDiscovery:MaxCandidatesPerRound",
                "LeadDiscovery:MaxKnownBusinessesInPrompt", "LeadDiscovery:MaxRounds"
            },
            Discovery.Select(d => d.Key).Order().ToArray());
    }

    [Fact]
    public void None_of_them_is_a_secret_a_list_or_a_dropdown()
    {
        Assert.All(Discovery, d =>
        {
            Assert.False(d.IsSecret, d.Key);
            Assert.False(d.IsList, d.Key);
            Assert.Null(d.Options);
            Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Key);
        });
    }

    [Fact]
    public void Every_default_is_inside_its_own_range_so_the_seeded_value_is_always_one_a_save_would_accept()
    {
        Assert.All(Discovery, d =>
        {
            Assert.NotNull(d.MinValue);
            Assert.NotNull(d.MaxValue);
            var value = int.Parse(d.DefaultValue!);
            Assert.InRange(value, d.MinValue!.Value, d.MaxValue!.Value);
        });
    }

    [Fact]
    public void The_defaults_are_the_ones_the_code_already_runs_on_so_moving_the_keys_changes_nothing_until_an_admin_does()
    {
        var pacing = new LeadDiscoveryOptions();
        var agent = new LeadDiscoveryAgentSettings();
        string Default(string key) => Discovery.Single(d => d.Key == key).DefaultValue!;

        Assert.Equal(pacing.MaxRounds.ToString(), Default("LeadDiscovery:MaxRounds"));
        Assert.Equal(pacing.CandidateHeadroomFactor.ToString(), Default("LeadDiscovery:CandidateHeadroomFactor"));
        Assert.Equal(pacing.InitialRejectionRatePercent.ToString(), Default("LeadDiscovery:InitialRejectionRatePercent"));
        Assert.Equal(pacing.MaxCandidatesPerRound.ToString(), Default("LeadDiscovery:MaxCandidatesPerRound"));
        Assert.Equal(pacing.MaxKnownBusinessesInPrompt.ToString(), Default("LeadDiscovery:MaxKnownBusinessesInPrompt"));
        Assert.Equal(agent.MaxTurns.ToString(), Default("LeadDiscovery:Agent:MaxTurns"));
        Assert.Equal(agent.MaxSearchesPerRound.ToString(), Default("LeadDiscovery:Agent:MaxSearchesPerRound"));
        Assert.Equal(agent.MaxFetchesPerRound.ToString(), Default("LeadDiscovery:Agent:MaxFetchesPerRound"));
    }

    [Fact]
    public void Saved_values_bind_to_the_options_the_run_and_the_agents_read()
    {
        // As the database-backed configuration presents them.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LeadDiscovery:MaxRounds"] = "9",
            ["LeadDiscovery:CandidateHeadroomFactor"] = "1",
            ["LeadDiscovery:InitialRejectionRatePercent"] = "45",
            ["LeadDiscovery:MaxCandidatesPerRound"] = "12",
            ["LeadDiscovery:MaxKnownBusinessesInPrompt"] = "50",
            ["LeadDiscovery:Agent:MaxTurns"] = "8",
            ["LeadDiscovery:Agent:MaxSearchesPerRound"] = "7",
            ["LeadDiscovery:Agent:MaxFetchesPerRound"] = "30",
        }).Build();

        var pacing = configuration.GetSection("LeadDiscovery").Get<LeadDiscoveryOptions>()!;
        var agent = configuration.GetSection("LeadDiscovery:Agent").Get<LeadDiscoveryAgentSettings>()!;

        Assert.Equal((9, 1, 45, 12, 50), (pacing.MaxRounds, pacing.CandidateHeadroomFactor, pacing.InitialRejectionRatePercent, pacing.MaxCandidatesPerRound, pacing.MaxKnownBusinessesInPrompt));
        Assert.Equal((8, 7, 30), (agent.MaxTurns, agent.MaxSearchesPerRound, agent.MaxFetchesPerRound));
    }

    [Fact]
    public async Task The_console_lists_all_eight_with_their_defaults_and_descriptions()
    {
        var store = new FakeStore();
        foreach (var d in Discovery)
            store.Saved[d.Key] = d.DefaultValue;

        var category = await new SettingsService(store).GetCategoryAsync("Discovery");

        Assert.Equal(8, category.Items.Count);
        Assert.Equal("6", category.Items.Single(i => i.Key == "LeadDiscovery:MaxRounds").Value);
        Assert.Contains("cost", category.Items.Single(i => i.Key == "LeadDiscovery:MaxRounds").Description!);
    }

    [Theory]
    [InlineData("LeadDiscovery:MaxRounds", "10")]
    [InlineData("LeadDiscovery:MaxRounds", "1")]
    [InlineData("LeadDiscovery:MaxRounds", "20")]
    [InlineData("LeadDiscovery:CandidateHeadroomFactor", "1")]
    [InlineData("LeadDiscovery:MaxKnownBusinessesInPrompt", "0")]
    [InlineData("LeadDiscovery:InitialRejectionRatePercent", "0")]
    [InlineData("LeadDiscovery:InitialRejectionRatePercent", "90")]
    [InlineData("LeadDiscovery:Agent:MaxFetchesPerRound", "100")]
    [InlineData("LeadDiscovery:Agent:MaxTurns", " 8 ")]
    public async Task A_whole_number_inside_the_range_is_saved(string key, string value)
    {
        var store = new FakeStore();

        await Save(new SettingsService(store), key, value);

        Assert.Equal(value, store.Saved[key]);
    }

    [Theory]
    [InlineData("LeadDiscovery:MaxRounds", "0")]            // below the range
    [InlineData("LeadDiscovery:MaxRounds", "21")]           // above it - the limit on what a run can cost
    [InlineData("LeadDiscovery:MaxRounds", "-1")]
    [InlineData("LeadDiscovery:MaxRounds", "abc")]          // a typo that would break the options binding
    [InlineData("LeadDiscovery:MaxRounds", "3.5")]
    [InlineData("LeadDiscovery:MaxRounds", "1e2")]
    [InlineData("LeadDiscovery:CandidateHeadroomFactor", "0")]
    [InlineData("LeadDiscovery:CandidateHeadroomFactor", "6")]
    [InlineData("LeadDiscovery:InitialRejectionRatePercent", "91")]   // 100% would ask for infinitely many
    [InlineData("LeadDiscovery:InitialRejectionRatePercent", "-5")]
    [InlineData("LeadDiscovery:MaxCandidatesPerRound", "51")]
    [InlineData("LeadDiscovery:MaxKnownBusinessesInPrompt", "1001")]
    [InlineData("LeadDiscovery:Agent:MaxTurns", "1")]
    [InlineData("LeadDiscovery:Agent:MaxSearchesPerRound", "51")]
    [InlineData("LeadDiscovery:Agent:MaxFetchesPerRound", "101")]
    public async Task A_value_outside_the_range_or_not_a_whole_number_is_refused_and_nothing_is_saved(string key, string value)
    {
        var store = new FakeStore();

        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Save(new SettingsService(store), key, value));

        Assert.Contains("whole number from", error.Errors.Single().ErrorMessage);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task One_bad_value_in_a_save_stops_the_whole_save()
    {
        var store = new FakeStore();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            new SettingsService(store).UpdateCategoryAsync("Discovery", new UpdateSettingsRequest(new()
            {
                ["LeadDiscovery:MaxRounds"] = "8",
                ["LeadDiscovery:Agent:MaxTurns"] = "99",
            }), null));

        Assert.Empty(store.Saved);   // not even the good one
    }

    [Fact]
    public async Task An_empty_value_is_accepted_and_clears_the_setting_so_the_built_in_default_applies()
    {
        var store = new FakeStore();

        await Save(new SettingsService(store), "LeadDiscovery:MaxRounds", "");

        Assert.Equal("", store.Saved["LeadDiscovery:MaxRounds"]);
    }

    [Fact]
    public async Task A_discovery_key_cannot_be_saved_under_another_category()
    {
        await Assert.ThrowsAsync<WhatsAppSalesAutomation.Application.Common.Exceptions.NotFoundException>(() =>
            new SettingsService(new FakeStore()).UpdateCategoryAsync("Campaigns", new UpdateSettingsRequest(new() { ["LeadDiscovery:MaxRounds"] = "6" }), null));
    }

    [Fact]
    public void Settings_that_have_no_range_are_unaffected_by_the_new_check()
    {
        // The existing text settings stay free-form: only a definition that declares a range is validated as a number.
        Assert.All(AppSettingCatalog.All.Where(d => d.Category != "Discovery"), d =>
        {
            Assert.Null(d.MinValue);
            Assert.Null(d.MaxValue);
        });
    }
}
