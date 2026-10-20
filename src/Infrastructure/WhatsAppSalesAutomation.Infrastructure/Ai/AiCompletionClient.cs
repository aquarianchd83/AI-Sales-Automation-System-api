using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// One call to ChatGPT, Claude or Gemini for a product-knowledge task, returning the text <b>and</b> what the older text generator throws away:
/// token counts, the model that answered, how long it took, and a normalised reason when it failed. It reads the same platform-wide keys,
/// models and On/Off switches as every other AI feature, so nothing here is configured twice. Keys are only ever put in request headers;
/// an error message is built from the status code and the provider's short error text, never from the request.
/// </summary>
public sealed class AiCompletionClient : IAiCompletionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IPlatformAiConfigProvider _config;
    private readonly ILogger<AiCompletionClient> _logger;

    public AiCompletionClient(HttpClient http, IPlatformAiConfigProvider config, ILogger<AiCompletionClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.Timeout = Timeout.InfiniteTimeSpan; // the per-call timeout is applied below, from the platform policy
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var credentials = _config.Get();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 300)));

        try
        {
            HttpRequestMessage message;
            string model;
            switch (request.Provider.ToLowerInvariant())
            {
                case "anthropic":
                    model = credentials.AnthropicModel;
                    message = Anthropic(credentials, request, model);
                    break;
                case "openai":
                    model = credentials.OpenAiChatModel;
                    message = OpenAi(credentials, request, model);
                    break;
                case "google":
                    model = credentials.GoogleChatModel;
                    message = Google(credentials, request, model);
                    break;
                default:
                    return Failed(watch, AiErrorCodes.ProviderUnavailable, $"{request.Provider} is not a known provider.", null);
            }

            using (message)
            {
                using var response = await _http.SendAsync(message, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var status = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode)
                    return Failed(watch, CodeFor(response.StatusCode), $"{request.Provider} answered HTTP {status}. {ProviderMessage(body)}".Trim(), status);

                return Parse(request.Provider, model, body, watch);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(watch, AiErrorCodes.Timeout, $"{request.Provider} did not answer within {request.TimeoutSeconds} seconds.", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("AI call to {Provider} could not reach the provider: {Reason}", request.Provider, ex.GetType().Name);
            return Failed(watch, AiErrorCodes.ProviderError, $"Could not reach {request.Provider}. Check the base URL and the network.", null);
        }
    }

    // ----- request shapes ----------------------------------------------------------------------------------------------------------

    private static HttpRequestMessage Anthropic(AiCredentials c, AiCompletionRequest r, string model)
    {
        object content = r.ImageBase64 is null
            ? r.User
            : new object[]
            {
                new { type = "image", source = new { type = "base64", media_type = r.ImageMediaType ?? "image/png", data = r.ImageBase64 } },
                new { type = "text", text = r.User }
            };

        var payload = new { model, max_tokens = r.MaxTokens, system = r.System, messages = new[] { new { role = "user", content } } };
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.AnthropicBaseUrl)), "messages"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("x-api-key", c.AnthropicApiKey);
        request.Headers.Add("anthropic-version", c.AnthropicApiVersion);
        return request;
    }

    private static HttpRequestMessage OpenAi(AiCredentials c, AiCompletionRequest r, string model)
    {
        object userContent = r.ImageBase64 is null
            ? r.User
            : new object[]
            {
                new { type = "text", text = r.User },
                new { type = "image_url", image_url = new { url = $"data:{r.ImageMediaType ?? "image/png"};base64,{r.ImageBase64}" } }
            };

        // No token cap and no response_format: like the other OpenAI clients here, this works with any OpenAI-compatible endpoint. The reply is
        // checked for JSON by the task runner, not trusted to be.
        var payload = new { model, messages = new object[] { new { role = "system", content = r.System }, new { role = "user", content = userContent } } };
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.OpenAiBaseUrl)), "chat/completions"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.OpenAiApiKey);
        return request;
    }

    private static HttpRequestMessage Google(AiCredentials c, AiCompletionRequest r, string model)
    {
        var parts = new List<object> { new { text = r.User } };
        if (r.ImageBase64 is not null)
            parts.Add(new { inlineData = new { mimeType = r.ImageMediaType ?? "image/png", data = r.ImageBase64 } });

        var generation = r.ExpectJson
            ? (object)new { maxOutputTokens = r.MaxTokens, responseMimeType = "application/json" }
            : new { maxOutputTokens = r.MaxTokens };

        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = r.System } } },
            contents = new[] { new { role = "user", parts } },
            generationConfig = generation
        };
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.GoogleBaseUrl)), $"models/{model}:generateContent"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("x-goog-api-key", c.GoogleApiKey);
        return request;
    }

    // ----- reading the reply -------------------------------------------------------------------------------------------------------

    private AiCompletionResult Parse(string provider, string configuredModel, string body, Stopwatch watch)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? text;
            int? input = null, output = null;
            var model = configuredModel;

            switch (provider.ToLowerInvariant())
            {
                case "anthropic":
                    text = root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array
                        ? string.Concat(blocks.EnumerateArray().Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text").Select(b => b.GetProperty("text").GetString()))
                        : null;
                    if (root.TryGetProperty("usage", out var au))
                    {
                        input = Int(au, "input_tokens");
                        output = Int(au, "output_tokens");
                    }
                    break;

                case "openai":
                    text = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                        ? choices[0].GetProperty("message").GetProperty("content").GetString()
                        : null;
                    if (root.TryGetProperty("usage", out var ou))
                    {
                        input = Int(ou, "prompt_tokens");
                        output = Int(ou, "completion_tokens");
                    }
                    break;

                default:
                    text = root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
                        && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0
                            ? string.Concat(parts.EnumerateArray().Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()))
                            : null;
                    if (root.TryGetProperty("usageMetadata", out var gu))
                    {
                        input = Int(gu, "promptTokenCount");
                        output = Int(gu, "candidatesTokenCount");
                    }
                    break;
            }

            if (root.TryGetProperty("model", out var echoed) && echoed.ValueKind == JsonValueKind.String)
                model = echoed.GetString() ?? configuredModel;

            return string.IsNullOrWhiteSpace(text)
                ? Failed(watch, AiErrorCodes.ProviderError, $"{provider} returned no text.", 200)
                : new AiCompletionResult(true, text, model, input, output, (int)watch.ElapsedMilliseconds, null, null, 200);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Failed(watch, AiErrorCodes.ProviderError, $"{provider} returned a reply that could not be read.", 200);
        }
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    private static string CodeFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiErrorCodes.Unauthorized,
        HttpStatusCode.TooManyRequests => AiErrorCodes.RateLimited,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => AiErrorCodes.Timeout,
        _ => AiErrorCodes.ProviderError
    };

    /// <summary>The provider's own short error text (every vendor wraps it as error.message), scrubbed and capped; never the whole body.</summary>
    private static string ProviderMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m))
                    return AiLogScrubber.Scrub(m.GetString(), 200) ?? string.Empty;
                if (error.ValueKind == JsonValueKind.String)
                    return AiLogScrubber.Scrub(error.GetString(), 200) ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // not JSON: say nothing rather than echo it
        }

        return string.Empty;
    }

    private static AiCompletionResult Failed(Stopwatch watch, string code, string message, int? status) =>
        new(false, null, null, null, null, (int)watch.ElapsedMilliseconds, code, AiLogScrubber.Scrub(message), status);
}
