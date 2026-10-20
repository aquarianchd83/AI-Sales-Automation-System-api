using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Infrastructure.Storage;

namespace WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;

/// <summary>Looks up a host's addresses. Its own interface so the network guard can be tested without real DNS.</summary>
public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Keeps the server from being used to reach anything that is not the public internet (SSRF): loopback, private, link-local (which includes
/// cloud metadata at 169.254.169.254), carrier-grade NAT, multicast, broadcast and unspecified addresses are all refused, for IPv4, IPv6
/// and IPv4 mapped into IPv6. Only http and https on their standard ports.
/// </summary>
public static class NetworkGuard
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (HttpMediaUrlFetcher.IsInternal(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6Teredo;

        var b = ip.GetAddressBytes();
        return b[0] >= 224 // multicast and reserved, including 255.255.255.255
            || (b[0] == 198 && b[1] is 18 or 19); // benchmarking range, never a real site
    }

    /// <summary>The scheme, port and every address the host resolves to must be public, or the address is refused.</summary>
    public static async Task EnsurePublicAsync(Uri uri, IHostAddressResolver resolver, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new UnsafeUrlException("Only http:// and https:// addresses can be scanned.");
        if (!uri.IsDefaultPort)
            throw new UnsafeUrlException("Only websites on the normal web ports can be scanned.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new UnsafeUrlException("An address with a user name or password in it cannot be scanned.");

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) ? new[] { literal } : await resolver.ResolveAsync(uri.Host, cancellationToken);
        }
        catch (SocketException)
        {
            throw new UnsafeUrlException("That website address could not be found.");
        }

        if (addresses.Length == 0)
            throw new UnsafeUrlException("That website address could not be found.");
        if (addresses.Any(IsBlocked))
            throw new UnsafeUrlException("That address points to a private or internal network. Only public websites can be scanned.");
    }

    /// <summary>
    /// The handler for the crawler's HttpClient. Redirects are off (each hop is checked by the crawler), and the address is checked again
    /// at the moment the connection is made, against the address actually connected to - so a DNS answer that changes between the check
    /// and the request (DNS rebinding) still cannot reach an internal address.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var address = addresses.FirstOrDefault(a => !IsBlocked(a))
                ?? throw new HttpRequestException("Blocked: the address is on a private or internal network.");
            if (addresses.Any(IsBlocked))
                throw new HttpRequestException("Blocked: the address is on a private or internal network.");

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>
/// Reads the public pages of a tenant's website for the product-knowledge engine (see <see cref="IWebsiteCrawler"/>). Breadth first from the
/// start page: same site only (example.com and www.example.com count as one), up to the depth and page limits, robots.txt respected, HTML
/// pages only, each page size- and time-limited, up to 5 redirects per page and each hop re-checked. Pages with text identical to one already
/// read are kept out by the caller (it compares content hashes). Page text is untrusted: it is converted to plain markdown with scripts,
/// styles and hidden elements removed, and is treated as data, never as instructions.
/// </summary>
public sealed class WebsiteCrawler : IWebsiteCrawler
{
    public const string UserAgent = "SalesAutomationKnowledgeBot/1.0 (+reads the business's own public website for its product knowledge)";
    private const int MaxRedirects = 5;
    private const long MaxRobotsBytes = 64 * 1024;

    private static readonly Regex Href = new(@"<a\b[^>]*?\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleTag = new(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NoIndex = new(@"<meta\b[^>]*name\s*=\s*[""']robots[""'][^>]*content\s*=\s*[""'][^""']*noindex", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Links to these are files, not pages; they are not followed.</summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".zip", ".rar", ".7z", ".gz", ".doc", ".docx", ".xls", ".xlsx", ".ppt",
        ".pptx", ".mp3", ".mp4", ".mov", ".avi", ".webm", ".css", ".js", ".json", ".xml", ".rss", ".txt", ".exe", ".dmg", ".apk",
    };

    private readonly HttpClient _http;
    private readonly IHostAddressResolver _resolver;
    private readonly ILogger<WebsiteCrawler> _logger;

    public WebsiteCrawler(HttpClient http, IHostAddressResolver resolver, ILogger<WebsiteCrawler> logger)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own time limit
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>A pause between requests so a tenant's site is never hammered. Zero in tests.</summary>
    public TimeSpan PolitenessDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public async Task<CrawlResult> CrawlAsync(string startUrl, CrawlOptions options, CancellationToken cancellationToken = default)
    {
        Uri start;
        try
        {
            start = Normalise(ParseStart(startUrl));
            await NetworkGuard.EnsurePublicAsync(start, _resolver, cancellationToken);
        }
        catch (UnsafeUrlException ex)
        {
            return new CrawlResult(startUrl, Array.Empty<CrawledPage>(), ex.Message);
        }

        var robots = options.RespectRobots ? await LoadRobotsAsync(start, options, cancellationToken) : RobotsRules.AllowAll;
        var pages = new List<CrawledPage>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.AbsoluteUri };
        var queue = new Queue<(Uri Url, int Depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0 && pages.Count(p => p.Status == "Read") < options.MaxPages && pages.Count < options.MaxPages * 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (url, depth) = queue.Dequeue();

            if (!robots.Allows(url.PathAndQuery))
            {
                pages.Add(new CrawledPage(url.AbsoluteUri, depth, "Blocked", null, null, null, "The site's robots.txt asks not to read this page."));
                continue;
            }

            if (pages.Count > 0 && PolitenessDelay > TimeSpan.Zero)
                await Task.Delay(PolitenessDelay, cancellationToken);

            var (page, html, finalUrl) = await FetchAsync(url, depth, start, options, cancellationToken);
            pages.Add(page);
            if (html is null || finalUrl is null || depth >= options.MaxDepth || NoIndex.IsMatch(html))
                continue;

            foreach (var link in LinksOf(html, finalUrl, start))
            {
                if (seen.Add(link.AbsoluteUri))
                    queue.Enqueue((link, depth + 1));
            }
        }

        var read = pages.Count(p => p.Status == "Read");
        var error = read == 0
            ? pages.FirstOrDefault()?.Error ?? "No page of the website could be read."
            : null;
        return new CrawlResult(start.AbsoluteUri, pages, error);
    }

    // ----- one page ----------------------------------------------------------------------------------------------------------------

    private async Task<(CrawledPage Page, string? Html, Uri? FinalUrl)> FetchAsync(Uri url, int depth, Uri start, CrawlOptions options, CancellationToken ct)
    {
        var current = url;
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                await NetworkGuard.EnsurePublicAsync(current, _resolver, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.PageTimeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;

                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return (Failed(url, depth, status, "The page redirects without saying where."), null, null);
                    var next = Normalise(location.IsAbsoluteUri ? location : new Uri(current, location));
                    if (!SameSite(next, start))
                        return (new CrawledPage(url.AbsoluteUri, depth, "Skipped", status, null, null, $"Redirects to another website ({next.Host})."), null, null);
                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return (Failed(url, depth, status, $"The website answered {status}."), null, null);

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) && !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                    return (new CrawledPage(current.AbsoluteUri, depth, "Skipped", status, null, null, $"Not a web page ({(mediaType.Length == 0 ? "unknown type" : mediaType)})."), null, null);

                if (response.Content.Headers.ContentLength is { } declared && declared > options.MaxPageBytes)
                    return (new CrawledPage(current.AbsoluteUri, depth, "Skipped", status, null, null, "The page is too big to read."), null, null);

                var bytes = await ReadCappedAsync(response, options.MaxPageBytes, timeout.Token);
                if (bytes is null)
                    return (new CrawledPage(current.AbsoluteUri, depth, "Skipped", status, null, null, "The page is too big to read."), null, null);

                var html = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
                var title = TitleTag.Match(html) is { Success: true } t ? WebUtility.HtmlDecode(Regex.Replace(t.Groups[1].Value, @"\s+", " ")).Trim() : null;
                if (title is { Length: > 300 })
                    title = title[..300];
                var markdown = HtmlDocumentExtractor.ToMarkdown(html);

                return (new CrawledPage(current.AbsoluteUri, depth, "Read", status, title, markdown, null), html, current);
            }

            return (Failed(url, depth, null, "The page redirects too many times."), null, null);
        }
        catch (UnsafeUrlException ex)
        {
            return (new CrawledPage(url.AbsoluteUri, depth, "Blocked", null, null, null, ex.Message), null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (Failed(url, depth, null, "The page took too long to answer."), null, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("Website page could not be fetched: {Message}", ex.Message);
            return (Failed(url, depth, null, ex.Message.StartsWith("Blocked", StringComparison.Ordinal)
                ? "That address points to a private or internal network."
                : "The page could not be reached."), null, null);
        }
    }

    private static CrawledPage Failed(Uri url, int depth, int? status, string error) => new(url.AbsoluteUri, depth, "Failed", status, null, null, error);

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            return (string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'))).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    // ----- addresses ---------------------------------------------------------------------------------------------------------------

    private static Uri ParseStart(string startUrl)
    {
        var text = startUrl?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new UnsafeUrlException("Enter the website address.");
        if (!text.Contains("://"))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || (!uri.Host.Contains('.') && !IPAddress.TryParse(uri.Host, out _)))
            throw new UnsafeUrlException("That is not a website address. Use the form https://www.example.com.");
        return uri;
    }

    /// <summary>One spelling per page: lowercase host, no fragment, no default port, no tracking parameters, "/" for an empty path.</summary>
    public static Uri Normalise(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty, Host = uri.Host.ToLowerInvariant() };
        if (uri.IsDefaultPort)
            builder.Port = -1;
        if (builder.Path.Length == 0)
            builder.Path = "/";

        var query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("fbclid=", StringComparison.OrdinalIgnoreCase)
                            && !p.StartsWith("gclid=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            builder.Query = string.Join('&', kept);
        }

        return builder.Uri;
    }

    /// <summary>The same website: the same host, ignoring a leading "www.".</summary>
    public static bool SameSite(Uri a, Uri b) => string.Equals(Bare(a.Host), Bare(b.Host), StringComparison.OrdinalIgnoreCase);

    private static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>The same-site page links of a page, normalised, without files, mail or script links.</summary>
    public static IEnumerable<Uri> LinksOf(string html, Uri page, Uri start)
    {
        foreach (Match m in Href.Matches(html))
        {
            var raw = WebUtility.HtmlDecode((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim());
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(page, raw, out var link) || (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps))
                continue;
            if (!SameSite(link, start) || FileExtensions.Contains(Path.GetExtension(link.AbsolutePath)))
                continue;

            yield return Normalise(link);
        }
    }

    // ----- robots.txt --------------------------------------------------------------------------------------------------------------

    private async Task<RobotsRules> LoadRobotsAsync(Uri start, CrawlOptions options, CancellationToken ct)
    {
        try
        {
            var robotsUrl = new Uri(start, "/robots.txt");
            await NetworkGuard.EnsurePublicAsync(robotsUrl, _resolver, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.PageTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, robotsUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return RobotsRules.AllowAll; // no robots.txt (or an error): the convention is that everything may be read
            var bytes = await ReadCappedAsync(response, MaxRobotsBytes, timeout.Token);
            return bytes is null ? RobotsRules.AllowAll : RobotsRules.Parse(Encoding.UTF8.GetString(bytes), "SalesAutomationKnowledgeBot");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UnsafeUrlException && !ct.IsCancellationRequested)
        {
            return RobotsRules.AllowAll;
        }
    }
}

