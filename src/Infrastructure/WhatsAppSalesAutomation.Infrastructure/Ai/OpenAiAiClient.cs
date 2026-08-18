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
/// Real OpenAI client (Chat Completions API), selected via <c>AiProviders:Provider = "OpenAI"</c>.
/// Forces the record_response function call via <c>tool_choice</c>, same reasoning as
/// AnthropicAiClient. Never exercised against a live API key in this codebase - see that class's doc
/// comment for the same caveat.
/// </summary>
public class OpenAiAiClient : IAiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly ILogger<OpenAiAiClient> _logger;

    public OpenAiAiClient(HttpClient httpClient, IOptions<AiProviderSettings> settings, ILogger<OpenAiAiClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value.OpenAI;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.openai.com/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
    }

    public async Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelUsed = $"OpenAI:{_settings.ChatModel}";

        var payload = new
        {
            model = _settings.ChatModel,
            messages = new[]
            {
                new { role = "system", content = AiPromptSupport.SystemPrompt(context.CustomerName) },
                new { role = "user", content = AiPromptSupport.BuildUserMessage(context) }
            },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = AiPromptSupport.ToolName,
                        description = AiPromptSupport.ToolDescription,
                        parameters = AiPromptSupport.ToolInputSchema()
                    }
                }
            },
            tool_choice = new { type = "function", function = new { name = AiPromptSupport.ToolName } }
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("chat/completions", payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI call failed ({Status}) for conversation {ConversationId}: {Body}", response.StatusCode, context.ConversationId, body);
                return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
            }

            var parsed = JsonSerializer.Deserialize<OpenAiResponse>(body, JsonOptions);
            var toolCall = parsed?.Choices?.FirstOrDefault()?.Message?.ToolCalls?
                .FirstOrDefault(t => t.Function?.Name == AiPromptSupport.ToolName);
            var argumentsJson = toolCall?.Function?.Arguments;

            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                _logger.LogWarning("OpenAI response had no {ToolName} tool call for conversation {ConversationId}", AiPromptSupport.ToolName, context.ConversationId);
                return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
            }

            var toolResult = JsonSerializer.Deserialize<ToolResultPayload>(argumentsJson, JsonOptions) ?? new ToolResultPayload();

            return toolResult.ToAiReplyResult(modelUsed, parsed?.Usage?.PromptTokens, parsed?.Usage?.CompletionTokens, stopwatch.Elapsed, context.ExistingSummary);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "OpenAI call threw for conversation {ConversationId}", context.ConversationId);
            return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
        }
    }

    private class OpenAiResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public OpenAiUsage? Usage { get; set; }
    }

    private class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }
    }

    private class OpenAiMessage
    {
        [JsonPropertyName("tool_calls")]
        public List<OpenAiToolCall>? ToolCalls { get; set; }
    }

    private class OpenAiToolCall
    {
        [JsonPropertyName("function")]
        public OpenAiFunctionCall? Function { get; set; }
    }

    private class OpenAiFunctionCall
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }

    private class OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }
    }
}
