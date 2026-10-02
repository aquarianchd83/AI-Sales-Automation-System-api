using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Storage;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The AWS Settings page: saved to the AppSettings store only, secrets never read back, and what is saved is what the
/// S3 storage settings bind from.</summary>
public class PlatformAwsSettingsTests
{
    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeTester : IAwsConnectionTester
    {
        public AwsConnectionTestSettings? Received { get; private set; }

        public AwsConnectionTestResultDto Result { get; set; } = new(true, "ok", new[] { new AwsConnectionStepDto("Write", true, null) });

        public Task<AwsConnectionTestResultDto> TestAsync(AwsConnectionTestSettings settings, CancellationToken cancellationToken = default)
        {
            Received = settings;
            return Task.FromResult(Result);
        }
    }

    private static PlatformAwsSettingsService Service(MemoryStore store, FakeTester? tester = null) =>
        new(store, new UpdatePlatformAwsSettingsRequestValidator(), tester ?? new FakeTester());

    private static UpdatePlatformAwsSettingsRequest Request(
        string provider = "S3", string bucket = "plat-media", string region = "ap-southeast-2", string prefix = "media",
        string publicBase = "", string? accessKey = "AKIAEXAMPLEKEY1234", string? secret = "s3cr3t-value-9876") =>
        new(provider, bucket, region, prefix, publicBase, accessKey, secret);

