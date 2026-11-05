using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.Settings;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The story studio settings the platform operator edits must exist in the catalog and bind to the options the services read.</summary>
public sealed class StorySettingsTests
{
    private static readonly IReadOnlyList<AppSettingDefinition> Story =
        AppSettingCatalog.All.Where(d => d.Key.StartsWith("ContentStudio:Cinematic:")).ToList();

    [Fact]
    public void Every_option_the_services_read_can_be_set_from_the_console()
    {
        var keys = Story.Select(d => d.Key.Split(':')[2]).ToHashSet();

        foreach (var property in typeof(CinematicOptions).GetProperties())
            Assert.True(keys.Contains(property.Name), $"{property.Name} has no setting in the catalog, so the console cannot set it.");
    }

    [Fact]
    public void There_is_no_second_place_to_enter_a_provider_key()
    {
        // Veo uses the Gemini key and pictures/voice the ChatGPT key saved under Configuration > AI Providers.
        Assert.DoesNotContain(Story, d => d.IsSecret);
        Assert.DoesNotContain(Story, d => d.Key.EndsWith("ApiKey"));
        Assert.Empty(typeof(CinematicOptions).GetProperties().Where(p => p.Name.EndsWith("ApiKey")));
    }

    [Fact]
    public void The_story_script_has_its_own_switch_on_the_integrations_page()
    {
        var group = WhatsAppSalesAutomation.Application.Platform.AiFeatures.ProductTaskGroups.Single(g => g.Feature == WhatsAppSalesAutomation.Application.Platform.AiFeatures.StoryScript);

        Assert.Equal("Story script", group.Title);
        Assert.Contains(WhatsAppSalesAutomation.Application.Ai.Tasks.AiTaskKinds.CinematicScript, group.Tasks);
        Assert.Equal(WhatsAppSalesAutomation.Application.Platform.AiFeatures.StoryScript, WhatsAppSalesAutomation.Application.Platform.AiFeatures.GroupOf(WhatsAppSalesAutomation.Application.Ai.Tasks.AiTaskKinds.CinematicScript));
        Assert.Equal(new[] { "OpenAI", "Anthropic", "Google" }, WhatsAppSalesAutomation.Application.Platform.AiFeatures.SupportedProviders(WhatsAppSalesAutomation.Application.Platform.AiFeatures.StoryScript));
        Assert.Equal("Simulator", WhatsAppSalesAutomation.Application.Platform.AiFeatures.DefaultMode(WhatsAppSalesAutomation.Application.Platform.AiFeatures.StoryScript));
    }

    [Fact]
    public void Every_other_setting_has_a_default_and_a_default_that_is_one_of_its_options()
    {
        foreach (var d in Story.Where(d => !d.IsSecret))
        {
            // Paths and the font settings are empty on purpose: empty means "use the host's own".
            if (d.Key.EndsWith("Path") || d.Key.EndsWith("FontsDirectory") || d.Key.EndsWith("FontName")) continue;
            Assert.False(string.IsNullOrWhiteSpace(d.DefaultValue), d.Key);
            if (d.Options is not null) Assert.Contains(d.DefaultValue, d.Options);
        }
    }

    [Fact]
    public void Saved_values_reach_the_options_the_pipeline_reads()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ContentStudio:Cinematic:SpeechProvider"] = "OpenAI",
            ["ContentStudio:Cinematic:VeoUsdPerSecond"] = "0.40",
            ["ContentStudio:Cinematic:SubtitleFontName"] = "Noto Sans Devanagari"
        }).Build();

        var options = config.GetSection("ContentStudio:Cinematic").Get<CinematicOptions>()!;

        Assert.Equal("OpenAI", options.SpeechProvider);
        Assert.Equal(0.40m, options.VeoUsdPerSecond);
        Assert.Equal("Noto Sans Devanagari", options.SubtitleFontName);
    }
}
