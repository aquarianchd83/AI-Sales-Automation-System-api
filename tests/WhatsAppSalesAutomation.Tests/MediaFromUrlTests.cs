using System.Net;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Storage;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class MediaFromUrlTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly StubFetcher _fetcher = new();

    public MediaFromUrlTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant.Id), new Nobody()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MediaService Service()
    {
        var config = Fake.Of<ITenantConfigOverrideProvider>((m, _) => m.Name switch
        {
            nameof(ITenantConfigOverrideProvider.GetMediaOptionsAsync) => Task.FromResult(new MediaOptions
            {
                MaxSizeBytes = 1024 * 1024,
                AllowedContentTypes = new[] { "image/jpeg", "image/png", "video/mp4" },
            }),
            _ => throw new NotImplementedException(m.Name),
        });
        return new MediaService(_db, new Storage(), config, _fetcher);
    }

    [Fact]
    public async Task A_link_keeps_the_tenants_own_address_as_the_public_url()
    {
        _fetcher.Result = new MediaUrlFetchResult(new byte[] { 1, 2, 3 }, "image/png", "logo.png");

        var dto = await Service().AddFromUrlAsync("https://site.example/assets/eye/logo.png", null);

        Assert.Equal("https://site.example/assets/eye/logo.png", dto.Url);
        Assert.Equal("logo.png", dto.FileName);
        Assert.Equal("image/png", dto.ContentType);
        Assert.True(dto.IsPublicUrl);
    }

    [Fact]
    public async Task Adding_the_same_link_twice_returns_the_first_entry()
    {
        _fetcher.Result = new MediaUrlFetchResult(new byte[] { 1 }, "image/png", "logo.png");

        var first = await Service().AddFromUrlAsync("https://site.example/logo.png", null);
        var second = await Service().AddFromUrlAsync("https://site.example/logo.png", null);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _db.MediaAssets.CountAsync());
    }

    [Fact]
    public async Task A_generic_content_type_falls_back_to_the_file_extension()
    {
        _fetcher.Result = new MediaUrlFetchResult(new byte[] { 1 }, "application/octet-stream", "clip.mp4");

        var dto = await Service().AddFromUrlAsync("https://site.example/clip.mp4", null);

        Assert.Equal("video/mp4", dto.ContentType);
    }

    [Fact]
    public async Task A_link_to_something_that_is_not_an_allowed_file_is_refused()
    {
        _fetcher.Result = new MediaUrlFetchResult(new byte[] { 1 }, "text/html", "page");

        await Assert.ThrowsAsync<ValidationException>(() => Service().AddFromUrlAsync("https://site.example/page", null));
        Assert.Empty(_db.MediaAssets);
    }

    [Fact]
    public async Task A_link_that_cannot_be_downloaded_is_reported_as_a_validation_error()
    {
        _fetcher.Failure = "The link could not be reached.";

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Service().AddFromUrlAsync("https://site.example/x.png", null));

        Assert.Contains("could not be reached", ex.Message);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.1")]
    [InlineData("192.168.0.9")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public void Private_addresses_are_internal(string ip) => Assert.True(HttpMediaUrlFetcher.IsInternal(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    public void Public_addresses_are_not_internal(string ip) => Assert.False(HttpMediaUrlFetcher.IsInternal(IPAddress.Parse(ip)));

    private sealed class StubFetcher : IMediaUrlFetcher
    {
        public MediaUrlFetchResult? Result { get; set; }
        public string? Failure { get; set; }

        public Task<MediaUrlFetchResult> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken = default) =>
            Failure is not null ? throw new MediaUrlFetchException(Failure) : Task.FromResult(Result!);
    }

    private sealed class Storage : IMediaStorageService
    {
        public Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaStorageResult($"2026/10/{Guid.NewGuid():N}", "/media/local"));

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public string GetPublicUrl(string storageKey) => $"https://ours.example/media/{storageKey}";

        public bool IsPublicUrl(string url) => url.StartsWith("https://");
    }

    private sealed class Ambient : ITenantContext
    {
        private readonly Guid _id;
        public Ambient(Guid id) => _id = id;
        public Guid? TenantId => _id;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class Nobody : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
