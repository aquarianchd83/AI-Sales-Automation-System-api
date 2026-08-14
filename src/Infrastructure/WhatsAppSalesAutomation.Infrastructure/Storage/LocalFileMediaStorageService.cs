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

        var url = $"{_settings.PublicBaseUrl}{_settings.PublicBasePath}/{relativeKey}";
        return new MediaStorageResult(relativeKey, url);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }
}
