using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What is actually sent to Meta for a template with an image header: the sample upload, the HEADER
/// component when the template is created, and the header parameter when a message is sent.</summary>
public sealed class MetaTemplateHeaderImageTests
{
    private sealed record Captured(HttpMethod Method, string Url, string? Authorization, string? FileOffset, string Body, int BodyLength);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<Captured, (HttpStatusCode Status, string Json)> _respond;
        public List<Captured> Requests { get; } = new();

        public ScriptedHandler(Func<Captured, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            request.Headers.TryGetValues("file_offset", out var offset);
            var captured = new Captured(
                request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                offset?.FirstOrDefault(), Encoding.UTF8.GetString(bytes), bytes.Length);
            Requests.Add(captured);

            var (status, json) = _respond(captured);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static TenantWhatsAppCredentials Credentials(string? appId = "app-1") => new(
        "phone-1", "waba-1", "token-1", "secret", "v19.0", "https://graph.example.test/", appId);

    private static WhatsAppTemplateSubmission Submission(bool withImage) => new(
        "welcome_offer", "en", "Marketing", "Hi {{1}}", new[] { "John" },
        withImage ? new WhatsAppTemplateHeaderImage("hero.png", "image/png", new byte[] { 1, 2, 3, 4, 5 }) : null);

    private static ScriptedHandler HappyMeta() => new(r => r.Url switch
    {
        var u when u.Contains("/app-1/uploads") => (HttpStatusCode.OK, "{\"id\":\"upload:SESSION123\"}"),
        var u when u.Contains("upload:SESSION123") => (HttpStatusCode.OK, "{\"h\":\"4::HANDLE-XYZ\"}"),
        var u when u.Contains("/waba-1/message_templates") => (HttpStatusCode.OK, "{\"id\":\"meta-tpl-1\",\"status\":\"PENDING\"}"),
        var u when u.Contains("/meta-tpl-1") => (HttpStatusCode.OK, "{\"success\":true}"),
        var u when u.Contains("/phone-1/messages") => (HttpStatusCode.OK, "{\"messages\":[{\"id\":\"wamid.1\"}]}"),
        _ => (HttpStatusCode.NotFound, "{}"),
    });

    private static MetaWhatsAppCloudApiClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler), NullLogger<MetaWhatsAppCloudApiClient>.Instance);

    [Fact]
    public async Task Creating_a_template_with_an_image_uploads_a_sample_then_defines_the_header_with_its_handle()
    {
        var meta = HappyMeta();

        var result = await Client(meta).CreateMessageTemplateAsync(Credentials(), Submission(withImage: true), default);

        Assert.True(result.Success);
        Assert.Equal("meta-tpl-1", result.MetaTemplateId);

        var open = meta.Requests[0];
        Assert.Contains("/app-1/uploads?file_length=5&file_type=image%2Fpng&file_name=hero.png", open.Url);
        Assert.Equal("OAuth token-1", open.Authorization);

        var upload = meta.Requests[1];
        // The session id starts "upload:", which must not be read as a URL scheme (that was a 500 on Sync).
        Assert.Equal("https://graph.example.test/v19.0/upload:SESSION123", upload.Url);
        Assert.Equal("0", upload.FileOffset);
        Assert.Equal(5, upload.BodyLength);

        using var payload = JsonDocument.Parse(meta.Requests[2].Body);
        var components = payload.RootElement.GetProperty("components");
        Assert.Equal("HEADER", components[0].GetProperty("type").GetString());
        Assert.Equal("IMAGE", components[0].GetProperty("format").GetString());
        Assert.Equal("4::HANDLE-XYZ", components[0].GetProperty("example").GetProperty("header_handle")[0].GetString());
        Assert.Equal("BODY", components[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Creating_a_template_with_a_video_defines_a_video_header()
    {
        var meta = HappyMeta();
        var submission = Submission(withImage: false) with
        {
            HeaderImage = new WhatsAppTemplateHeaderImage("promo.mp4", "video/mp4", new byte[] { 1, 2, 3 })
        };

        var result = await Client(meta).CreateMessageTemplateAsync(Credentials(), submission, default);

        Assert.True(result.Success);
        Assert.Contains("file_type=video%2Fmp4", meta.Requests[0].Url);
        using var payload = JsonDocument.Parse(meta.Requests[2].Body);
        Assert.Equal("VIDEO", payload.RootElement.GetProperty("components")[0].GetProperty("format").GetString());
    }

    [Fact]
    public async Task A_template_without_an_image_is_created_body_only_with_no_upload()
    {
        var meta = HappyMeta();

        var result = await Client(meta).CreateMessageTemplateAsync(Credentials(), Submission(withImage: false), default);

        Assert.True(result.Success);
        var only = Assert.Single(meta.Requests);
        using var payload = JsonDocument.Parse(only.Body);
        Assert.Equal(1, payload.RootElement.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task Without_a_meta_app_id_an_image_template_is_not_sent_and_the_reason_says_so()
    {
        var meta = HappyMeta();

        var result = await Client(meta).CreateMessageTemplateAsync(Credentials(appId: null), Submission(withImage: true), default);

        Assert.False(result.Success);
        Assert.Contains("App ID", result.ErrorMessage);
        Assert.Empty(meta.Requests);
    }

    [Fact]
    public async Task A_refused_upload_is_reported_with_metas_own_reason_and_the_template_is_not_created()
    {
        var meta = new ScriptedHandler(r => r.Url.Contains("/uploads")
            ? (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid file type\"}}")
            : (HttpStatusCode.OK, "{\"id\":\"x\"}"));

        var result = await Client(meta).CreateMessageTemplateAsync(Credentials(), Submission(withImage: true), default);

        Assert.False(result.Success);
        Assert.Equal("Invalid file type", result.ErrorMessage);
        Assert.Single(meta.Requests);
    }

    [Fact]
    public async Task Editing_a_template_that_has_an_image_sends_the_header_again()
    {
        var meta = HappyMeta();

        var result = await Client(meta).UpdateMessageTemplateAsync(Credentials(), "meta-tpl-1", Submission(withImage: true), default);

        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(meta.Requests[^1].Body);
        Assert.Equal("HEADER", payload.RootElement.GetProperty("components")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_message_carries_the_image_as_a_header_parameter_by_link()
    {
        var meta = HappyMeta();

        var result = await Client(meta).SendTemplateMessageAsync(
            Credentials(), "+919815733426", "welcome_offer", "en", new[] { "Harish" }, "https://cdn.example.test/hero.png", default);

        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(meta.Requests[0].Body);
        var components = payload.RootElement.GetProperty("template").GetProperty("components");
        Assert.Equal("header", components[0].GetProperty("type").GetString());
        Assert.Equal("https://cdn.example.test/hero.png", components[0].GetProperty("parameters")[0].GetProperty("image").GetProperty("link").GetString());
        Assert.Equal("body", components[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_image_link_Meta_cannot_reach_fails_with_a_clear_reason_instead_of_being_sent()
    {
        var meta = HappyMeta();

        var result = await Client(meta).SendTemplateMessageAsync(
            Credentials(), "+919815733426", "welcome_offer", "en", new[] { "Harish" }, "/media/2026/09/hero.png", default);

        Assert.False(result.Success);
        Assert.Contains("PublicBaseUrl", result.ErrorMessage);
        Assert.Empty(meta.Requests);
    }
}
