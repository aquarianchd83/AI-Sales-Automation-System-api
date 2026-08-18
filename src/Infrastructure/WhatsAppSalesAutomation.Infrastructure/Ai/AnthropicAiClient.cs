using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real Anthropic Claude client (Messages API), selected via <c>AiProviders:Provider = "Anthropic"</c>.
/// Forces tool-use (<c>tool_choice</c> pinned to AiPromptSupport.ToolName) so the reply text and the
/// structured intent/confidence/entities always arrive together in one call - never exercised against
/// a live API key in this codebase, same caveat as MetaWhatsAppCloudApiClient: treat first use against
/// a real key as the actual first test of this class.
/// </summary>
public class AnthropicAiClient : IAiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly AnthropicSettings _settings;
    private readonly ILogger<AnthropicAiClient> _logger;

    public AnthropicAiClient(HttpClient httpClient, IOptions<AiProviderSettings> settings, ILogger<AnthropicAiClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value.Anthropic;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.anthropic.com/v1/");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _settings.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", _settings.ApiVersion);
    }

    public async Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelUsed = $"Anthropic:{_settings.Model}";

        var payload = new
        {
            model = _settings.Model,
            max_tokens = 1024,
            system = AiPromptSupport.SystemPrompt(context.CustomerName),
            messages = new[] { new { role = "user", content = AiPromptSupport.BuildUserMessage(context) } },
            tools = new[]
            {
                new
                {
                    name = AiPromptSupport.ToolName,
                    description = AiPromptSupport.ToolDescription,
                    input_schema = AiPromptSupport.ToolInputSchema()
                }
            },
            tool_choice = new { type = "tool", name = AiPromptSupport.ToolName }
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("messages", payload, JsonOptions, cancellationToken);
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
    }
}