/// <summary>
/// The rules of a robots.txt for this crawler: the group naming it, else the "*" group. The longest matching rule wins, Allow beating
/// Disallow on a tie; "*" and a trailing "$" are understood, as the major search engines do.
/// </summary>
public sealed class RobotsRules
{
    public static readonly RobotsRules AllowAll = new(Array.Empty<(bool, string)>());

    private readonly IReadOnlyList<(bool Allow, string Pattern)> _rules;

    private RobotsRules(IReadOnlyList<(bool Allow, string Pattern)> rules) => _rules = rules;

    public static RobotsRules Parse(string text, string agentToken)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        (List<string> Agents, List<(bool, string)> Rules)? current = null;
        var lastWasAgent = false;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                if (current is null || !lastWasAgent)
                {
                    current = (new List<string>(), new List<(bool, string)>());
                    groups.Add(current.Value);
                }
                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (current is null)
                continue;
            if (field == "disallow" && value.Length > 0)
                current.Value.Rules.Add((false, value));
            else if (field == "allow" && value.Length > 0)
                current.Value.Rules.Add((true, value));
        }

        var token = agentToken.ToLowerInvariant();
        var mine = groups.Where(g => g.Agents.Any(a => a != "*" && token.Contains(a))).SelectMany(g => g.Rules).ToList();
        var rules = mine.Count > 0 ? mine : groups.Where(g => g.Agents.Contains("*")).SelectMany(g => g.Rules).ToList();
        return new RobotsRules(rules);
    }

    public bool Allows(string pathAndQuery)
    {
        var best = (Length: -1, Allow: true);
        foreach (var (allow, pattern) in _rules)
        {
            if (!Matches(pattern, pathAndQuery))
                continue;
            if (pattern.Length > best.Length || (pattern.Length == best.Length && allow))
                best = (pattern.Length, allow);
        }
        return best.Allow;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var body = anchored ? pattern[..^1] : pattern;
        var regex = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (anchored ? "$" : string.Empty);
        return Regex.IsMatch(path, regex);
    }
}
