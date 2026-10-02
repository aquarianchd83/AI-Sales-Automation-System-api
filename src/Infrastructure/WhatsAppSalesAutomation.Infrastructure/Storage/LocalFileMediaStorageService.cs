using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>
/// Stores media on local disk under the API's content root. Implements <see cref="IMediaStorageService"/>
/// so a cloud provider (Azure Blob/S3, per the Phase 1 default) can be dropped in later behind the
/// same interface without touching <c>MediaService</c>.
/// </summary>
public class LocalFileMediaStorageService : IMediaStorageService
{
    private readonly string _rootPath;
    private readonly LocalMediaStorageSettings _settings;

    public LocalFileMediaStorageService(IWebHostEnvironment environment, IOptions<LocalMediaStorageSettings> settings)
    {
        _settings = settings.Value;
        _rootPath = Path.IsPathRooted(_settings.RootPath)
            ? _settings.RootPath
            : Path.Combine(environment.ContentRootPath, _settings.RootPath);

        Directory.CreateDirectory(_rootPath);
    }

    public async Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        // yyyy/MM subfolders keep any one directory from accumulating years' worth of files.
        var relativeDir = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"));
        var safeExtension = Path.GetExtension(fileName);
        var storedFileName = $"{Guid.NewGuid():N}{safeExtension}";
        var relativeKey = Path.Combine(relativeDir, storedFileName).Replace('\\', '/');

        var fullDir = Path.Combine(_rootPath, relativeDir);
        Directory.CreateDirectory(fullDir);

        var fullPath = Path.Combine(_rootPath, relativeKey.Replace('/', Path.DirectorySeparatorChar));
        await using (var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write))
        {
            await content.CopyToAsync(fileStream, cancellationToken);
        }

        var url = GetPublicUrl(relativeKey);
        return new MediaStorageResult(relativeKey, url);
    }

    public string GetPublicUrl(string storageKey) =>
        $"{_settings.PublicBaseUrl?.Trim().TrimEnd('/')}{_settings.PublicBasePath}/{storageKey}";

    public bool IsPublicUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        return !(uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar)));

        // The key comes from our own database, but never let a bad one climb out of the media folder.
        if (!fullPath.StartsWith(Path.GetFullPath(_rootPath), StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Media key points outside the media folder.", storageKey);

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }
}
