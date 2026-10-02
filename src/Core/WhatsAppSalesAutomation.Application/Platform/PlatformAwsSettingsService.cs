using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The platform's AWS (S3 media bucket) configuration as the Platform Admin Console shows it. The access key and
/// secret never leave the server: only whether one is stored, plus the last four characters as a hint.</summary>
public record PlatformAwsSettingsDto(
    string StorageProvider,
    string BucketName,
    string Region,
    string KeyPrefix,
    string PublicBaseUrl,
    bool HasAccessKeyId,
    string? AccessKeyIdHint,
    bool HasSecretAccessKey,
    string? SecretAccessKeyHint,
    bool IsConfigured);

/// <param name="StorageProvider">"Local" or "S3" - where NEW uploads go.</param>
/// <param name="AccessKeyId">null keeps the stored key, "" clears it, anything else replaces it.</param>
/// <param name="SecretAccessKey">Same convention as <paramref name="AccessKeyId"/>.</param>
public record UpdatePlatformAwsSettingsRequest(
    string StorageProvider,
    string BucketName,
    string Region,
    string KeyPrefix,
    string PublicBaseUrl,
    string? AccessKeyId,
    string? SecretAccessKey);

public interface IPlatformAwsSettingsService
{
    Task<PlatformAwsSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores the settings in the AppSettings table. Takes effect on the next request - no restart.</summary>
    Task<PlatformAwsSettingsDto> UpdateAsync(UpdatePlatformAwsSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-admin-owned AWS settings. They live ONLY in the AppSettings table (the access key and secret encrypted at rest) -
/// appsettings.json carries no AWS values. The keys are the same ones <c>S3MediaStorageSettings</c> binds from, so the media
/// storage code reads what is saved here on its next request.
/// </summary>
public class PlatformAwsSettingsService : IPlatformAwsSettingsService
{
    public const string ProviderKey = "MediaStorage:Provider";
    public const string BucketNameKey = "MediaStorage:S3:BucketName";
    public const string RegionKey = "MediaStorage:S3:Region";
    public const string AccessKeyIdKey = "MediaStorage:S3:AccessKeyId";
    public const string SecretAccessKeyKey = "MediaStorage:S3:SecretAccessKey";
    public const string KeyPrefixKey = "MediaStorage:S3:KeyPrefix";
    public const string PublicBaseUrlKey = "MediaStorage:S3:PublicBaseUrl";

    /// <summary>What the bucket folder falls back to when none is given.</summary>
    public const string DefaultKeyPrefix = "media";

    public const string LocalProvider = "Local";
    public const string S3Provider = "S3";

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdatePlatformAwsSettingsRequest> _validator;

    public PlatformAwsSettingsService(IAppSettingsStore store, IValidator<UpdatePlatformAwsSettingsRequest> validator)
    {
        _store = store;
        _validator = validator;
    }

    public async Task<PlatformAwsSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await _store.GetAllAsync(cancellationToken));

    public async Task<PlatformAwsSettingsDto> UpdateAsync(UpdatePlatformAwsSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var stored = await _store.GetAllAsync(cancellationToken);

        var accessKey = Resolve(request.AccessKeyId, Get(stored, AccessKeyIdKey));
        var secret = Resolve(request.SecretAccessKey, Get(stored, SecretAccessKeyKey));

        // One half of a key pair is never usable, and would otherwise surface later as an AWS "signature does not match".
        if ((accessKey.Length == 0) != (secret.Length == 0))
            throw new ValidationException("The AWS access key ID and secret access key go together: provide both, or clear both to use the server's own AWS role.");

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ProviderKey] = request.StorageProvider.Trim(),
            [BucketNameKey] = request.BucketName.Trim(),
            [RegionKey] = request.Region.Trim(),
            [KeyPrefixKey] = string.IsNullOrWhiteSpace(request.KeyPrefix.Trim('/', ' ')) ? DefaultKeyPrefix : request.KeyPrefix.Trim().Trim('/'),
            [PublicBaseUrlKey] = request.PublicBaseUrl.Trim().TrimEnd('/'),
        };

