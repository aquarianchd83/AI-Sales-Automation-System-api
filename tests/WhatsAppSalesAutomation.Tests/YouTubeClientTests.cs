using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;
using WhatsAppSalesAutomation.Infrastructure.ContentStudio;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The YouTube and Google sign-in adapter against a scripted HTTP handler (code-level tests, not a live check of Google). The client secret and
/// tokens go in bodies and headers only; every refusal is classified so the caller knows whether to retry, give up, reconnect, or warn that the
/// video may already be there.
/// </summary>
public sealed class YouTubeClientTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    private sealed class Snapshot : IOptionsSnapshot<SocialOptions>
    {
        public SocialOptions Value { get; set; } = new() { GoogleClientId = "client-id", GoogleClientSecret = "client-secret" };
        public SocialOptions Get(string? name) => Value;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null || request.Content is StreamContent ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.Content is StreamContent)
                await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return Respond(request);
        }
    }

    private readonly Handler _handler = new();
    private readonly Snapshot _options = new();

    private YouTubeClient Client() => new(new HttpClient(_handler), _options, new Clock(), NullLogger<YouTubeClient>.Instance);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string GoogleError(string reason, string message = "details") => "{\"error\":{\"code\":403,\"message\":\"" + message + "\",\"errors\":[{\"reason\":\"" + reason + "\"}]}}";

    private static readonly YouTubeUploadRequest Upload = new("Cataract surgery explained", "A short explainer.", "private", true);

    // ----- signing in ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_login_address_asks_for_long_term_upload_access_and_never_contains_the_secret()
    {
        var url = Client().BuildLoginUrl("https://app.example/content-studio/connections", "state value/+");

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=client-id", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("prompt=consent", url);
        Assert.Contains("youtube.upload", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fapp.example%2Fcontent-studio%2Fconnections", url);
        Assert.Contains("state=state%20value%2F%2B", url);
        Assert.DoesNotContain("client-secret", url);
    }

    [Fact]
    public async Task A_code_is_exchanged_for_tokens_with_the_secret_in_the_body_and_the_expiry_worked_out_from_the_clock()
    {
        _handler.Respond = _ => Json("{\"access_token\":\"acc\",\"expires_in\":3600,\"refresh_token\":\"ref\",\"scope\":\"https://www.googleapis.com/auth/youtube.upload\"}");

        var tokens = await Client().ExchangeCodeAsync("the-code", "https://app.example/x");

        Assert.Equal(("acc", "ref", Now.AddHours(1)), (tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtUtc));
        Assert.Contains("youtube.upload", tokens.Scopes);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.ToString());
        var body = _handler.Bodies.Single();
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("client_secret=client-secret", body);
        Assert.Contains("code=the-code", body);
        Assert.DoesNotContain("client-secret", request.RequestUri.ToString());
    }

    [Fact]
    public async Task Renewing_returns_a_new_access_token_and_a_refused_renewal_means_connect_again()
    {
        _handler.Respond = _ => Json("{\"access_token\":\"acc2\",\"expires_in\":1800}");
        var tokens = await Client().RefreshAsync("ref");
        Assert.Equal(("acc2", null), (tokens.AccessToken, tokens.RefreshToken));
        Assert.Contains("grant_type=refresh_token", _handler.Bodies.Single());

        _handler.Respond = _ => Json("{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<SocialProviderException>(() => Client().RefreshAsync("ref"));
        Assert.Equal(SocialFailure.NeedsReconnect, ex.Failure);
        Assert.Contains("Connect your YouTube account again", ex.Message);
        Assert.DoesNotContain("expired or revoked", ex.Message); // Google's own text is not passed on
    }

    [Fact]
    public async Task A_bad_code_is_permanent_and_google_having_a_bad_day_is_temporary()
    {
        _handler.Respond = _ => Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest);
        Assert.Equal(SocialFailure.Permanent, (await Assert.ThrowsAsync<SocialProviderException>(() => Client().ExchangeCodeAsync("c", "https://app.example/x"))).Failure);

        _handler.Respond = _ => Json("{}", HttpStatusCode.ServiceUnavailable);
        Assert.Equal(SocialFailure.Transient, (await Assert.ThrowsAsync<SocialProviderException>(() => Client().ExchangeCodeAsync("c", "https://app.example/x"))).Failure);

        _handler.Respond = _ => throw new HttpRequestException("down");
        Assert.Equal(SocialFailure.Transient, (await Assert.ThrowsAsync<SocialProviderException>(() => Client().RefreshAsync("r"))).Failure);
    }

    [Fact]
    public async Task The_channel_is_read_with_the_token_in_a_header_and_an_account_without_one_gives_nothing()
    {
        _handler.Respond = _ => Json("{\"items\":[{\"id\":\"UC123\",\"snippet\":{\"title\":\"Clinic Channel\"}}]}");
        var channel = await Client().GetChannelAsync("acc");
        Assert.Equal(("UC123", "Clinic Channel"), (channel!.Id, channel.Title));
        Assert.Equal("Bearer acc", _handler.Requests.Single().Headers.Authorization!.ToString());
        Assert.DoesNotContain("acc", _handler.Requests.Single().RequestUri!.ToString());

        _handler.Respond = _ => Json("{\"items\":[]}");
        Assert.Null(await Client().GetChannelAsync("acc"));
    }

    [Fact]
    public async Task Revoking_sends_the_token_in_the_body()
    {
        _handler.Respond = _ => Json("{}");

        await Client().RevokeAsync("secret-refresh");

        Assert.Equal("https://oauth2.googleapis.com/revoke", _handler.Requests.Single().RequestUri!.ToString());
        Assert.Contains("token=secret-refresh", _handler.Bodies.Single());
    }

    // ----- uploading -------------------------------------------------------------------------------------------------------------

    private HttpResponseMessage Session(string location = "https://www.googleapis.com/upload/youtube/v3/videos?upload_id=abc")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new Uri(location);
        return response;
    }

    [Fact]
    public async Task A_video_is_uploaded_in_two_steps_with_the_details_the_disclosure_and_the_whole_file()
    {
        _handler.Respond = request => request.Method == HttpMethod.Post ? Session() : Json("{\"id\":\"abc123XYZ\"}");
        var video = new MemoryStream(new byte[5000]);

        var id = await Client().UploadAsync("acc", Upload, video, 5000);

        Assert.Equal("abc123XYZ", id);
        var start = _handler.Requests[0];
        Assert.Equal(HttpMethod.Post, start.Method);
        Assert.Contains("uploadType=resumable", start.RequestUri!.Query);
        Assert.Equal("Bearer acc", start.Headers.Authorization!.ToString());
        Assert.Equal("5000", start.Headers.GetValues("X-Upload-Content-Length").Single());
        using var body = JsonDocument.Parse(_handler.Bodies[0]);
        Assert.Equal("Cataract surgery explained", body.RootElement.GetProperty("snippet").GetProperty("title").GetString());
        var status = body.RootElement.GetProperty("status");
        Assert.Equal(("private", false, true), (status.GetProperty("privacyStatus").GetString(), status.GetProperty("selfDeclaredMadeForKids").GetBoolean(), status.GetProperty("containsSyntheticMedia").GetBoolean()));

        var send = _handler.Requests[1];
        Assert.Equal(HttpMethod.Put, send.Method);
        Assert.Equal("www.googleapis.com", send.RequestUri!.Host);
        Assert.Equal(5000, send.Content!.Headers.ContentLength);
    }

    [Fact]
    public async Task An_upload_address_that_is_not_googles_is_never_followed()
    {
        _handler.Respond = _ => Session("https://evil.example/steal");

        var ex = await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10));

        Assert.Equal(SocialFailure.Permanent, ex.Failure);
        Assert.Single(_handler.Requests); // the video was never sent anywhere
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "quotaExceeded", SocialFailure.Permanent, "daily allowance")]
    [InlineData(HttpStatusCode.Forbidden, "uploadLimitExceeded", SocialFailure.Permanent, "upload limit")]
    [InlineData(HttpStatusCode.Forbidden, "youtubeSignupRequired", SocialFailure.Permanent, "needs a YouTube channel")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", SocialFailure.NeedsReconnect, "Connect it again")]
    [InlineData(HttpStatusCode.BadRequest, "invalidTitle", SocialFailure.Permanent, "title or description")]
    [InlineData(HttpStatusCode.Unauthorized, "authError", SocialFailure.NeedsReconnect, "Connect it again")]
    [InlineData(HttpStatusCode.TooManyRequests, "", SocialFailure.Transient, "limiting requests")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", SocialFailure.Transient, "problem on its side")]
    public async Task A_refusal_when_the_upload_is_announced_is_classified_before_anything_was_sent(HttpStatusCode status, string reason, SocialFailure expected, string words)
    {
        _handler.Respond = _ => Json(GoogleError(reason, "secret internal detail"), status);

        var ex = await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10));

        Assert.Equal(expected, ex.Failure);
        Assert.Contains(words, ex.Message);
        Assert.DoesNotContain("secret internal detail", ex.Message); // Google's own text is not passed on
    }

    [Fact]
    public async Task Losing_contact_before_the_upload_starts_is_temporary_but_during_it_is_ambiguous()
    {
        _handler.Respond = _ => throw new HttpRequestException("down");
        Assert.Equal(SocialFailure.Transient, (await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10))).Failure);

        _handler.Requests.Clear();
        _handler.Respond = request => request.Method == HttpMethod.Post ? Session() : throw new HttpRequestException("reset");
        var ex = await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10));
        Assert.Equal(SocialFailure.Ambiguous, ex.Failure);
        Assert.Contains("may or may not be on YouTube", ex.Message);
    }

    [Fact]
    public async Task A_server_error_after_the_bytes_started_is_ambiguous_while_a_rejected_file_is_permanent()
    {
        _handler.Respond = request => request.Method == HttpMethod.Post ? Session() : Json("{}", HttpStatusCode.InternalServerError);
        Assert.Equal(SocialFailure.Ambiguous, (await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10))).Failure);

        _handler.Respond = request => request.Method == HttpMethod.Post ? Session() : Json(GoogleError("invalidVideo"), HttpStatusCode.BadRequest);
        var rejected = await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10));
        Assert.Equal(SocialFailure.Permanent, rejected.Failure);
        Assert.Contains("could not use the video file", rejected.Message);
    }

    [Fact]
    public async Task An_answer_with_no_video_id_after_sending_is_ambiguous()
    {
        _handler.Respond = request => request.Method == HttpMethod.Post ? Session() : Json("{}");

        var ex = await Assert.ThrowsAsync<SocialProviderException>(() => Client().UploadAsync("acc", Upload, new MemoryStream(new byte[10]), 10));

        Assert.Equal(SocialFailure.Ambiguous, ex.Failure);
    }
}
