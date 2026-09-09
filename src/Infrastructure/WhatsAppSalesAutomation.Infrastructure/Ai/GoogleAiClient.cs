using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real Google Gemini client (Generative Language API). No longer implements <see cref="IAiService"/>
/// directly - see <see cref="AnthropicAiClient"/>'s own doc comment for why. Forces the record_response
/// function call via <c>tool_config.function_calling_config</c>, same reasoning as AnthropicAiClient.
/// Never exercised against a live API key in this codebase - see that class's doc comment for the same
/// caveat. Unlike the other two providers, Gemini's REST API takes the API key as a query-string
/// parameter rather than a header, so the request URI is built per-call instead of via a fixed
/// BaseAddress (this was already true before multi-tenancy - now the base itself is per-tenant too).
/// </summary>
public class GoogleAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleAiClient> _logger;

    public GoogleAiClient(HttpClient httpClient, ILogger<GoogleAiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<AiReplyResult> GetResponseAsync(TenantAiCredentials credentials, AiConversationContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelUsed = $"Google:{credentials.GoogleChatModel}";

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = AiPromptSupport.SystemPrompt(context.CustomerName) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = AiPromptSupport.BuildUserMessage(context) } } } },
            tools = new[]
            {
                new
                {
                    function_declarations = new[]
                    {
                        new
                        {
                            name = AiPromptSupport.ToolName,
                            description = AiPromptSupport.ToolDescription,
                            parameters = AiPromptSupport.ToolInputSchema()
                        }
                    }
                }
            },
            tool_config = new { function_calling_config = new { mode = "ANY", allowed_function_names = new[] { AiPromptSupport.ToolName } } }
        };

        try
        {
            var baseUri = new Uri(AiPromptSupport.EnsureTrailingSlash(credentials.GoogleBaseUrl));
            var uri = new Uri(baseUri, $"models/{credentials.GoogleChatModel}:generateContent?key={Uri.EscapeDataString(credentials.GoogleApiKey ?? string.Empty)}");
            using var response = await _httpClient.PostAsJsonAsync(uri, payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google AI call failed ({Status}) for conversation {ConversationId}: {Body}", response.StatusCode, context.ConversationId, body);
                return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
            }

            var parsed = JsonSerializer.Deserialize<GoogleGenerateContentResponse>(body, JsonOptions);
            var functionCall = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts?
                .Select(p => p.FunctionCall)
                .FirstOrDefault(f => f?.Name == AiPromptSupport.ToolName);

            if (functionCall?.Args is null)
            {
                _logger.LogWarning("Google AI response had no {ToolName} function call for conversation {ConversationId}", AiPromptSupport.ToolName, context.ConversationId);
                return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
            }

            var toolResult = functionCall.Args.Value.Deserialize<ToolResultPayload>(JsonOptions) ?? new ToolResultPayload();

            return toolResult.ToAiReplyResult(
                modelUsed, parsed?.UsageMetadata?.PromptTokenCount, parsed?.UsageMetadata?.CandidatesTokenCount, stopwatch.Elapsed, context.ExistingSummary);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Google AI call threw for conversation {ConversationId}", context.ConversationId);
            return AiClientFailure.Result(modelUsed, stopwatch.Elapsed, context.ExistingSummary);
        }
    }

    private class GoogleGenerateContentResponse
    {
        [JsonPropertyName("candidates")]
        public List<GoogleCandidate>? Candidates { get; set; }

        [JsonPropertyName("usageMetadata")]
        public GoogleUsageMetadata? UsageMetadata { get; set; }
    }

    private class GoogleCandidate
    {
        [JsonPropertyName("content")]
        public GoogleContent? Content { get; set; }
    }

    private class GoogleContent
    {
        [JsonPropertyName("parts")]
        public List<GooglePart>? Parts { get; set; }
    }

    private class GooglePart
    {
        [JsonPropertyName("functionCall")]
        public GoogleFunctionCall? FunctionCall { get; set; }
    }

    private class GoogleFunctionCall
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("args")]
        public JsonElement? Args { get; set; }
    }

    private class GoogleUsageMetadata
    {
        [JsonPropertyName("promptTokenCount")]
        public int PromptTokenCount { get; set; }

        [JsonPropertyName("candidatesTokenCount")]
        public int CandidatesTokenCount { get; set; }
    }
}
