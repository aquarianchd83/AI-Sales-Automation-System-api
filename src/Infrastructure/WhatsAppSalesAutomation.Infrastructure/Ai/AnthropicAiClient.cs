using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real Anthropic Claude client (Messages API). No longer implements <see cref="IAiService"/> directly -
/// <see cref="AiServiceFactory"/> is the DI-registered IAiService and the only caller of this class,
/// passing the calling tenant's already-resolved <see cref="TenantAiCredentials"/> into every call
/// instead of this class reading one fixed global setting. Forces tool-use (<c>tool_choice</c> pinned
/// to AiPromptSupport.ToolName) so the reply text and the structured intent/confidence/entities always
/// arrive together in one call - never exercised against a live API key in this codebase, same caveat
/// as MetaWhatsAppCloudApiClient: treat first use against a real key as the actual first test of this
/// class.
/// </summary>
public class AnthropicAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicAiClient> _logger;

    public AnthropicAiClient(HttpClient httpClient, ILogger<AnthropicAiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<AiReplyResult> GetResponseAsync(TenantAiCredentials credentials, AiConversationContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelUsed = $"Anthropic:{credentials.AnthropicModel}";

        var payload = new
        {
            model = credentials.AnthropicModel,
            max_tokens = 1536,
            // System prompt and tool schema are sent as cacheable blocks. Both are stable for a
            // given tenant across every conversation and every turn (see AiPromptSupport's doc
            // comment), so this prefix is a cache hit on all but the first call - which is what keeps
            // the much larger prompt from costing more than the thirteen-line one it replaced.
            system = new[]
            {
                new
                {
                    type = "text",
                    text = AiPromptSupport.SystemPrompt(context),
                    cache_control = new { type = "ephemeral" }
                }
            },
            messages = new[] { new { role = "user", content = AiPromptSupport.BuildUserMessage(context) } },
            tools = new[]
            {
                new
                {
                    name = AiPromptSupport.ToolName,
                    description = AiPromptSupport.ToolDescription,
                    input_schema = AiPromptSupport.ToolInputSchema(context.SchemaFields),
                    cache_control = new { type = "ephemeral" }
                }
            },
            tool_choice = new { type = "tool", name = AiPromptSupport.ToolName }
        };

        try
        {
            var uri = new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(credentials.AnthropicBaseUrl)), "messages");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Add("x-api-key", credentials.AnthropicApiKey);
            request.Headers.Add("anthropic-version", credentials.AnthropicApiVersion);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic call failed ({Status}) for conversation {ConversationId}: {Body}", response.StatusCode, context.ConversationId, body);
                return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
            }

            var parsed = JsonSerializer.Deserialize<AnthropicResponse>(body, JsonOptions);
            var toolUse = parsed?.Content?.FirstOrDefault(c => c.Type == "tool_use" && c.Name == AiPromptSupport.ToolName);

            if (toolUse?.Input is null)
            {
                _logger.LogWarning("Anthropic response had no {ToolName} tool_use block for conversation {ConversationId}", AiPromptSupport.ToolName, context.ConversationId);
                return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
            }

            var toolResult = toolUse.Input.Value.Deserialize<ToolResultPayload>(JsonOptions) ?? new ToolResultPayload();

            _logger.LogDebug(
                "Anthropic conversation {ConversationId}: {CachedTokens} of {InputTokens} input tokens served from cache",
                context.ConversationId, parsed?.Usage?.CacheReadInputTokens ?? 0, parsed?.Usage?.InputTokens ?? 0);

            return toolResult.ToAiReplyResult(modelUsed, parsed?.Usage?.InputTokens, parsed?.Usage?.OutputTokens, stopwatch.Elapsed, context.ExistingSummary);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Anthropic call threw for conversation {ConversationId}", context.ConversationId);
            return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
        }
    }

    private class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }

        [JsonPropertyName("usage")]
        public AnthropicUsage? Usage { get; set; }
    }

    private class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("input")]
        public JsonElement? Input { get; set; }
    }

    private class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }

        /// <summary>Tokens served from the prompt cache. Logged rather than stored: if this sits at
        /// zero in production the cacheable prefix has been broken by something conversation-specific
        /// leaking into it, and that is worth noticing early.</summary>
        [JsonPropertyName("cache_read_input_tokens")]
        public int CacheReadInputTokens { get; set; }
    }
}
