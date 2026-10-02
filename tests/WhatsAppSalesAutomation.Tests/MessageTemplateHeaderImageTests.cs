using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.MessageTemplates;
using WhatsAppSalesAutomation.Domain.Entities.Media;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A template can carry an optional image: it is validated, sent to Meta when the template is created, and
/// then fixed there - the image can be swapped but not added or removed.</summary>
public sealed class MessageTemplateHeaderImageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly List<WhatsAppTemplateSubmission> _created = new();
    private readonly List<WhatsAppTemplateSubmission> _updated = new();
    private readonly Dictionary<string, byte[]> _files = new();

    public MessageTemplateHeaderImageTests()
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

    private MessageTemplateService Service()
    {
        var whatsApp = Fake.Of<IWhatsAppService>((m, args) => m.Name switch
        {
            nameof(IWhatsAppService.CreateMessageTemplateAsync) => Record(_created, args![0], "meta-tpl-1"),
            nameof(IWhatsAppService.UpdateMessageTemplateAsync) => Record(_updated, args![1], "meta-tpl-1"),
            nameof(IWhatsAppService.GetMessageTemplatesAsync) => Task.FromResult<IReadOnlyList<WhatsAppRemoteTemplate>>(Array.Empty<WhatsAppRemoteTemplate>()),
            _ => throw new NotImplementedException(m.Name),
        });

        return new MessageTemplateService(
            _db, whatsApp, new CreateMessageTemplateRequestValidator(), new UpdateMessageTemplateRequestValidator(),
            new ReviewMessageTemplateRequestValidator(), new InMemoryStorage(_files));
    }

    private static Task<WhatsAppTemplateSubmitResult> Record(List<WhatsAppTemplateSubmission> into, object? submission, string metaId)
    {
        into.Add((WhatsAppTemplateSubmission)submission!);
        return Task.FromResult(new WhatsAppTemplateSubmitResult(true, metaId, "PENDING", null));
    }

    private MediaAsset Image(string contentType = "image/png", long sizeBytes = 1000, string name = "hero.png")
    {
        var asset = new MediaAsset
        {
            FileName = name, ContentType = contentType, SizeBytes = sizeBytes, StorageProvider = "Local",
            StorageKey = $"2026/09/{Guid.NewGuid():N}.png", Url = "https://cdn.example.test/hero.png", Checksum = "x",
        };
        _files[asset.StorageKey] = new byte[] { 9, 8, 7 };
        _db.MediaAssets.Add(asset);
        _db.SaveChanges();
        return asset;
    }

    private static CreateMessageTemplateRequest NewTemplate(Guid? headerId) =>
        new("Welcome", "en", "Marketing", "welcome_offer", "Hi {{FirstName}}", headerId);

    [Fact]
    public async Task An_image_added_by_link_is_sent_from_the_tenants_own_address()
    {
        var png = Image();
        png.StorageProvider = "External";
        png.Url = "https://bucket.s3.amazonaws.com/logo.png";
        _db.SaveChanges();
        var service = Service();
        var created = await service.CreateAsync(NewTemplate(png.Id));
        Assert.Equal("https://bucket.s3.amazonaws.com/logo.png", created.HeaderImageUrl);

        var template = await _db.MessageTemplates.FirstAsync(t => t.Id == created.Id);
        template.HeaderOnMeta = true;
        Assert.Equal("https://bucket.s3.amazonaws.com/logo.png", await TemplateHeaderImage.ResolveUrlAsync(_db, template, default, new InMemoryStorage(_files)));
    }

    [Fact]
    public async Task The_list_names_the_image_each_template_carries()
    {
        var png = Image();
        await Service().CreateAsync(NewTemplate(png.Id));
        await Service().CreateAsync(NewTemplate(null) with { WhatsAppTemplateName = "plain_one" });

        var page = await Service().GetPagedAsync(new WhatsAppSalesAutomation.Application.Common.Models.PagedRequest());

        var withImage = Assert.Single(page.Items, t => t.HeaderMediaAssetId != null);
        Assert.Equal("hero.png", withImage.HeaderImageFileName);
        var plain = Assert.Single(page.Items, t => t.HeaderMediaAssetId == null);
        Assert.Null(plain.HeaderImageFileName);
        Assert.Null(plain.HeaderImageUrl);
    }

    [Fact]
    public async Task A_template_can_be_created_with_a_png_or_jpeg_image()
    {
        var png = Image("image/png");
        var created = await Service().CreateAsync(NewTemplate(png.Id));

        Assert.Equal(png.Id, created.HeaderMediaAssetId);
        Assert.False(created.HeaderOnMeta); // not on Meta until the first sync
        Assert.Equal("hero.png", created.HeaderImageFileName);
        Assert.Equal($"https://cdn.example.test/{png.StorageKey}", created.HeaderImageUrl);

        var jpeg = Image("image/jpeg", name: "hero.jpg");
        var second = await Service().CreateAsync(NewTemplate(jpeg.Id) with { WhatsAppTemplateName = "welcome_two" });
        Assert.Equal(jpeg.Id, second.HeaderMediaAssetId);
    }

    [Fact]
    public async Task An_image_Meta_would_refuse_is_rejected_up_front()
    {
        var gif = Image("image/gif", name: "hero.gif");
        var huge = Image("image/png", sizeBytes: 6 * 1024 * 1024, name: "huge.png");

        var wrongType = await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(NewTemplate(gif.Id)));
        var tooBig = await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(NewTemplate(huge.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().CreateAsync(NewTemplate(Guid.NewGuid())));

        Assert.Contains("JPEG or PNG", wrongType.Message);
        Assert.Contains("5 MB", tooBig.Message);
        Assert.Empty(await _db.MessageTemplates.ToListAsync());
    }

    [Fact]
    public async Task Syncing_a_new_template_with_an_image_hands_the_file_to_meta_and_remembers_it_has_a_header()
    {
        var image = Image();
        var created = await Service().CreateAsync(NewTemplate(image.Id));

        var result = await Service().SyncOneAsync(created.Id);

        var submission = Assert.Single(_created);
        Assert.NotNull(submission.HeaderImage);
        Assert.Equal("hero.png", submission.HeaderImage!.FileName);
        Assert.Equal("image/png", submission.HeaderImage.ContentType);
        Assert.Equal(new byte[] { 9, 8, 7 }, submission.HeaderImage.Content);
        Assert.True(result.Template.HeaderOnMeta);
        Assert.Null(result.PushError);
    }

    [Fact]
    public async Task A_template_with_no_image_is_pushed_body_only()
    {
        var created = await Service().CreateAsync(NewTemplate(null));

        var result = await Service().SyncOneAsync(created.Id);

        Assert.Null(Assert.Single(_created).HeaderImage);
        Assert.False(result.Template.HeaderOnMeta);
    }

    [Fact]
    public async Task A_missing_image_file_is_a_push_failure_not_a_crash()
    {
        var image = Image();
        var created = await Service().CreateAsync(NewTemplate(image.Id));
        _files.Clear();

        var result = await Service().SyncOneAsync(created.Id);

        Assert.NotNull(result.PushError);
        Assert.Empty(_created);
        Assert.Null((await _db.MessageTemplates.SingleAsync()).MetaTemplateId);
    }

    [Fact]
    public async Task On_meta_the_image_can_be_swapped_but_not_added_or_removed()
    {
        var first = Image();
        var second = Image(name: "second.png");
        var withImage = await Service().CreateAsync(NewTemplate(first.Id));
        await Service().SyncOneAsync(withImage.Id);

        var swapped = await Service().UpdateAsync(withImage.Id, new UpdateMessageTemplateRequest("Hi {{FirstName}}", true, HeaderMediaAssetId: second.Id));
        Assert.Equal(second.Id, swapped.HeaderMediaAssetId);

        await Assert.ThrowsAsync<ConflictException>(() =>
            Service().UpdateAsync(withImage.Id, new UpdateMessageTemplateRequest("Hi {{FirstName}}", true, RemoveHeaderImage: true)));

        var bodyOnly = await Service().CreateAsync(NewTemplate(null) with { WhatsAppTemplateName = "body_only" });
        await Service().SyncOneAsync(bodyOnly.Id);
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            Service().UpdateAsync(bodyOnly.Id, new UpdateMessageTemplateRequest("Hi {{FirstName}}", true, HeaderMediaAssetId: first.Id)));
        Assert.Contains("create a new template", error.Message);
    }

    [Fact]
    public async Task Before_it_is_on_meta_the_image_can_be_added_and_removed_freely()
    {
        var image = Image();
        var created = await Service().CreateAsync(NewTemplate(null));

        var added = await Service().UpdateAsync(created.Id, new UpdateMessageTemplateRequest("Hi {{FirstName}}", true, HeaderMediaAssetId: image.Id));
        Assert.Equal(image.Id, added.HeaderMediaAssetId);

        var removed = await Service().UpdateAsync(created.Id, new UpdateMessageTemplateRequest("Hi {{FirstName}}", true, RemoveHeaderImage: true));
        Assert.Null(removed.HeaderMediaAssetId);
    }

    [Fact]
    public async Task A_message_only_carries_the_image_once_the_template_was_created_on_meta_with_it()
    {
        var image = Image();
        var template = new MessageTemplate { Name = "t", WhatsAppTemplateName = "t", BodyText = "Hi", HeaderMediaAssetId = image.Id };
        _db.MessageTemplates.Add(template);
        await _db.SaveChangesAsync();

        Assert.Null(await TemplateHeaderImage.ResolveUrlAsync(_db, template, default));

        template.HeaderOnMeta = true;
        Assert.Equal("https://cdn.example.test/hero.png", await TemplateHeaderImage.ResolveUrlAsync(_db, template, default));

        template.HeaderMediaAssetId = null;
        Assert.Null(await TemplateHeaderImage.ResolveUrlAsync(_db, template, default));
    }

    private sealed class InMemoryStorage : IMediaStorageService
    {
        private readonly Dictionary<string, byte[]> _files;
        public InMemoryStorage(Dictionary<string, byte[]> files) => _files = files;

        public Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string GetPublicUrl(string storageKey) => $"https://cdn.example.test/{storageKey}";

        public bool IsPublicUrl(string url) => url.StartsWith("https://cdn.example.test");

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            _files.TryGetValue(storageKey, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException(storageKey);
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
