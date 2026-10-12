using System.Security.Cryptography;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

public enum StudioStorageZone
{
    Uploads = 0,
    Renders = 1,
    Audio = 2
}

/// <summary>
/// Names studio files. Because the platform's bucket is public (anyone holding an address can read the file), the key
/// is the only secret a draft has: it ends in 128 bits of randomness, so it cannot be guessed or enumerated. The same
/// layout serves the signed-URL mode, so switching modes later never moves a file.
/// Layout: {prefix}/{tenantId}/{zone}/{yyyyMM}/{random}.{ext}
/// </summary>
public static class StudioStorageKeys
{
    public static string NewKey(string prefix, Guid tenantId, StudioStorageZone zone, string extension, DateTime nowUtc)
    {
        var ext = SafeExtension(extension);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return $"{prefix.Trim('/')}/{tenantId:N}/{zone.ToString().ToLowerInvariant()}/{nowUtc:yyyyMM}/{random}{ext}";
    }

    /// <summary>True when <paramref name="key"/> sits under this tenant's own studio folder - the check that stops one
    /// tenant addressing another's file by key.</summary>
    public static bool BelongsToTenant(string prefix, Guid tenantId, string key) =>
        key.StartsWith($"{prefix.Trim('/')}/{tenantId:N}/", StringComparison.Ordinal) && !key.Contains("..", StringComparison.Ordinal);

    private static string SafeExtension(string extension)
    {
        var e = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return e.Length is > 0 and <= 5 && e.All(char.IsLetterOrDigit) ? "." + e : string.Empty;
    }
}