    [Fact]
    public async Task Nothing_stored_reads_as_local_and_unconfigured()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.Equal("Local", dto.StorageProvider);
        Assert.False(dto.IsConfigured);
        Assert.False(dto.HasAccessKeyId);
        Assert.False(dto.HasSecretAccessKey);
    }

    [Fact]
    public async Task Saving_stores_every_value_under_the_keys_the_s3_settings_bind_from()
    {
        var store = new MemoryStore();

        await Service(store).UpdateAsync(Request(publicBase: "https://cdn.example.com/"), Guid.NewGuid());

        var config = new ConfigurationBuilder().AddInMemoryCollection(store.Rows).Build();
        var s3 = config.GetSection("MediaStorage:S3").Get<S3MediaStorageSettings>()!;
        Assert.Equal("plat-media", s3.BucketName);
        Assert.Equal("ap-southeast-2", s3.Region);
        Assert.Equal("media", s3.KeyPrefix);
        Assert.Equal("https://cdn.example.com", s3.PublicBaseUrl);
        Assert.True(s3.HasAccessKeys);
        Assert.Equal("S3", config["MediaStorage:Provider"]);
        Assert.True(s3.IsConfigured);
    }

    [Fact]
    public void Every_key_it_writes_is_a_catalog_setting_so_the_store_will_accept_it()
    {
        var keys = new[]
        {
            PlatformAwsSettingsService.ProviderKey, PlatformAwsSettingsService.BucketNameKey, PlatformAwsSettingsService.RegionKey,
            PlatformAwsSettingsService.KeyPrefixKey, PlatformAwsSettingsService.PublicBaseUrlKey,
            PlatformAwsSettingsService.AccessKeyIdKey, PlatformAwsSettingsService.SecretAccessKeyKey,
        };

        foreach (var key in keys)
            Assert.Contains(AppSettingCatalog.All, d => d.Key == key);

        Assert.True(AppSettingCatalog.All.Single(d => d.Key == PlatformAwsSettingsService.AccessKeyIdKey).IsSecret);
        Assert.True(AppSettingCatalog.All.Single(d => d.Key == PlatformAwsSettingsService.SecretAccessKeyKey).IsSecret);
    }

    [Fact]
    public async Task Secrets_are_only_ever_returned_masked()
    {
        var service = Service(new MemoryStore());

        var dto = await service.UpdateAsync(Request(), Guid.NewGuid());

        Assert.True(dto.HasAccessKeyId);
        Assert.True(dto.HasSecretAccessKey);
        Assert.Equal("••••1234", dto.AccessKeyIdHint);
        Assert.Equal("••••9876", dto.SecretAccessKeyHint);
        Assert.DoesNotContain("s3cr3t", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task Leaving_the_keys_out_keeps_the_stored_ones()
    {
        var store = new MemoryStore();
        var service = Service(store);
        await service.UpdateAsync(Request(), Guid.NewGuid());

        var dto = await service.UpdateAsync(Request(bucket: "other-bucket", accessKey: null, secret: null), Guid.NewGuid());

        Assert.Equal("other-bucket", dto.BucketName);
        Assert.Equal("AKIAEXAMPLEKEY1234", store.Rows[PlatformAwsSettingsService.AccessKeyIdKey]);
        Assert.Equal("s3cr3t-value-9876", store.Rows[PlatformAwsSettingsService.SecretAccessKeyKey]);
    }

    [Fact]
    public async Task Empty_strings_clear_the_keys_so_the_servers_own_aws_role_is_used()
    {
        var store = new MemoryStore();
        var service = Service(store);
        await service.UpdateAsync(Request(), Guid.NewGuid());

        var dto = await service.UpdateAsync(Request(accessKey: "", secret: ""), Guid.NewGuid());

        Assert.False(dto.HasAccessKeyId);
        Assert.False(dto.HasSecretAccessKey);
    }

    [Fact]
    public async Task Half_a_key_pair_is_refused()
    {
        var service = Service(new MemoryStore());

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(Request(accessKey: "AKIAEXAMPLEKEY1234", secret: ""), Guid.NewGuid()));

        // ...including when the other half is only already stored.
        var store = new MemoryStore();
        var seeded = Service(store);
        await seeded.UpdateAsync(Request(), Guid.NewGuid());
        await Assert.ThrowsAsync<ValidationException>(() => seeded.UpdateAsync(Request(accessKey: "", secret: null), Guid.NewGuid()));
    }

    [Theory]
    [InlineData("Azure", "plat-media", "ap-southeast-2", "")]
    [InlineData("S3", "Not_A_Bucket", "ap-southeast-2", "")]
    [InlineData("S3", "plat-media", "mars", "")]
    [InlineData("S3", "plat-media", "ap-southeast-2", "ftp://cdn.example.com")]
    [InlineData("S3", "", "", "")]
    public async Task Invalid_settings_are_refused_and_nothing_is_stored(string provider, string bucket, string region, string publicBase)
    {
        var store = new MemoryStore();

        await Assert.ThrowsAsync<ValidationException>(() => Service(store).UpdateAsync(Request(provider, bucket, region, publicBase: publicBase), Guid.NewGuid()));

        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Local_storage_can_be_saved_without_a_bucket_and_a_blank_prefix_falls_back_to_media()
    {
        var store = new MemoryStore();

        var dto = await Service(store).UpdateAsync(Request("Local", "", "", prefix: " / ", accessKey: null, secret: null), Guid.NewGuid());

        Assert.Equal("Local", dto.StorageProvider);
        Assert.False(dto.IsConfigured);
        Assert.Equal("media", dto.KeyPrefix);
    }

    [Fact]
    public async Task The_saved_provider_is_what_the_media_storage_settings_bind_from()
    {
        // RoutingMediaStorageService reads these through IOptionsSnapshot, so a saved provider applies on the next request.
        var store = new MemoryStore();
        await Service(store).UpdateAsync(Request("S3"), Guid.NewGuid());

        var settings = new ConfigurationBuilder().AddInMemoryCollection(store.Rows).Build().Get<TestRoot>()!;

        Assert.Equal("S3", settings.MediaStorage.Provider);
    }

    [Fact]
    public async Task Testing_without_typing_the_keys_uses_the_stored_ones_and_saves_nothing()
    {
        var store = new MemoryStore();
        var tester = new FakeTester();
        var service = Service(store, tester);
        await service.UpdateAsync(Request(), Guid.NewGuid());
        var before = new Dictionary<string, string?>(store.Rows);

        var result = await service.TestConnectionAsync(Request(bucket: "other-bucket", prefix: "/uploads/", accessKey: null, secret: null));

        Assert.True(result.Success);
        Assert.Equal("other-bucket", tester.Received!.BucketName);
        Assert.Equal("uploads", tester.Received.KeyPrefix);
        Assert.Equal("AKIAEXAMPLEKEY1234", tester.Received.AccessKeyId);
        Assert.Equal("s3cr3t-value-9876", tester.Received.SecretAccessKey);
        Assert.Equal(before.Count, store.Rows.Count);
        Assert.Equal("plat-media", store.Rows[PlatformAwsSettingsService.BucketNameKey]);
    }

    [Fact]
    public async Task Testing_with_typed_keys_uses_those_not_the_stored_ones()
    {
        var store = new MemoryStore();
        var tester = new FakeTester();
        var service = Service(store, tester);
        await service.UpdateAsync(Request(), Guid.NewGuid());

        await service.TestConnectionAsync(Request(accessKey: "AKIANEW", secret: "newsecret"));

        Assert.Equal("AKIANEW", tester.Received!.AccessKeyId);
        Assert.Equal("newsecret", tester.Received.SecretAccessKey);
    }

    [Fact]
    public async Task Cleared_keys_test_with_the_servers_own_aws_role()
    {
        var tester = new FakeTester();

        await Service(new MemoryStore(), tester).TestConnectionAsync(Request(accessKey: "", secret: ""));

        Assert.Equal(string.Empty, tester.Received!.AccessKeyId);
        Assert.Equal(string.Empty, tester.Received.SecretAccessKey);
    }

    [Fact]
    public async Task A_failed_test_is_reported_not_thrown()
    {
        var tester = new FakeTester { Result = new(false, "Bucket 'x' does not exist in ap-southeast-2.", new[] { new AwsConnectionStepDto("Write", false, "no bucket") }) };

        var result = await Service(new MemoryStore(), tester).TestConnectionAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Message);
    }

    [Theory]
    [InlineData("", "ap-southeast-2")]
    [InlineData("plat-media", "")]
    public async Task Testing_needs_a_bucket_and_region_and_never_reaches_aws_without_them(string bucket, string region)
    {
        var tester = new FakeTester();

        await Assert.ThrowsAsync<ValidationException>(() => Service(new MemoryStore(), tester).TestConnectionAsync(Request(bucket: bucket, region: region)));

        Assert.Null(tester.Received);
    }

    [Fact]
    public async Task Testing_refuses_half_a_key_pair()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service(new MemoryStore()).TestConnectionAsync(Request(accessKey: "AKIANEW", secret: "")));
    }

    private sealed class TestRoot
    {
        public LocalMediaStorageSettings MediaStorage { get; set; } = new();
    }
}
