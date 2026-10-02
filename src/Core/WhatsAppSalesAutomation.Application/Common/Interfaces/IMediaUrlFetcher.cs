namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Downloads a file from a URL the tenant typed in, for the Media Library's "add by link" option.
/// Implemented in Infrastructure. Refuses addresses that point inside the server's own network.</summary>
public interface IMediaUrlFetcher
{
    /// <param name="maxBytes">Stops reading (and fails) past this size.</param>
    /// <exception cref="MediaUrlFetchException">The link cannot be used; the message is safe to show the tenant.</exception>
    Task<MediaUrlFetchResult> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken = default);
}

/// <param name="ContentType">From the response header, without parameters (e.g. "image/png").</param>
/// <param name="FileName">The last segment of the URL path, or empty when it has none.</param>
public record MediaUrlFetchResult(byte[] Content, string ContentType, string FileName);

public class MediaUrlFetchException : Exception
{
    public MediaUrlFetchException(string message) : base(message) { }
}
