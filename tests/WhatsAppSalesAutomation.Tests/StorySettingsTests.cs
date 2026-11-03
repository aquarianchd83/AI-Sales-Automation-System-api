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
    public void The_keys_are_secret_and_have_no_default()
    {
        foreach (var name in new[] { "GeminiApiKey", "OpenAiApiKey" })
        {
            var setting = Story.Single(d => d.Key == "ContentStudio:Cinematic:" + name);
            Assert.True(setting.IsSecret);
            Assert.True(string.IsNullOrEmpty(setting.DefaultValue));
        }
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
            ["ContentStudio:Cinematic:GeminiApiKey"] = "gemini-key",
            ["ContentStudio:Cinematic:OpenAiApiKey"] = "openai-key",
            ["ContentStudio:Cinematic:SpeechProvider"] = "OpenAI",
            ["ContentStudio:Cinematic:VeoUsdPerSecond"] = "0.40",
            ["ContentStudio:Cinematic:SubtitleFontName"] = "Noto Sans Devanagari"
        }).Build();

        var options = config.GetSection("ContentStudio:Cinematic").Get<CinematicOptions>()!;

        Assert.Equal("gemini-key", options.GeminiApiKey);
        Assert.Equal("openai-key", options.OpenAiApiKey);
        Assert.Equal("OpenAI", options.SpeechProvider);
        Assert.Equal(0.40m, options.VeoUsdPerSecond);
        Assert.Equal("Noto Sans Devanagari", options.SubtitleFontName);
    }
}
