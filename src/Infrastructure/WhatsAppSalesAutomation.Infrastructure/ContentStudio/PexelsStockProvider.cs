using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Assets;

namespace WhatsAppSalesAutomation.Infrastructure.ContentStudio;

/// <summary>
/// Pexels as the stock photo library. Searches use the platform's API key, sent in a header and never logged. A chosen photo is always
/// looked up again by its id (the browser is never trusted for a URL), and the picture is fetched only from Pexels' own image host over
/// https, without the API key, with redirects off and the same network guard the website reader uses - so this cannot be turned into a way
/// to reach anything else. Every failure comes back as a sentence, not an exception, so a stock outage never breaks the picker.
/// </summary>
public sealed class PexelsStockProvider : IStockAssetProvider
{
    public const string ApiHost = "api.pexels.com";
    public const string ImageHost = "images.pexels.com";
    private const string LicenseName = "Pexels License";
    private const string LicenseUrl = "https://www.pexels.com/license/";

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<StockOptions> _options;
    private readonly ILogger<PexelsStockProvider> _logger;

    public PexelsStockProvider(HttpClient http, IOptionsSnapshot<StockOptions> options, ILogger<PexelsStockProvider> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Name => "Pexels";

    public bool IsConfigured => _options.Value.IsConfigured;

    public async Task<StockSearchResult> SearchAsync(string query, int count, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new StockSearchResult(Array.Empty<StockPhoto>(), "Stock photo search is not set up on this platform yet.");

        var url = $"https://{ApiHost}/v1/search?query={Uri.EscapeDataString(query.Trim())}&per_page={Math.Clamp(count, 1, 30)}&orientation=landscape";
        try
        {
            using var response = await SendAsync(url, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new StockSearchResult(Array.Empty<StockPhoto>(), "The stock photo service rejected the platform's key. Ask the platform administrator to check it.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new StockSearchResult(Array.Empty<StockPhoto>(), "The stock photo service is limiting requests right now. Try again in a little while.");
            if (!response.IsSuccessStatusCode)
                return new StockSearchResult(Array.Empty<StockPhoto>(), "The stock photo service could not be reached. Try again in a moment.");

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var photos = new List<StockPhoto>();
            if (doc.RootElement.TryGetProperty("photos", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var item in array.EnumerateArray())
                    if (TryRead(item, out var photo))
                        photos.Add(photo);
            return new StockSearchResult(photos, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Pexels search failed.");
            return new StockSearchResult(Array.Empty<StockPhoto>(), "The stock photo service could not be reached. Try again in a moment.");
        }
    }

    public async Task<StockPhoto?> GetAsync(string providerAssetId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || providerAssetId.Length is 0 or > 20 || !providerAssetId.All(char.IsDigit))
            return null;
        try
        {
            using var response = await SendAsync($"https://{ApiHost}/v1/photos/{providerAssetId}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return TryRead(doc.RootElement, out var photo) ? photo : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Pexels photo lookup failed.");
            return null;
        }
    }

    public async Task<StockImage> DownloadAsync(StockPhoto photo, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(photo.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, ImageHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The picture is not on the stock library's own image host.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri); // no Authorization header: the key never goes to the image host
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The image host answered {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidOperationException("The picture is larger than the allowed size.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException("The picture is larger than the allowed size.");
            buffer.Write(chunk, 0, read);
        }

        return new StockImage(buffer.ToArray());
    }

    private Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", _options.Value.PexelsApiKey.Trim());
        return _http.SendAsync(request, ct);
    }

    private static bool TryRead(JsonElement item, out StockPhoto photo)
    {
        photo = null!;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !item.TryGetProperty("src", out var src))
            return false;

        var preview = Text(src, "medium") ?? Text(src, "small");
        var download = Text(src, "large2x") ?? Text(src, "large") ?? Text(src, "original");
        if (preview is null || download is null || !IsImageHost(preview) || !IsImageHost(download))
            return false;

        var page = Text(item, "url");
        if (page is null || !Uri.TryCreate(page, UriKind.Absolute, out var pageUri) || pageUri.Scheme != Uri.UriSchemeHttps)
            return false;

        photo = new StockPhoto(
            id.GetInt64().ToString(), "Pexels", preview, download, page, Text(item, "photographer") ?? "an unnamed photographer", Text(item, "photographer_url"),
            item.TryGetProperty("width", out var w) && w.TryGetInt32(out var width) ? width : 0, item.TryGetProperty("height", out var h) && h.TryGetInt32(out var height) ? height : 0,
            Text(item, "alt"), LicenseName, LicenseUrl);
        return true;
    }

    private static bool IsImageHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, ImageHost, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}
