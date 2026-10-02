using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Storage;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class S3MediaStorageTests
{
    private sealed class Ambient : ITenantContext
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private static S3MediaStorageService S3(string publicBase = "") => new(
        Options.Create(new S3MediaStorageSettings { BucketName = "plat-media", Region = "ap-southeast-2", PublicBaseUrl = publicBase }),
        new Ambient());

    private static RoutingMediaStorageService Router(string provider, S3MediaStorageService s3)
    {
        var root = Path.Combine(Path.GetTempPath(), "s3-route-" + Guid.NewGuid().ToString("N"));
        var local = new LocalFileMediaStorageService(
            Fake.Of<IWebHostEnvironment>((_, _) => null),
            Options.Create(new LocalMediaStorageSettings { RootPath = root, PublicBaseUrl = "https://api.example.com" }));
        return new RoutingMediaStorageService(local, s3, Options.Create(new LocalMediaStorageSettings { Provider = provider }));
    }

    [Fact]
    public void An_s3_file_is_reached_at_the_bucket_address()
    {
        var url = S3().GetPublicUrl("s3:media/abc/2026/10/f.png");

        Assert.Equal("https://plat-media.s3.ap-southeast-2.amazonaws.com/media/abc/2026/10/f.png", url);
    }

    [Fact]
    public void A_cdn_address_replaces_the_bucket_address()
    {
        var url = S3("https://cdn.example.com/").GetPublicUrl("s3:media/abc/f.png");

        Assert.Equal("https://cdn.example.com/media/abc/f.png", url);
    }

    [Fact]
    public async Task Uploading_to_s3_without_a_bucket_says_who_must_configure_it()
    {
        var unconfigured = new S3MediaStorageService(Options.Create(new S3MediaStorageSettings()), new Ambient());

        var ex = await Assert.ThrowsAsync<WhatsAppSalesAutomation.Application.Common.Exceptions.StorageUnavailableException>(() => unconfigured.UploadAsync(new MemoryStream(), "a.png", "image/png"));

        Assert.Contains("platform administrator", ex.Message);
    }

    [Fact]
    public async Task Stray_spaces_in_the_settings_are_ignored_and_missing_keys_are_explained()
    {
        var settings = new S3MediaStorageSettings { BucketName = "plat-media ", Region = " ap-southeast-2" };
        Assert.Equal("plat-media", settings.BucketName);
        Assert.Equal("ap-southeast-2", settings.Region);
        Assert.False(settings.HasAccessKeys);

        // Whatever the machine has (a role, a profile, nothing), the failure must never be a bare 500: a keyless laptop gets
        // told to fill the keys in.
        var service = new S3MediaStorageService(Options.Create(settings), new Ambient());
        var ex = await Record.ExceptionAsync(() => service.DeleteAsync("s3:media/x/none.png"));
        if (ex is not null)
            Assert.IsType<WhatsAppSalesAutomation.Application.Common.Exceptions.StorageUnavailableException>(ex);
    }

    [Fact]
    public void Each_file_is_served_by_the_store_its_key_belongs_to_whatever_the_current_provider()
    {
        var router = Router("S3", S3());

        Assert.StartsWith("https://plat-media.s3.", router.GetPublicUrl("s3:media/x/f.png"));
        Assert.Equal("https://api.example.com/media/2026/09/g.png", router.GetPublicUrl("2026/09/g.png"));
        Assert.StartsWith("https://plat-media.s3.", router.GetLocalPath("s3:media/x/f.png"));
        Assert.Equal("/media/2026/09/g.png", router.GetLocalPath("2026/09/g.png"));
    }

    [Fact]
    public async Task New_files_go_to_the_local_disk_unless_the_provider_is_s3()
    {
        var router = Router("Local", S3());

        var stored = await router.UploadAsync(new MemoryStream(new byte[] { 1 }), "a.png", "image/png");

        Assert.DoesNotContain("s3:", stored.StorageKey);
    }
}
