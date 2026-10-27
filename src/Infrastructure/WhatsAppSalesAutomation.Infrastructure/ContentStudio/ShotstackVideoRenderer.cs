using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;

namespace WhatsAppSalesAutomation.Infrastructure.ContentStudio;

/// <summary>
/// Shotstack as the video renderer: a hosted service, so the application only describes the video and later collects it. The platform's key goes
/// in a header and never into a log or a URL. The finished video is fetched only from Shotstack's own output hosts over https, without the key,
/// with redirects off and the network guard the website reader uses, and no more than the allowed size is ever read.
/// </summary>
public sealed class ShotstackVideoRenderer : IVideoRenderer
{
    private const string ApiHost = "api.shotstack.io";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<RenderOptions> _options;
    private readonly ILogger<ShotstackVideoRenderer> _logger;

    public ShotstackVideoRenderer(HttpClient http, IOptionsSnapshot<RenderOptions> options, ILogger<ShotstackVideoRenderer> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Name => "Shotstack";

    public bool IsConfigured => _options.Value.RendererConfigured;

    public async Task<string> SubmitAsync(RenderPlan plan, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(plan);
        using var request = Request(HttpMethod.Post, "render");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);

        using var doc = await ParseAsync(response, cancellationToken);
        if (doc.RootElement.TryGetProperty("response", out var inner) && inner.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
            return id.GetString()!;
        throw new VideoProviderException("The video service did not accept the video.");
    }

    public async Task<RenderStatus> GetStatusAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (externalId.Length is 0 or > 100 || !externalId.All(c => char.IsLetterOrDigit(c) || c == '-'))
            throw new VideoProviderException("The video service's reference for this video is not valid.");

        using var request = Request(HttpMethod.Get, $"render/{externalId}");
        using var response = await SendAsync(request, cancellationToken);
        using var doc = await ParseAsync(response, cancellationToken);
        if (!doc.RootElement.TryGetProperty("response", out var inner))
            throw new VideoProviderException("The video service gave an answer that could not be read.");

        var status = Text(inner, "status") ?? string.Empty;
        return status switch
        {
            "done" => new RenderStatus(RenderPhase.Done, 100, Text(inner, "url"), null),
            "failed" => new RenderStatus(RenderPhase.Failed, 0, null, Text(inner, "error")),
            "queued" => new RenderStatus(RenderPhase.Working, 5, null, null),
            "fetching" => new RenderStatus(RenderPhase.Working, 20, null, null),
            "rendering" => new RenderStatus(RenderPhase.Working, 55, null, null),
            "saving" => new RenderStatus(RenderPhase.Working, 90, null, null),
            _ => new RenderStatus(RenderPhase.Working, 10, null, null),
        };
    }

    public async Task DownloadAsync(string url, long maxBytes, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!IsOutputHost(url, out var uri))
            throw new VideoProviderException("The finished video is not on the video service's own host, so it was not downloaded.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri); // no API key: it never goes to the file host
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new VideoProviderException("The finished video could not be downloaded from the video service.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new VideoProviderException("The finished video is larger than allowed.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new VideoProviderException("The finished video is larger than allowed.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>Shotstack's output lives on its CDN or in its own "shotstack-..." S3 buckets; nothing else is trusted.</summary>
    public static bool IsOutputHost(string? url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = parsed.Host.ToLowerInvariant();
        var ok = host == "cdn.shotstack.io" || host.EndsWith(".shotstack.io", StringComparison.Ordinal) || (host.StartsWith("shotstack-", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal));
        uri = parsed;
        return ok;
    }

    /// <summary>The timeline: pictures on one track, the on-screen words above them, the narration on its own track.</summary>
    public static string BuildBody(RenderPlan plan)
    {
        var pictures = plan.Scenes.Select(s => new Dictionary<string, object?>
        {
            ["asset"] = new { type = "image", src = s.ImageUrl },
            ["start"] = Number(s.StartSeconds), ["length"] = Number(s.LengthSeconds), ["fit"] = "cover", ["effect"] = "zoomIn",
        }).ToList();
        var words = plan.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.OnScreenText)).Select(s => new Dictionary<string, object?>
        {
            ["asset"] = new { type = "title", text = s.OnScreenText!.Trim(), style = "minimal", size = "small" },
            ["start"] = Number(s.StartSeconds), ["length"] = Number(s.LengthSeconds), ["position"] = "bottom",
        }).ToList();
        var voice = plan.Scenes.Select(s => new Dictionary<string, object?>
        {
            ["asset"] = new { type = "audio", src = s.AudioUrl, volume = 1 },
            ["start"] = Number(s.StartSeconds), ["length"] = Number(s.LengthSeconds),
        }).ToList();

        var tracks = new List<object>();
        if (words.Count > 0)
            tracks.Add(new { clips = words });
        tracks.Add(new { clips = pictures });
        tracks.Add(new { clips = voice });

        return JsonSerializer.Serialize(new
        {
            timeline = new { background = "#000000", tracks },
            output = new { format = "mp4", resolution = "hd", aspectRatio = plan.AspectRatio },
        }, Json);
    }

    private static double Number(double value) => Math.Round(value, 2);

    // ----- http ---------------------------------------------------------------------------------------------------------------------

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"https://{ApiHost}/edit/{_options.Value.Environment}/{path}");
        request.Headers.TryAddWithoutValidation("x-api-key", _options.Value.ShotstackApiKey.Trim());
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        response.Dispose();
        _logger.LogWarning("Shotstack answered {Status}.", (int)status);
        throw new VideoProviderException(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The video service rejected the platform's key. Ask the platform administrator to check it.",
            HttpStatusCode.TooManyRequests => "The video service is limiting requests right now. Try again in a little while.",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "The video service could not use this script or its pictures.",
            _ => "The video service could not be reached. Try again in a moment.",
        });
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new VideoProviderException("The video service gave an answer that could not be read.", ex);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}
