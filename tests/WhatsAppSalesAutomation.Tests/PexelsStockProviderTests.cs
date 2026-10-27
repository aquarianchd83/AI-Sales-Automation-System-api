using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Assets;
using WhatsAppSalesAutomation.Infrastructure.ContentStudio;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The Pexels adapter, against a scripted HTTP handler (these are code-level tests, not a live check of Pexels). It must keep the platform's key
/// in a header and away from the image host, accept photos only from Pexels' own hosts, and turn every failure into a sentence.
/// </summary>
public sealed class PexelsStockProviderTests
{
    private sealed class Snapshot : IOptionsSnapshot<StockOptions>
    {
        public StockOptions Value { get; set; } = new() { PexelsApiKey = "secret-key" };
        public StockOptions Get(string? name) => Value;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private readonly Handler _handler = new();
    private readonly Snapshot _options = new();

    private PexelsStockProvider Provider() => new(new HttpClient(_handler), _options, NullLogger<PexelsStockProvider>.Instance);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string PhotoJson(long id = 2014422, string imageHost = "images.pexels.com", string page = "https://www.pexels.com/photo/2014422/") =>
        "{\"id\":" + id + ",\"width\":4000,\"height\":3000,\"url\":\"" + page + "\",\"photographer\":\"Jane Doe\",\"photographer_url\":\"https://www.pexels.com/@jane\",\"alt\":\"A clinic reception\"," +
        "\"src\":{\"original\":\"https://" + imageHost + "/photos/" + id + "/o.jpg\",\"large2x\":\"https://" + imageHost + "/photos/" + id + "/l2.jpg\",\"large\":\"https://" + imageHost + "/photos/" + id + "/l.jpg\"," +
        "\"medium\":\"https://" + imageHost + "/photos/" + id + "/m.jpg\",\"small\":\"https://" + imageHost + "/photos/" + id + "/s.jpg\"}}";

    [Fact]
    public async Task A_search_sends_the_key_in_a_header_asks_for_landscape_and_maps_the_photos_with_licence_and_credit()
    {
        _handler.Respond = _ => Json("{\"photos\":[" + PhotoJson() + "]}");

        var result = await Provider().SearchAsync("eye clinic & reception", 12);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("api.pexels.com", request.RequestUri!.Host);
        Assert.Contains("query=eye%20clinic%20%26%20reception", request.RequestUri.Query);
        Assert.Contains("orientation=landscape", request.RequestUri.Query);
        Assert.DoesNotContain("secret-key", request.RequestUri.ToString());
        Assert.Equal("secret-key", request.Headers.GetValues("Authorization").Single());

        Assert.Null(result.Message);
        var photo = Assert.Single(result.Photos);
        Assert.Equal(("2014422", "Pexels", "Pexels License", "Photo by Jane Doe on Pexels"), (photo.ProviderAssetId, photo.Provider, photo.LicenseName, photo.Attribution));
        Assert.EndsWith("/m.jpg", photo.PreviewUrl);
        Assert.EndsWith("/l2.jpg", photo.DownloadUrl);
    }

    [Fact]
    public async Task A_photo_that_points_anywhere_but_pexels_own_image_host_is_ignored()
    {
        _handler.Respond = _ => Json("{\"photos\":[" + PhotoJson(1, "evil.example") + "," + PhotoJson(2, page: "http://www.pexels.com/photo/2/") + "," + PhotoJson(3) + ",{\"id\":4},\"junk\"]}");

        var result = await Provider().SearchAsync("clinic", 5);

        Assert.Equal(new[] { "3" }, result.Photos.Select(p => p.ProviderAssetId));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the platform's key")]
    [InlineData(HttpStatusCode.Forbidden, "rejected the platform's key")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.InternalServerError, "could not be reached")]
    public async Task A_failing_search_comes_back_as_a_sentence_never_an_exception(HttpStatusCode status, string expected)
    {
        _handler.Respond = _ => Json("{}", status);

        var result = await Provider().SearchAsync("clinic", 5);

        Assert.Empty(result.Photos);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task A_network_error_or_unreadable_answer_is_also_a_sentence_and_without_a_key_nothing_is_sent()
    {
        _handler.Respond = _ => throw new HttpRequestException("down");
        Assert.Contains("could not be reached", (await Provider().SearchAsync("clinic", 5)).Message);

        _handler.Respond = _ => Json("not json");
        Assert.Contains("could not be reached", (await Provider().SearchAsync("clinic", 5)).Message);

        _handler.Requests.Clear();
        _options.Value = new StockOptions();
        var provider = Provider();
        Assert.False(provider.IsConfigured);
        Assert.Contains("not set up", (await provider.SearchAsync("clinic", 5)).Message);
        Assert.Null(await provider.GetAsync("2014422"));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_photo_is_looked_up_by_a_numeric_id_only()
    {
        _handler.Respond = _ => Json(PhotoJson(555));

        var photo = await Provider().GetAsync("555");
        Assert.Equal("555", photo!.ProviderAssetId);
        Assert.Equal("/v1/photos/555", _handler.Requests.Single().RequestUri!.AbsolutePath);

        _handler.Requests.Clear();
        foreach (var bad in new[] { "", "../../etc", "12ab", "1/2", new string('9', 30) })
            Assert.Null(await Provider().GetAsync(bad));
        Assert.Empty(_handler.Requests);

        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await Provider().GetAsync("556"));
    }

    private static StockPhoto PhotoAt(string downloadUrl) => new("1", "Pexels", "https://images.pexels.com/p.jpg", downloadUrl, "https://www.pexels.com/photo/1/", "J", null, 1, 1, null, "Pexels License", "https://www.pexels.com/license/");

    [Fact]
    public async Task The_picture_is_fetched_from_pexels_image_host_only_without_the_key_and_within_the_size_limit()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) };

        var image = await Provider().DownloadAsync(PhotoAt("https://images.pexels.com/photos/1/l2.jpg"), 5000);

        Assert.Equal(1000, image.Content.Length);
        Assert.False(_handler.Requests.Single().Headers.Contains("Authorization")); // the key never goes to the image host

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider().DownloadAsync(PhotoAt("https://images.pexels.com/photos/1/l2.jpg"), 500));
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[1000])) }; // no declared length
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider().DownloadAsync(PhotoAt("https://images.pexels.com/photos/1/l2.jpg"), 500));
    }

    [Theory]
    [InlineData("https://evil.example/photos/1.jpg")]
    [InlineData("http://images.pexels.com/photos/1.jpg")]
    [InlineData("https://images.pexels.com.evil.example/1.jpg")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Any_other_address_is_refused_before_a_request_is_made(string url)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider().DownloadAsync(PhotoAt(url), 5000));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_failed_download_is_an_error_the_caller_can_explain()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider().DownloadAsync(PhotoAt("https://images.pexels.com/photos/1/l2.jpg"), 5000));
    }
}
