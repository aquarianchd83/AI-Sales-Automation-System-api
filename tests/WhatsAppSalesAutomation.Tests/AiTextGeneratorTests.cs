using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>One-shot text with the tenant's own provider: the right request goes to the right provider, the key stays
/// out of the URL, and anything short of a real answer is a quiet null so the caller can fall back.</summary>
public sealed class AiTextGeneratorTests
{
    private sealed class FakeConfig : ITenantAiConfigProvider
    {
        public TenantAiCredentials? Credentials { get; set; }

        public Task<TenantAiCredentials?> GetForCurrentTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(Credentials);

        public Task<TenantAiProviderConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TenantAiProviderConfigDto> SaveConfigForCurrentTenantAsync(UpdateTenantAiProviderConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TenantAiProviderConfigDto?> GetConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TenantAiProviderConfigDto> SaveConfigForTenantAsync(Guid tenantId, UpdateTenantAiProviderConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public bool Throw { get; set; }
        public List<(string Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            Requests.Add((request.RequestUri!.ToString(), headers, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Throw)
                throw new HttpRequestException("connection refused");
            return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }

    private readonly StubHandler _http = new();
    private readonly FakeConfig _config = new();

    private AiTextGenerator Generator() => new(new HttpClient(_http), _config, NullLogger<AiTextGenerator>.Instance);

    private static TenantAiCredentials Credentials(string provider) => new(
        provider, "Simulated",
        "anthropic-key", "claude-test", "2023-06-01", "https://anthropic.test/v1",
        "openai-key", "gpt-test", "embed", "https://openai.test/v1",
        "google-key", "gemini-test", "embed", "https://google.test/v1beta");

    [Fact]
    public async Task Asks_Anthropic_with_the_key_in_a_header_and_returns_the_text()
    {
        _config.Credentials = Credentials("Anthropic");
        _http.Body = """{"content":[{"type":"text","text":"[\"a\",\"b\"]"}]}""";

        var text = await Generator().GenerateAsync("be brief", "list keywords", 300);

        Assert.Equal("""["a","b"]""", text);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://anthropic.test/v1/messages", request.Uri);
        Assert.Equal("anthropic-key", request.Headers["x-api-key"]);
        Assert.Contains("claude-test", request.Body);
        Assert.Contains("be brief", request.Body);
        Assert.Contains("list keywords", request.Body);
    }

    [Fact]
    public async Task Asks_OpenAI_with_a_bearer_token_and_returns_the_text()
    {
        _config.Credentials = Credentials("OpenAI");
        _http.Body = """{"choices":[{"message":{"content":"[\"x\"]"}}]}""";

        var text = await Generator().GenerateAsync("sys", "usr", 300);

        Assert.Equal("""["x"]""", text);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://openai.test/v1/chat/completions", request.Uri);
        Assert.Equal("Bearer openai-key", request.Headers["authorization"]);
        Assert.Contains("gpt-test", request.Body);
    }

    [Fact]
    public async Task Asks_Google_with_the_key_in_a_header_never_in_the_url()
    {
        _config.Credentials = Credentials("Google");
        _http.Body = """{"candidates":[{"content":{"parts":[{"text":"[\"y\"]"}]}}]}""";

        var text = await Generator().GenerateAsync("sys", "usr", 300);

        Assert.Equal("""["y"]""", text);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://google.test/v1beta/models/gemini-test:generateContent", request.Uri);
        Assert.DoesNotContain("google-key", request.Uri);
        Assert.Equal("google-key", request.Headers["x-goog-api-key"]);
    }

    [Theory]
    [InlineData("Simulated")]
    [InlineData("")]
    public async Task Does_not_call_anything_when_the_provider_is_simulated(string provider)
    {
        _config.Credentials = Credentials(provider);

        Assert.Null(await Generator().GenerateAsync("s", "u", 100));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Does_not_call_anything_without_credentials_or_without_the_providers_key()
    {
        Assert.Null(await Generator().GenerateAsync("s", "u", 100)); // tenant has never configured AI

        _config.Credentials = Credentials("Anthropic") with { AnthropicApiKey = null };
        Assert.Null(await Generator().GenerateAsync("s", "u", 100));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_failed_call_is_a_quiet_null_not_an_exception()
    {
        _config.Credentials = Credentials("OpenAI");

        _http.Status = HttpStatusCode.Unauthorized;
        Assert.Null(await Generator().GenerateAsync("s", "u", 100));

        _http.Status = HttpStatusCode.OK;
        _http.Body = "this is not json";
        Assert.Null(await Generator().GenerateAsync("s", "u", 100));

        _http.Throw = true;
        Assert.Null(await Generator().GenerateAsync("s", "u", 100));
    }
}