        // Only touch the secrets when the caller said something about them.
        if (request.AccessKeyId is not null)
            values[AccessKeyIdKey] = accessKey;
        if (request.SecretAccessKey is not null)
            values[SecretAccessKeyKey] = secret;

        await _store.UpsertAsync(values, updatedByUserId, cancellationToken);

        return await GetAsync(cancellationToken);
    }

    private static string Resolve(string? requested, string current) => requested is null ? current : requested.Trim();

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static PlatformAwsSettingsDto ToDto(IReadOnlyDictionary<string, string?> stored)
    {
        var provider = Get(stored, ProviderKey);
        var bucket = Get(stored, BucketNameKey);
        var region = Get(stored, RegionKey);
        var accessKey = Get(stored, AccessKeyIdKey);
        var secret = Get(stored, SecretAccessKeyKey);

        return new PlatformAwsSettingsDto(
            string.Equals(provider, S3Provider, StringComparison.OrdinalIgnoreCase) ? S3Provider : LocalProvider,
            bucket,
            region,
            Get(stored, KeyPrefixKey),
            Get(stored, PublicBaseUrlKey),
            accessKey.Length > 0,
            Mask(accessKey),
            secret.Length > 0,
            Mask(secret),
            bucket.Length > 0 && region.Length > 0);
    }

    private static string? Mask(string value) =>
        value.Length == 0 ? null : value.Length <= 4 ? "••••" : $"••••{value[^4..]}";
}

public class UpdatePlatformAwsSettingsRequestValidator : AbstractValidator<UpdatePlatformAwsSettingsRequest>
{
    public UpdatePlatformAwsSettingsRequestValidator()
    {
        RuleFor(x => x.StorageProvider)
            .NotNull()
            .Must(p => p is not null && (p.Trim().Equals(PlatformAwsSettingsService.LocalProvider, StringComparison.OrdinalIgnoreCase)
                                         || p.Trim().Equals(PlatformAwsSettingsService.S3Provider, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Storage provider must be 'Local' or 'S3'.");

        // S3 bucket naming: 3-63 chars of lowercase letters, digits, dots and hyphens, starting and ending alphanumeric.
        RuleFor(x => x.BucketName).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || BucketName.IsMatch(v.Trim())))
            .WithMessage("Bucket name must be 3-63 characters: lowercase letters, numbers, dots and hyphens.");

        RuleFor(x => x.Region).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || RegionCode.IsMatch(v.Trim())))
            .WithMessage("Region must be an AWS region code such as ap-southeast-2.");

        RuleFor(x => x.KeyPrefix).NotNull().MaximumLength(200);

        RuleFor(x => x.PublicBaseUrl).NotNull().Must(v => v is not null && (v.Trim().Length == 0 || IsHttpUrl(v.Trim())))
            .WithMessage("Public base URL must be an absolute http(s) address, e.g. https://cdn.example.com.");

        RuleFor(x => x.AccessKeyId).MaximumLength(128);
        RuleFor(x => x.SecretAccessKey).MaximumLength(256);

        // Saving "use S3" without a bucket would make every upload fail with a 503 - refuse it here instead.
        RuleFor(x => x).Must(x => !string.Equals(x.StorageProvider?.Trim(), PlatformAwsSettingsService.S3Provider, StringComparison.OrdinalIgnoreCase)
                                  || (!string.IsNullOrWhiteSpace(x.BucketName) && !string.IsNullOrWhiteSpace(x.Region)))
            .WithName("Bucket")
            .WithMessage("A bucket name and region are required before new uploads can go to S3.");
    }

    private static readonly System.Text.RegularExpressions.Regex BucketName = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RegionCode = new("^[a-z]{2}(-[a-z]+)+-[0-9]{1}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
