using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The On/Off switch per AI provider on Configuration > AI Providers: Off means the provider is not used anywhere, whatever else is saved.</summary>
public sealed class AiProviderSwitchTests
{
    private sealed class Snapshot : IOptionsSnapshot<AiProviderSettings>
    {
        public Snapshot(AiProviderSettings value) => Value = value;
        public AiProviderSettings Value { get; }
        public AiProviderSettings Get(string? name) => Value;
    }

    private static AiProviderSettings Settings(string chat = "OpenAI", string embedding = "OpenAI") => new()
    {
        Provider = chat,
        EmbeddingProvider = embedding,
        Anthropic = { ApiKey = "sk-claude" },
        OpenAI = { ApiKey = "sk-openai" },
        Google = { ApiKey = "g-key" },
    };

    private static AiCredentials Credentials(AiProviderSettings s) => new PlatformAiConfigProvider(new Snapshot(s)).Get();

    [Fact]
    public void Every_provider_is_On_until_the_admin_switches_it_off()
    {
        var c = Credentials(Settings());

        Assert.True(c.AnthropicEnabled && c.OpenAiEnabled && c.GoogleEnabled);
        Assert.Equal("sk-openai", c.OpenAiApiKey);
        Assert.Equal("OpenAI", c.Provider);
    }

    [Fact]
    public void An_Off_provider_has_no_key_for_any_caller_and_the_others_are_untouched()
    {
        var s = Settings();
        s.OpenAI.Enabled = false;

        var c = Credentials(s);

        Assert.Null(c.OpenAiApiKey);
        Assert.False(c.OpenAiEnabled);
        Assert.Equal("sk-claude", c.AnthropicApiKey);
        Assert.Equal("g-key", c.GoogleApiKey);
    }

    [Fact]
    public void Chat_and_embeddings_fall_back_to_the_simulator_when_their_provider_is_Off()
    {
        var s = Settings(chat: "Anthropic", embedding: "Google");
        s.Anthropic.Enabled = false;
        s.Google.Enabled = false;

        var c = Credentials(s);

        Assert.Equal("Simulated", c.Provider);
        Assert.Equal("Simulated", c.EmbeddingProvider);
    }

    [Fact]
    public void Switching_a_provider_Off_does_not_change_a_choice_made_on_another_one()
    {
        var s = Settings(chat: "Anthropic", embedding: "Google");
        s.OpenAI.Enabled = false;

        var c = Credentials(s);

        Assert.Equal("Anthropic", c.Provider);
        Assert.Equal("Google", c.EmbeddingProvider);
    }

    [Fact]
    public void Platform_embeddings_stop_using_an_Off_provider()
    {
        var s = Settings(embedding: "OpenAI");
        var service = new PlatformEmbeddingService(new Snapshot(s), null!, null!, new SimulatedEmbeddingClient());
        Assert.Equal("OpenAI", service.ProviderName);

        s.OpenAI.Enabled = false;
        Assert.Equal("Simulated", service.ProviderName);
        Assert.False(service.RealProviderRequestedButUnconfigured); // switched off on purpose, not a missing key
    }
}
