using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>The outcome of one page fetch: the readable text on success, or why it could not be read.</summary>
public sealed record PageFetchResult(bool Ok, string? Text, string? Error);

/// <summary>
/// Fetches a web page and reduces it to readable text, for the OpenAI discovery agent - OpenAI's hosted web
/// search returns citations but not page text, and LeadQualification verifies phone numbers and emails against
/// the text of pages the agent really read. (Claude's own web_fetch tool does this server-side.)
///
/// The model chooses the URLs, so they are untrusted: only http(s) on ports 80/443 is allowed, redirects are
/// followed by hand, and every connection is checked at connect time to be a public address (see
/// <see cref="ConnectToPublicAddressAsync"/>), which keeps a prompt-injected page from pointing the server at
/// localhost, a private network or a cloud metadata endpoint - including through a redirect or a DNS answer
/// that changes between lookups.
///
/// Phone numbers and emails that live only in <c>tel:</c> / <c>mailto:</c> links or JSON-LD are written into the
/// text, ahead of the body, so they survive both the tag stripping and the length cap.
/// </summary>
public sealed class LeadDiscoveryPageFetcher
{
    private const int MaxRedirects = 4;
    private const int MaxBodyBytes = 1_500_000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex TelOrMailHref = new(@"href\s*=\s*[""']?\s*(tel|mailto):([^""'>\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsonLd = new(@"<script[^>]*application/ld\+json[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex NonContent = new(@"<(script|style|noscript|svg)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex BlockBreak = new(@"</?(p|div|br|li|ul|ol|tr|table|h[1-6]|section|article|header|footer|address|nav|main)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Spaces = new(@"[ \t\f\v ]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex BlankLines = new(@"\s*\n\s*", RegexOptions.Compiled, RegexTimeout);

    private readonly HttpClient _http;

    public LeadDiscoveryPageFetcher(HttpClient http)
    {
        _http = http;
    }

    /// <summary>The primary handler for this fetcher's HttpClient: no automatic redirects, no proxy, and a
    /// connect callback that only ever connects to public addresses.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectToPublicAddressAsync
    };

    /// <param name="maxChars">How much of the page text to return (and for the caller to treat as read).</param>
    public async Task<PageFetchResult> FetchAsync(string? rawUrl, int maxChars, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri) || !IsHttp(uri))
            return Fail("not a valid http(s) URL");

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (uri.Port is not (80 or 443))
                    return Fail("only ports 80 and 443 can be fetched");

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
                request.Headers.AcceptLanguage.ParseAdd("en;q=0.9");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!IsHttp(uri))
                        return Fail("redirected to a non-http(s) address");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return Fail($"HTTP {(int)response.StatusCode}");

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
                var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
                if (!isHtml && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                            && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                            && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                {
                    return Fail($"not a text page ({mediaType})");
                }

                var raw = await ReadLimitedAsync(response, cancellationToken);
                var text = isHtml ? HtmlToText(raw) : raw;
                if (text.Length > maxChars)
                    text = text[..maxChars];

                return new PageFetchResult(true, text, null);
            }

            return Fail("too many redirects");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("timed out");
        }
        catch (HttpRequestException)
        {
            return Fail("could not connect to the site");
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("page could not be read");
        }
    }

    private static PageFetchResult Fail(string error) => new(false, null, error);

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxBodyBytes && (read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            buffer.Write(chunk, 0, read);

        Encoding encoding;
        try
        {
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static string HtmlToText(string html)
    {
        var header = new List<string>();

        foreach (Match match in TelOrMailHref.Matches(html))
        {
            var value = Uri.UnescapeDataString(match.Groups[2].Value);
            var queryStart = value.IndexOf('?');
            if (queryStart >= 0)
                value = value[..queryStart];
            var line = $"{match.Groups[1].Value.ToLowerInvariant()}: {value.Trim()}";
            if (!header.Contains(line))
                header.Add(line);
        }

        foreach (Match match in JsonLd.Matches(html))
            header.Add(WebUtility.HtmlDecode(match.Groups[1].Value).Trim());

        var body = Comments.Replace(html, " ");
        body = NonContent.Replace(body, " ");
        body = BlockBreak.Replace(body, "\n");
        body = Tags.Replace(body, " ");
        body = WebUtility.HtmlDecode(body);
        body = Spaces.Replace(body, " ");
        body = BlankLines.Replace(body, "\n").Trim();

        return header.Count == 0 ? body : string.Join("\n", header) + "\n\n" + body;
    }

    /// <summary>Resolves the host itself and connects only to a public address, so the check applies to the
    /// address actually used (not a separate earlier lookup) and to every redirect hop.</summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("blocked: the host does not resolve to a public address");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0
                     || bytes[0] == 10
                     || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                     || (bytes[0] == 169 && bytes[1] == 254)
                     || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                     || (bytes[0] == 192 && bytes[1] == 168)
                     || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                     || bytes[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal
                     || address.IsIPv6SiteLocal
                     || address.IsIPv6Multicast
                     || (bytes[0] & 0xFE) == 0xFC);
        }

        return false;
    }
}
