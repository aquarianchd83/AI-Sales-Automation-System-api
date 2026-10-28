using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;
using WhatsAppSalesAutomation.Infrastructure.ContentStudio;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The Shotstack and Google text-to-speech adapters against a scripted HTTP handler (code-level tests, not live checks). Keys go in headers
/// only; the video is fetched from Shotstack's own hosts only, without the key, and never beyond the size limit; every failure is a sentence.
/// </summary>
public sealed class VideoProvidersTests
{
    private sealed class Snapshot : IOptionsSnapshot<RenderOptions>
    {
        public RenderOptions Value { get; set; } = new() { ShotstackApiKey = "shot-secret", GoogleTtsApiKey = "goog-secret", ShotstackEnvironment = "stage" };
        public RenderOptions Get(string? name) => Value;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return Respond(request);
        }
    }

    private readonly Handler _handler = new();
    private readonly Snapshot _options = new();

    private ShotstackVideoRenderer Renderer() => new(new HttpClient(_handler), _options, NullLogger<ShotstackVideoRenderer>.Instance);

    private GoogleTtsVoiceSynthesizer Voice() => new(new HttpClient(_handler), _options, NullLogger<GoogleTtsVoiceSynthesizer>.Instance);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static RenderPlan Plan() => new("16:9", new[]
    {
        new RenderScene("https://signed.example/img1", "https://signed.example/a1", 0, 6.4, "Welcome"),
        new RenderScene("https://signed.example/img2", "https://signed.example/a2", 6.4, 5.123456, null),
    });

    // ----- Shotstack -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_timeline_has_the_pictures_the_words_on_screen_and_the_narration_timed_to_the_voice()
    {
        using var doc = JsonDocument.Parse(ShotstackVideoRenderer.BuildBody(Plan()));
        var root = doc.RootElement;

        Assert.Equal(("mp4", "hd", "16:9"), (root.GetProperty("output").GetProperty("format").GetString(), root.GetProperty("output").GetProperty("resolution").GetString(), root.GetProperty("output").GetProperty("aspectRatio").GetString()));
        var tracks = root.GetProperty("timeline").GetProperty("tracks").EnumerateArray().ToList();
        Assert.Equal(3, tracks.Count); // words on top, then pictures, then voice

        var words = tracks[0].GetProperty("clips").EnumerateArray().ToList();
        var wordClip = Assert.Single(words); // only the scene that has words
        Assert.Equal(("title", "Welcome"), (wordClip.GetProperty("asset").GetProperty("type").GetString(), wordClip.GetProperty("asset").GetProperty("text").GetString()));

        var pictures = tracks[1].GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal(new[] { "https://signed.example/img1", "https://signed.example/img2" }, pictures.Select(c => c.GetProperty("asset").GetProperty("src").GetString()));
        Assert.Equal(new[] { 0.0, 6.4 }, pictures.Select(c => c.GetProperty("start").GetDouble()));
        Assert.Equal(new[] { 6.4, 5.12 }, pictures.Select(c => c.GetProperty("length").GetDouble()));

        var voice = tracks[2].GetProperty("clips").EnumerateArray().ToList();
        Assert.All(voice, c => Assert.Equal("audio", c.GetProperty("asset").GetProperty("type").GetString()));
    }

    [Fact]
    public void The_cinema_choices_become_the_effect_the_filter_and_the_transition_of_each_picture()
    {
        var plan = new RenderPlan("16:9", new[]
        {
            new RenderScene("https://x/i1", "https://x/a1", 0, 8, null, "PanLeft", "Warm", "Fade"),
            new RenderScene("https://x/i2", "https://x/a2", 8, 8, null, "Still", "Natural", "None"),
            new RenderScene("https://x/i3", "https://x/a3", 16, 8, null, "ZoomOut", "Dramatic", "Wipe"),
            new RenderScene("https://x/i4", "https://x/a4", 24, 8, null, "teleport", "Sparkly", "Dissolve"),
        });

        using var doc = JsonDocument.Parse(ShotstackVideoRenderer.BuildBody(plan));
        var pictures = doc.RootElement.GetProperty("timeline").GetProperty("tracks")[0].GetProperty("clips").EnumerateArray().ToList();

        Assert.Equal(("slideLeft", "boost", "fade"), (pictures[0].GetProperty("effect").GetString(), pictures[0].GetProperty("filter").GetString(), pictures[0].GetProperty("transition").GetProperty("in").GetString()));
        Assert.Equal("fade", pictures[0].GetProperty("transition").GetProperty("out").GetString());
        Assert.False(pictures[1].TryGetProperty("effect", out _)); // Still: no movement
        Assert.False(pictures[1].TryGetProperty("filter", out _));
        Assert.False(pictures[1].TryGetProperty("transition", out _));
        Assert.Equal(("zoomOut", "contrast", "wipeLeft"), (pictures[2].GetProperty("effect").GetString(), pictures[2].GetProperty("filter").GetString(), pictures[2].GetProperty("transition").GetProperty("in").GetString()));
        Assert.Equal("zoomIn", pictures[3].GetProperty("effect").GetString()); // anything unknown is the plain default
        Assert.False(pictures[3].TryGetProperty("filter", out _));
        Assert.False(pictures[3].TryGetProperty("transition", out _));
    }

    [Fact]
    public void The_clips_are_laid_end_to_end_on_one_track_with_their_own_sound_and_each_scenes_transition()
    {
        var plan = new MergePlan("9:16", new[]
        {
            new MergeClip("https://signed.example/c1", 0, 10, "None"),
            new MergeClip("https://signed.example/c2", 10, 10.4, "Fade"),
        });

        using var doc = JsonDocument.Parse(ShotstackVideoRenderer.BuildMergeBody(plan));
        var tracks = doc.RootElement.GetProperty("timeline").GetProperty("tracks");
        Assert.Equal(1, tracks.GetArrayLength());
        var clips = tracks[0].GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal(new[] { "https://signed.example/c1", "https://signed.example/c2" }, clips.Select(c => c.GetProperty("asset").GetProperty("src").GetString()));
        Assert.All(clips, c => Assert.Equal(("video", 1), (c.GetProperty("asset").GetProperty("type").GetString(), c.GetProperty("asset").GetProperty("volume").GetInt32())));
        Assert.Equal(new[] { 0.0, 10.0 }, clips.Select(c => c.GetProperty("start").GetDouble()));
        Assert.Equal(new[] { 10.0, 10.4 }, clips.Select(c => c.GetProperty("length").GetDouble()));
        Assert.False(clips[0].TryGetProperty("transition", out _));
        Assert.Equal("fade", clips[1].GetProperty("transition").GetProperty("in").GetString());
        Assert.Equal("9:16", doc.RootElement.GetProperty("output").GetProperty("aspectRatio").GetString());
    }

    [Fact]
    public async Task Joining_clips_is_submitted_like_any_render_and_its_id_returned()
    {
        _handler.Respond = _ => Json("{\"response\":{\"id\":\"merge-9\"}}");

        var id = await Renderer().SubmitMergeAsync(new MergePlan("16:9", new[] { new MergeClip("https://signed.example/c1", 0, 10, "None") }));

        Assert.Equal("merge-9", id);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.shotstack.io/edit/stage/render", request.RequestUri!.ToString());
        Assert.Equal("shot-secret", request.Headers.GetValues("x-api-key").Single());
        Assert.Contains("\"type\":\"video\"", _handler.Bodies.Single());

        _handler.Respond = _ => Json("{\"success\":true}");
        Assert.Contains("did not accept the clips", (await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().SubmitMergeAsync(new MergePlan("16:9", new[] { new MergeClip("https://x/c", 0, 10, "None") })))).Message);
    }

    [Fact]
    public void A_plan_with_no_words_on_screen_has_no_words_track()
    {
        var plan = new RenderPlan("9:16", new[] { new RenderScene("https://x/i", "https://x/a", 0, 5, "   ") });

        using var doc = JsonDocument.Parse(ShotstackVideoRenderer.BuildBody(plan));

        Assert.Equal(2, doc.RootElement.GetProperty("timeline").GetProperty("tracks").GetArrayLength());
        Assert.Equal("9:16", doc.RootElement.GetProperty("output").GetProperty("aspectRatio").GetString());
    }

    [Fact]
    public async Task Submitting_sends_the_key_in_a_header_to_the_chosen_environment_and_returns_the_renderers_id()
    {
        _handler.Respond = _ => Json("{\"success\":true,\"response\":{\"message\":\"Render Successfully Queued\",\"id\":\"abc-123\"}}");

        var id = await Renderer().SubmitAsync(Plan());

        Assert.Equal("abc-123", id);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.shotstack.io/edit/stage/render", request.RequestUri!.ToString());
        Assert.Equal("shot-secret", request.Headers.GetValues("x-api-key").Single());
        Assert.DoesNotContain("shot-secret", request.RequestUri.ToString());

        _options.Value = new RenderOptions { ShotstackApiKey = "k", ShotstackEnvironment = "v1" };
        _handler.Respond = _ => Json("{\"response\":{\"id\":\"x\"}}");
        await Renderer().SubmitAsync(Plan());
        Assert.Equal("https://api.shotstack.io/edit/v1/render", _handler.Requests.Last().RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the platform's key")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.BadRequest, "could not use this script")]
    [InlineData(HttpStatusCode.BadGateway, "could not be reached")]
    public async Task A_refusal_from_the_video_service_is_a_sentence_the_tenant_can_read(HttpStatusCode status, string expected)
    {
        _handler.Respond = _ => Json("{\"error\":\"secret details\"}", status);

        var ex = await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().SubmitAsync(Plan()));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("secret details", ex.Message);
    }

    [Fact]
    public async Task An_answer_without_an_id_or_that_is_not_json_is_a_clear_failure()
    {
        _handler.Respond = _ => Json("{\"success\":true}");
        Assert.Contains("did not accept", (await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().SubmitAsync(Plan()))).Message);

        _handler.Respond = _ => Json("<html>");
        Assert.Contains("could not be read", (await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().SubmitAsync(Plan()))).Message);
    }

    [Theory]
    [InlineData("queued", RenderPhase.Working)]
    [InlineData("fetching", RenderPhase.Working)]
    [InlineData("rendering", RenderPhase.Working)]
    [InlineData("saving", RenderPhase.Working)]
    [InlineData("whatever-new", RenderPhase.Working)]
    public async Task Progress_is_read_from_the_renderers_status(string status, RenderPhase phase)
    {
        _handler.Respond = _ => Json("{\"response\":{\"status\":\"" + status + "\"}}");

        var result = await Renderer().GetStatusAsync("abc-123");

        Assert.Equal(phase, result.Phase);
        Assert.InRange(result.Percent, 1, 95);
    }

    [Fact]
    public async Task A_finished_render_gives_its_address_and_a_failed_one_its_reason()
    {
        _handler.Respond = _ => Json("{\"response\":{\"status\":\"done\",\"url\":\"https://cdn.shotstack.io/v.mp4\"}}");
        var done = await Renderer().GetStatusAsync("abc-123");
        Assert.Equal((RenderPhase.Done, 100, "https://cdn.shotstack.io/v.mp4"), (done.Phase, done.Percent, done.Url));
        Assert.Equal("/edit/stage/render/abc-123", _handler.Requests.Single().RequestUri!.AbsolutePath);

        _handler.Respond = _ => Json("{\"response\":{\"status\":\"failed\",\"error\":\"Invalid image\"}}");
        var failed = await Renderer().GetStatusAsync("abc-123");
        Assert.Equal((RenderPhase.Failed, "Invalid image"), (failed.Phase, failed.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../x")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public async Task A_renderer_id_that_is_not_plain_is_never_put_in_an_address(string id)
    {
        await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().GetStatusAsync(id));

        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("https://cdn.shotstack.io/v.mp4", true)]
    [InlineData("https://shotstack-api-stage-output.s3-ap-southeast-2.amazonaws.com/x/v.mp4", true)]
    [InlineData("https://evil.example/v.mp4", false)]
    [InlineData("https://shotstack.io.evil.example/v.mp4", false)]
    [InlineData("https://other-bucket.s3.amazonaws.com/v.mp4", false)]
    [InlineData("http://cdn.shotstack.io/v.mp4", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    [InlineData("file:///etc/passwd", false)]
    public void Only_the_video_services_own_hosts_over_https_are_trusted_for_the_finished_file(string url, bool trusted) =>
        Assert.Equal(trusted, ShotstackVideoRenderer.IsOutputHost(url, out _));

    [Fact]
    public async Task The_finished_video_is_downloaded_without_the_key_and_within_the_size_limit()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2000]) };
        using var destination = new MemoryStream();

        await Renderer().DownloadAsync("https://cdn.shotstack.io/v.mp4", 5000, destination);

        Assert.Equal(2000, destination.Length);
        Assert.False(_handler.Requests.Single().Headers.Contains("x-api-key"));

        // Too big by its declared size, and too big while streaming (no declared size).
        await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().DownloadAsync("https://cdn.shotstack.io/v.mp4", 1000, new MemoryStream()));
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[2000])) };
        await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().DownloadAsync("https://cdn.shotstack.io/v.mp4", 1000, new MemoryStream()));
    }

    [Fact]
    public async Task Any_other_address_is_refused_before_a_request_is_made_and_a_failed_download_is_a_sentence()
    {
        await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().DownloadAsync("https://evil.example/v.mp4", 5000, new MemoryStream()));
        Assert.Empty(_handler.Requests);

        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Contains("could not be downloaded", (await Assert.ThrowsAsync<VideoProviderException>(() => Renderer().DownloadAsync("https://cdn.shotstack.io/v.mp4", 5000, new MemoryStream()))).Message);
    }

    // ----- Google text-to-speech -------------------------------------------------------------------------------------------------

    private static string Audio(int wavBytes) => "{\"audioContent\":\"" + Convert.ToBase64String(new byte[wavBytes]) + "\"}";

    [Fact]
    public async Task Narration_is_spoken_in_the_chosen_language_and_gender_with_the_key_in_a_header_and_the_length_worked_out_from_the_audio()
    {
        _handler.Respond = _ => Json(Audio(44 + 48000 * 3)); // 24 kHz, 16-bit, mono: 48,000 bytes a second

        var clip = await Voice().SynthesizeAsync("Namaste", "hi-IN", "Male");

        Assert.Equal(3.0, clip.Seconds);
        Assert.Equal(44 + 48000 * 3, clip.Wav.Length);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("texttospeech.googleapis.com", request.RequestUri!.Host);
        Assert.Equal("goog-secret", request.Headers.GetValues("X-Goog-Api-Key").Single());
        Assert.DoesNotContain("goog-secret", request.RequestUri.ToString());
        using var body = JsonDocument.Parse(_handler.Bodies.Single());
        Assert.Equal("Namaste", body.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.Equal(("hi-IN", "MALE"), (body.RootElement.GetProperty("voice").GetProperty("languageCode").GetString(), body.RootElement.GetProperty("voice").GetProperty("ssmlGender").GetString()));
        Assert.Equal(("LINEAR16", 24000), (body.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString(), body.RootElement.GetProperty("audioConfig").GetProperty("sampleRateHertz").GetInt32()));

        await Voice().SynthesizeAsync("Hello", "en-IN", "anything else");
        using var second = JsonDocument.Parse(_handler.Bodies.Last());
        Assert.Equal("FEMALE", second.RootElement.GetProperty("voice").GetProperty("ssmlGender").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rejected the platform's key")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.BadRequest, "could not speak this text")]
    [InlineData(HttpStatusCode.InternalServerError, "could not be reached")]
    public async Task A_refusal_from_the_voice_service_is_a_sentence_the_tenant_can_read(HttpStatusCode status, string expected)
    {
        _handler.Respond = _ => Json("{\"error\":{\"message\":\"internal detail\"}}", status);

        var ex = await Assert.ThrowsAsync<VideoProviderException>(() => Voice().SynthesizeAsync("Hi", "en-IN", "Female"));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("internal detail", ex.Message);
    }

    [Fact]
    public async Task No_audio_unreadable_audio_no_key_or_no_text_is_a_clear_failure_and_sends_nothing_when_nothing_can_be_said()
    {
        _handler.Respond = _ => Json("{}");
        Assert.Contains("no audio", (await Assert.ThrowsAsync<VideoProviderException>(() => Voice().SynthesizeAsync("Hi", "en-IN", "Female"))).Message);

        _handler.Respond = _ => Json(Audio(10)); // shorter than a WAV header
        Assert.Contains("no audio", (await Assert.ThrowsAsync<VideoProviderException>(() => Voice().SynthesizeAsync("Hi", "en-IN", "Female"))).Message);

        _handler.Respond = _ => Json("{\"audioContent\":\"***not base64***\"}");
        Assert.Contains("could not be read", (await Assert.ThrowsAsync<VideoProviderException>(() => Voice().SynthesizeAsync("Hi", "en-IN", "Female"))).Message);

        _handler.Requests.Clear();
        await Assert.ThrowsAsync<VideoProviderException>(() => Voice().SynthesizeAsync("   ", "en-IN", "Female"));
        _options.Value = new RenderOptions();
        Assert.False(Voice().IsConfigured);
        Assert.Contains("not set up", (await Assert.ThrowsAsync<VideoProviderException>(() => Voice().SynthesizeAsync("Hi", "en-IN", "Female"))).Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void The_render_and_voice_keys_are_secret_platform_settings_with_a_sandbox_default()
    {
        var all = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All;
        Assert.True(all.Single(d => d.Key == "ContentStudio:Render:ShotstackApiKey").IsSecret);
        Assert.True(all.Single(d => d.Key == "ContentStudio:Render:GoogleTtsApiKey").IsSecret);
        var environment = all.Single(d => d.Key == "ContentStudio:Render:ShotstackEnvironment");
        Assert.Equal("stage", environment.DefaultValue);
        Assert.Equal(new[] { "stage", "v1" }, environment.Options);
        Assert.Equal("3", all.Single(d => d.Key == "ContentStudio:Render:MaxConcurrent").DefaultValue);
    }
}
