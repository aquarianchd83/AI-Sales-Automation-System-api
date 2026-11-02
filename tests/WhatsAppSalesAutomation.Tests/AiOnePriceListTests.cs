using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The AI call log prices a call from the Configuration page's "Model token prices" (the one price list), not from a second set of numbers
/// typed beside each provider's key.</summary>
public sealed class AiOnePriceListTests
{
    private static AiTaskPolicy PolicyFor(AiProviderSettings providers) =>
        new AiTaskPolicyProvider(
            new FixedOptions<AiFeatureOptions>(new AiFeatureOptions()),
            new FixedOptions<AiProviderSettings>(providers),
            new FixedOptions<AiPricingOptions>(new AiPricingOptions())).Get();

    [Fact]
    public void A_listed_model_is_priced_from_the_model_table_even_when_the_per_provider_price_is_zero()
    {
        var policy = PolicyFor(new AiProviderSettings { OpenAI = new OpenAiSettings { ChatModel = "gpt-5-nano" } });

        // 0.00005 and 0.0004 per 1,000 tokens = $0.05 and $0.40 per million.
        Assert.Equal(new ProviderPrice(0.05m, 0.40m), policy.PriceOf("OpenAI"));
        Assert.Equal(0.003669m, policy.PriceOf("OpenAI").Cost(9_308, 8_009));
    }

    [Fact]
    public void A_dated_model_id_matches_its_row()
    {
        var policy = PolicyFor(new AiProviderSettings { Anthropic = new AnthropicSettings { Model = "claude-haiku-4-5-20251001" } });

        Assert.Equal(new ProviderPrice(1m, 5m), policy.PriceOf("Anthropic"));
    }

    [Fact]
    public void A_model_with_no_row_keeps_the_price_typed_beside_its_key()
    {
        var policy = PolicyFor(new AiProviderSettings { OpenAI = new OpenAiSettings { ChatModel = "gpt-9-unlisted", InputPer1MUsd = 2m, OutputPer1MUsd = 8m } });

        Assert.Equal(new ProviderPrice(2m, 8m), policy.PriceOf("OpenAI"));
    }

    [Fact]
    public void One_providers_model_is_never_priced_at_anothers_row()
    {
        var policy = PolicyFor(new AiProviderSettings { Google = new GoogleAiSettings { ChatModel = "gpt-5-nano" } });

        Assert.Equal(new ProviderPrice(0m, 0m), policy.PriceOf("Google"));
    }
}
