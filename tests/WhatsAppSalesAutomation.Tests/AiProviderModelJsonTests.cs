using System.Text.Json;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The model catalogue screen reads and sends the type and cost tier by name ("Chat", "Cheap"), not by number.</summary>
public sealed class AiProviderModelJsonTests
{
    [Fact]
    public void A_model_is_sent_with_its_type_and_cost_tier_as_names()
    {
        var dto = new AiProviderModelDto(Guid.NewGuid(), "Anthropic", "Claude Haiku 4.5", "claude-haiku-4-5", AiModelType.Chat, null, null, 1m, 5m,
            AiModelCostTier.Cheap, new[] { "customer-replies" }, "when to use", true, true, DateTime.UtcNow);

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"modelType\":\"Chat\"", json);
        Assert.Contains("\"costTier\":\"Cheap\"", json);
        Assert.Contains("\"bestFor\":[\"customer-replies\"]", json);
    }

    [Fact]
    public void A_save_request_reads_the_type_and_cost_tier_by_name()
    {
        var request = JsonSerializer.Deserialize<SaveAiProviderModelRequest>(
            "{\"provider\":\"OpenAI\",\"displayName\":\"x\",\"modelId\":\"x\",\"modelType\":\"Embedding\",\"costTier\":\"Expensive\"}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(AiModelType.Embedding, request.ModelType);
        Assert.Equal(AiModelCostTier.Expensive, request.CostTier);
    }

    [Fact]
    public void Best_for_keeps_known_tasks_once_in_catalogue_order()
    {
        Assert.Equal("customer-replies,content-studio", AiModelTasks.Join(new[] { " Content-Studio ", "customer-replies", "content-studio" }));
        Assert.Equal(new[] { "customer-replies", "content-studio" }, AiModelTasks.Split("customer-replies,content-studio"));
        Assert.Empty(AiModelTasks.Split(null));
    }

    [Fact]
    public void A_task_the_catalogue_does_not_know_is_refused()
    {
        var validator = new SaveAiProviderModelRequestValidator();

        Assert.True(validator.Validate(new SaveAiProviderModelRequest("OpenAI", "x", "x", BestFor: new[] { "customer-replies" })).IsValid);
        Assert.False(validator.Validate(new SaveAiProviderModelRequest("OpenAI", "x", "x", BestFor: new[] { "world-domination" })).IsValid);
    }
}
