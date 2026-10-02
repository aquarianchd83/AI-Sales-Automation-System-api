using System.Net;
using System.Net.Sockets;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Storage;

public class HttpMediaUrlFetcher : IMediaUrlFetcher
{
    private readonly HttpClient _http;

    public HttpMediaUrlFetcher(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<MediaUrlFetchResult> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new MediaUrlFetchException("Enter a full link starting with https:// (or http://).");

        await EnsureNotInternalAsync(uri, cancellationToken);

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Redirects are switched off (see DependencyInjection): the next hop would skip the address check above.
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new MediaUrlFetchException("That link redirects somewhere else. Use the final link of the file itself.");
            if (!response.IsSuccessStatusCode)
                throw new MediaUrlFetchException($"The link could not be downloaded (the server answered {(int)response.StatusCode}).");

            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                throw new MediaUrlFetchException($"The file is larger than the {maxBytes / (1024 * 1024)} MB limit.");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new MediaUrlFetchException($"The file is larger than the {maxBytes / (1024 * 1024)} MB limit.");
                buffer.Write(chunk, 0, read);
            }

            var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return new MediaUrlFetchResult(buffer.ToArray(), contentType, fileName);
        }
        catch (HttpRequestException)
        {
            throw new MediaUrlFetchException("The link could not be reached. Check it opens in a browser without signing in.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaUrlFetchException("The link took too long to answer.");
        }
    }

    /// <summary>The server fetches whatever the tenant types, so keep it away from loopback, private and link-local ranges.</summary>
    private static async Task EnsureNotInternalAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (SocketException)
        {
            throw new MediaUrlFetchException("That address could not be found.");
        }

        if (addresses.Length == 0 || addresses.Any(IsInternal))
            throw new MediaUrlFetchException("That link points to a private address. Use a public link.");
    }

    internal static bool IsInternal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;

        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || b[0] == 0;
    }
}
