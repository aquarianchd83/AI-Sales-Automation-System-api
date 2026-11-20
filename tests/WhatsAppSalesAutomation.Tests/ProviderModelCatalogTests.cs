using System.Text.Json.Nodes;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The models a provider's own list holds, cut down to the ones that generate text.</summary>
public sealed class ProviderModelCatalogTests
{
    private static JsonNode Data(params string[] ids) =>
        new JsonObject { ["data"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) };

    [Fact]
    public void Claude_lists_every_model_it_returns()
    {
        var models = ProviderModelCatalog.Parse("Anthropic", Data("claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5-20251001"));

        Assert.Equal(new[] { "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5-20251001" }, models);
    }

    [Fact]
    public void ChatGPT_keeps_the_chat_models_and_drops_embeddings_speech_images_and_moderation()
    {
        var models = ProviderModelCatalog.Parse("OpenAI", Data(
            "gpt-5", "gpt-5-mini", "o3-mini", "chatgpt-4o-latest",
            "text-embedding-3-small", "whisper-1", "tts-1", "dall-e-3", "omni-moderation-latest", "gpt-4o-audio-preview", "gpt-4o-realtime-preview",
            "gpt-4o-transcribe", "gpt-image-1", "gpt-4o-search-preview", "gpt-3.5-turbo-instruct", "babbage-002"));

        Assert.Equal(new[] { "gpt-5", "gpt-5-mini", "o3-mini", "chatgpt-4o-latest" }, models);
    }

    [Fact]
    public void Gemini_keeps_the_models_that_generate_content_and_drops_embeddings_and_images()
    {
        var body = new JsonObject
        {
            ["models"] = new JsonArray(
                Model("models/gemini-2.5-flash", "generateContent", "countTokens"),
                Model("models/gemini-2.5-pro", "generateContent"),
                Model("models/text-embedding-004", "embedContent"),
                Model("models/gemini-embedding-001", "embedContent"),
                Model("models/imagen-4.0-generate-001", "predict"),
                Model("models/gemini-2.0-flash-live-001", "bidiGenerateContent"),
                Model("models/gemini-2.5-flash-image", "generateContent"))
        };

        Assert.Equal(new[] { "gemini-2.5-flash", "gemini-2.5-pro" }, ProviderModelCatalog.Parse("Google", body));
    }

    [Fact]
    public void A_model_listed_twice_is_listed_once()
    {
        Assert.Single(ProviderModelCatalog.Parse("Anthropic", Data("claude-opus-5", "CLAUDE-OPUS-5")));
    }

    [Fact]
    public void An_empty_or_unexpected_answer_lists_nothing()
    {
        Assert.Empty(ProviderModelCatalog.Parse("OpenAI", new JsonObject()));
        Assert.Empty(ProviderModelCatalog.Parse("Google", null));
    }

    private static JsonNode Model(string name, params string[] methods) =>
        new JsonObject { ["name"] = name, ["supportedGenerationMethods"] = new JsonArray(methods.Select(m => (JsonNode)m).ToArray()) };
}
