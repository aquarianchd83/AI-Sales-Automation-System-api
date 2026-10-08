using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Proves each saved key works by asking the provider to list its models - a free, read-only call that fails with 401/403 on a
/// bad key. Keys travel in headers, never the URL, and this client's loggers are removed (see DependencyInjection), so a key is
/// never written to a log. Nothing here returns or logs a key.
/// </summary>
public class AiProviderVerifier : IAiProviderVerifier
{
    private readonly HttpClient _httpClient;
    private readonly IPlatformAiConfigProvider _configProvider;
    private readonly ILogger<AiProviderVerifier> _logger;

    public AiProviderVerifier(HttpClient httpClient, IPlatformAiConfigProvider configProvider, ILogger<AiProviderVerifier> logger)
    {
        _httpClient = httpClient;
        _configProvider = configProvider;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<IReadOnlyList<AiProviderCheckDto>> VerifyAsync(string? provider, CancellationToken cancellationToken)
    {
        var c = _configProvider.Get();
        bool Active(string name) => string.Equals(c.Provider, name, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(c.EmbeddingProvider, name, StringComparison.OrdinalIgnoreCase);

        var checks = new List<AiProviderCheckDto>();
        bool Wanted(string name) => string.IsNullOrWhiteSpace(provider) || string.Equals(provider, name, StringComparison.OrdinalIgnoreCase);

        if (Wanted("Anthropic"))
        {
            checks.Add(await CheckAsync("Anthropic", c.AnthropicApiKey, Active("Anthropic"), cancellationToken, key =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Url(c.AnthropicBaseUrl, "models"));
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", c.AnthropicApiVersion);
                return request;
            }));
        }

        if (Wanted("OpenAI"))
        {
            checks.Add(await CheckAsync("OpenAI", c.OpenAiApiKey, Active("OpenAI"), cancellationToken, key =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Url(c.OpenAiBaseUrl, "models"));
                request.Headers.Add("Authorization", $"Bearer {key}");
                return request;
            }));
        }

        if (Wanted("Google"))
        {
            checks.Add(await CheckAsync("Google", c.GoogleApiKey, Active("Google"), cancellationToken, key =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Url(c.GoogleBaseUrl, "models?pageSize=1"));
                request.Headers.Add("x-goog-api-key", key);
                return request;
            }));
        }

        return checks;
    }

    private async Task<AiProviderCheckDto> CheckAsync(
        string provider, string? apiKey, bool isActive, CancellationToken cancellationToken, Func<string, HttpRequestMessage> build)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiProviderCheckDto(provider, false, isActive, false, "No API key is saved.");
        }

        try
        {
            using var request = build(apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new AiProviderCheckDto(provider, true, isActive, true, $"{provider} accepted the API key.");
            }

            var status = (int)response.StatusCode;
            var reason = status is 401 or 403 or 400
                ? $"{provider} rejected the API key (HTTP {status})."
                : $"{provider} answered HTTP {status}.";
            return new AiProviderCheckDto(provider, true, isActive, false, reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("AI provider check for {Provider} could not reach the provider: {Reason}", provider, ex.GetType().Name);
            return new AiProviderCheckDto(provider, true, isActive, false, $"Could not reach {provider}. Check the base URL and the network.");
        }
    }

    private static Uri Url(string baseUrl, string path) => new(baseUrl.EndsWith('/') ? baseUrl + path : baseUrl + "/" + path);
}
