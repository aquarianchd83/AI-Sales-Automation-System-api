using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Assets;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The studio's pictures: only JPEG/PNG/WebP, judged by their bytes; uploaded only with the tenant's confirmation; kept once per tenant,
/// privately, in the tenant's own folder; stock photos copied with the licence and credit the library itself gives; a picture set on a scene
/// only while the script is a draft; nothing crosses tenants.
/// </summary>
public sealed class StudioAssetServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class Admin : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Email => "owner@clinic.example";
        public IReadOnlyList<string> Roles => new[] { "Admin" };
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    private sealed class MemoryStore : ITenantDocumentStore
    {
        public bool IsConfigured { get; set; } = true;
        public Dictionary<string, byte[]> Files { get; } = new();
        public int Saves { get; private set; }

        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var key = "s3:" + FolderOf(tenantId) + relativePath;
            Files[key] = copy.ToArray();
            Saves++;
            return Task.FromResult(key);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Files[storageKey]));

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            Files.Remove(storageKey);
            return Task.CompletedTask;
        }

        public string FolderOf(Guid tenantId) => $"private/{tenantId:N}/product-knowledge/";
    }

    private sealed class FakeStock : IStockAssetProvider
    {
        public string Name => "Pexels";
        public bool IsConfigured { get; set; } = true;
        public string? Message { get; set; }
        public List<StockPhoto> Photos { get; } = new();
        public List<string> Searches { get; } = new();
        public int Downloads { get; private set; }
        public byte[]? Image { get; set; }
        public bool FailDownload { get; set; }

        public Task<StockSearchResult> SearchAsync(string query, int count, CancellationToken cancellationToken = default)
        {
            Searches.Add(query);
            return Task.FromResult(new StockSearchResult(Photos, Message));
        }

        public Task<StockPhoto?> GetAsync(string providerAssetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Photos.FirstOrDefault(p => p.ProviderAssetId == providerAssetId));

        public Task<StockImage> DownloadAsync(StockPhoto photo, int maxBytes, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (FailDownload)
                throw new HttpRequestException("down");
            return Task.FromResult(new StockImage(Image ?? Png(1880, 1253, 7)));
        }
    }

    private static StockPhoto Photo(string id = "1001") => new(
        id, "Pexels", $"https://images.pexels.com/photos/{id}/small.jpg", $"https://images.pexels.com/photos/{id}/large.jpg", $"https://www.pexels.com/photo/{id}/", "Jane Doe", "https://www.pexels.com/@jane",
        1880, 1253, "A bright eye clinic reception", "Pexels License", "https://www.pexels.com/license/");

    /// <summary>A minimal PNG header (signature + IHDR) with a filler byte so two pictures can differ.</summary>
    internal static byte[] Png(int width, int height, byte filler = 0)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        for (var i = 24; i < bytes.Length; i++)
            bytes[i] = filler;
        return bytes;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly MemoryStore _store = new();
    private readonly FakeStock _stock = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public StudioAssetServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _tenant.TenantId = _a;
        _db = new SqliteApplicationDbContext(options, _tenant, new NoUser());
        _db.Database.EnsureCreated();
        var tick = 0;
        _db.BeforeSave = db =>
        {
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added))
                entry.Entity.CreatedAt = Now.AddSeconds(++tick);
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private StudioAssetService Service() => new(_db, _store, _stock, new Admin(), new Clock(), NullLogger<StudioAssetService>.Instance);

    private static UploadAssetRequest Upload(byte[] bytes, string fileName = "front_desk-photo.png", StudioAssetKind kind = StudioAssetKind.Upload, bool rights = true, bool personal = false,
        string? name = null, string? tags = null, string? description = null) =>
        new(new MemoryStream(bytes), bytes.Length, fileName, kind, name, description, tags, rights, personal);

    private async Task<(ContentScene Scene, ContentVersion Version)> Scene(Guid? tenant = null, string? hint = "reception desk of the clinic", ContentVersionStatus status = ContentVersionStatus.Draft)
    {
        var tenantId = tenant ?? _a;
        var project = new ContentProject { TenantId = tenantId, Title = "P", Goal = "G" };
        var version = new ContentVersion { TenantId = tenantId, ProjectId = project.Id, Number = 1, Status = status, PromptVersion = "v", Provider = "OpenAI", ContentHash = new string('a', 64) };
        var scene = new ContentScene { TenantId = tenantId, VersionId = version.Id, Order = 1, Narration = "Welcome to the clinic.", VisualHint = hint, DurationSeconds = 8 };
        _db.ContentProjects.Add(project);
        _db.ContentVersions.Add(version);
        _db.ContentScenes.Add(scene);
        await _db.SaveChangesAsync();
        return (scene, version);
    }

    // ----- uploading -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_picture_is_judged_by_its_bytes_kept_privately_in_the_tenants_folder_and_labelled_as_the_tenants_own()
    {
        var asset = await Service().UploadAsync(_a, Upload(Png(800, 600), tags: " Reception, FRONT desk, reception ", description: "  The front desk  "));

        Assert.Equal(("Upload", "Your upload", "front desk photo", "image/png", 800, 600), (asset.Kind, asset.KindLabel, asset.Name, asset.ContentType, asset.Width, asset.Height));
        Assert.Equal(new[] { "reception", "front desk" }, asset.Tags);
        Assert.Equal(("Provided by you", true, false, null), (asset.LicenseName, asset.RightsConfirmed, asset.PersonalDataChecked, asset.Attribution));
        Assert.Equal("The front desk", asset.Description);
        var stored = await _db.StudioAssets.SingleAsync();
        Assert.Contains($"private/{_a:N}/product-knowledge/studio/assets/202610/", stored.StorageKey);
        Assert.EndsWith(".png", stored.StorageKey);
        Assert.Equal(64, stored.Checksum.Length);
        Assert.Equal(_store.Files[stored.StorageKey], Png(800, 600));
    }

    [Fact]
    public async Task A_screenshot_needs_both_confirmations_and_is_labelled_as_one()
    {
        var bytes = Png(1280, 720, 3);
        await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, Upload(bytes, kind: StudioAssetKind.Screenshot, personal: false)));

        var asset = await Service().UploadAsync(_a, Upload(bytes, "dashboard.png", StudioAssetKind.Screenshot, personal: true));

        Assert.Equal(("Screenshot", "Your screenshot", true), (asset.Kind, asset.KindLabel, asset.PersonalDataChecked));
    }

    [Fact]
    public async Task An_upload_is_refused_without_the_tenants_confirmation_as_a_stock_photo_as_something_that_is_not_a_picture_or_when_too_big()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, Upload(Png(10, 10), rights: false)));
        Assert.Contains("may use this picture", ex.Errors.Single().ErrorMessage);
        await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, Upload(Png(10, 10), kind: StudioAssetKind.Stock)));
        await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, Upload(Array.Empty<byte>())));

        // A program renamed to .jpg, an SVG (which can carry script), a PDF and text are all refused: only the first bytes count.
        foreach (var bytes in new[]
                 {
                     System.Text.Encoding.ASCII.GetBytes("MZ" + new string('x', 100)),
                     System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
                     System.Text.Encoding.ASCII.GetBytes("%PDF-1.7" + new string(' ', 100)),
                     System.Text.Encoding.UTF8.GetBytes("just words, not a picture, long enough to be read")
                 })
            Assert.Contains("Only JPEG, PNG and WebP", (await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, Upload(bytes, "holiday.jpg")))).Errors.Single().ErrorMessage);

        var tooBig = new byte[StudioAssetService.MaxImageBytes + 1];
        Png(5, 5).CopyTo(tooBig, 0);
        await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, Upload(tooBig)));
        // The declared length cannot be trusted: a stream longer than it claims is cut off too.
        await Assert.ThrowsAsync<ValidationException>(() => Service().UploadAsync(_a, new UploadAssetRequest(new MemoryStream(tooBig), 100, "x.png", StudioAssetKind.Upload, null, null, null, true, false)));

        Assert.Empty(await _db.StudioAssets.ToListAsync());
        Assert.Empty(_store.Files);
    }

    [Fact]
    public async Task Without_file_storage_nothing_is_accepted_and_the_message_says_why()
    {
        _store.IsConfigured = false;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().UploadAsync(_a, Upload(Png(10, 10))));

        Assert.Contains("AWS S3", ex.Message);
    }

    [Fact]
    public async Task The_same_picture_is_kept_once_per_tenant_but_each_tenant_has_its_own_copy()
    {
        var first = await Service().UploadAsync(_a, Upload(Png(100, 100, 5), "one.png"));
        var again = await Service().UploadAsync(_a, Upload(Png(100, 100, 5), "renamed.png"));
        var theirs = await Service().UploadAsync(_b, Upload(Png(100, 100, 5), "one.png"));

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, theirs.Id);
        Assert.Equal(2, _store.Saves);
        Assert.Equal(2, await _db.StudioAssets.IgnoreQueryFilters().CountAsync());
    }

    // ----- reading and removing --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_picture_is_read_back_only_by_its_own_tenant_and_only_from_the_tenants_own_folder()
    {
        var asset = await Service().UploadAsync(_a, Upload(Png(30, 20, 9)));

        var file = await Service().OpenAsync(_a, asset.Id);
        using var copy = new MemoryStream();
        await file.Content.CopyToAsync(copy);
        Assert.Equal(Png(30, 20, 9), copy.ToArray());
        Assert.Equal("image/png", file.ContentType);
        Assert.EndsWith(".png", file.FileName);

        await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenAsync(_b, asset.Id));

        // Even a row that points at another tenant's folder is not read.
        var row = await _db.StudioAssets.SingleAsync();
        row.StorageKey = $"s3:private/{_b:N}/product-knowledge/studio/assets/202610/other.png";
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenAsync(_a, asset.Id));
    }

    [Fact]
    public async Task A_picture_in_use_cannot_be_deleted_and_an_unused_one_is_removed_with_its_file()
    {
        var asset = await Service().UploadAsync(_a, Upload(Png(30, 20, 1)));
        var (scene, _) = await Scene();
        await Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(asset.Id, null));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().DeleteAsync(_a, asset.Id));
        Assert.Contains("used in 1 scene", ex.Message);
        Assert.Equal(1, (await Service().ListAsync(_a, null)).Single().UsedInScenes);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteAsync(_b, asset.Id));

        await Service().ClearAsync(_a, scene.Id);
        await Service().DeleteAsync(_a, asset.Id);

        Assert.Empty(await _db.StudioAssets.ToListAsync());
        Assert.Empty(_store.Files);
    }

    [Fact]
    public async Task The_library_lists_only_the_tenants_own_pictures_and_can_be_searched_by_words()
    {
        await Service().UploadAsync(_a, Upload(Png(10, 10, 1), "a.png", name: "Reception desk", tags: "front desk"));
        await Service().UploadAsync(_a, Upload(Png(10, 10, 2), "b.png", name: "Operating theatre"));
        await Service().UploadAsync(_b, Upload(Png(10, 10, 3), "c.png", name: "Reception of the other tenant"));

        Assert.Equal(2, (await Service().ListAsync(_a, null)).Count);
        Assert.Equal("Reception desk", Assert.Single(await Service().ListAsync(_a, "reception")).Name);
        Assert.Empty(await Service().ListAsync(_a, "dentist"));
    }

    // ----- candidates ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pictures_are_ranked_for_a_scene_by_the_words_they_share_with_its_picture_note_and_say_why()
    {
        await Service().UploadAsync(_a, Upload(Png(10, 10, 1), "a.png", name: "Waiting room"));
        await Service().UploadAsync(_a, Upload(Png(10, 10, 2), "b.png", name: "Clinic reception", tags: "desk, front desk"));
        await Service().UploadAsync(_a, Upload(Png(10, 10, 3), "c.png", name: "Doctor consulting"));
        var (scene, _) = await Scene(hint: "The reception desk of the clinic");

        var result = await Service().CandidatesAsync(_a, scene.Id, includeStock: false, null);

        Assert.Equal("reception desk clinic", result.Query);
        Assert.Equal("Clinic reception", result.Own[0].Asset.Name);
        Assert.Equal(1.0, result.Own[0].Score);
        Assert.Contains("Matches: reception, desk, clinic", result.Own[0].Reason);
        Assert.Equal(3, result.Own.Count);
        Assert.Equal(new[] { 0.0, 0.0 }, result.Own.Skip(1).Select(c => c.Score));
        Assert.Empty(result.Stock);
        Assert.Empty(_stock.Searches); // stock was not asked for
    }

    [Fact]
    public async Task Stock_search_uses_the_scene_note_or_the_tenants_own_words_and_shows_the_credit_and_licence()
    {
        _stock.Photos.Add(Photo());
        var (scene, _) = await Scene(hint: "reception desk");

        var byNote = await Service().CandidatesAsync(_a, scene.Id, includeStock: true, null);
        var byWords = await Service().CandidatesAsync(_a, scene.Id, includeStock: true, "  eye doctor  ");

        Assert.Equal(new[] { "reception desk", "eye doctor" }, _stock.Searches);
        var photo = Assert.Single(byNote.Stock);
        Assert.Equal(("Pexels", "Photo by Jane Doe on Pexels", "Pexels License"), (photo.Provider, photo.Attribution, photo.LicenseName));
        Assert.Equal("eye doctor", byWords.Query);
        Assert.True(byNote.StockAvailable);
        Assert.Null(byNote.StockMessage);
    }

    [Fact]
    public async Task When_stock_is_not_set_up_or_fails_the_tenant_is_told_and_their_own_pictures_still_work()
    {
        var (scene, _) = await Scene();
        _stock.IsConfigured = false;
        var off = await Service().CandidatesAsync(_a, scene.Id, includeStock: true, null);
        Assert.Equal((false, 0), (off.StockAvailable, off.Stock.Count));
        Assert.Contains("not set up", off.StockMessage);

        _stock.IsConfigured = true;
        _stock.Message = "The stock photo service is limiting requests right now.";
        var limited = await Service().CandidatesAsync(_a, scene.Id, includeStock: true, null);
        Assert.Equal(_stock.Message, limited.StockMessage);

        _stock.Message = null;
        Assert.Contains("No stock photos matched", (await Service().CandidatesAsync(_a, scene.Id, includeStock: true, null)).StockMessage);

        var (blank, _) = await Scene(hint: null);
        blank.Narration = "Hi";
        blank.OnScreenText = null;
        await _db.SaveChangesAsync();
        Assert.Contains("Describe the picture", (await Service().CandidatesAsync(_a, blank.Id, includeStock: true, null)).StockMessage);
    }

    // ----- choosing --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Choosing_one_of_the_tenants_pictures_sets_it_on_the_scene_and_clearing_takes_it_off()
    {
        var asset = await Service().UploadAsync(_a, Upload(Png(40, 30, 4), name: "Reception"));
        var (scene, _) = await Scene();

        var summary = await Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(asset.Id, null));

        Assert.Equal(("Your upload", "Reception", "Provided by you"), (summary.KindLabel, summary.Name, summary.LicenseName));
        Assert.Equal(asset.Id, (await _db.ContentScenes.SingleAsync()).ChosenAssetId);

        await Service().ClearAsync(_a, scene.Id);
        Assert.Null((await _db.ContentScenes.SingleAsync()).ChosenAssetId);
        await Assert.ThrowsAsync<ValidationException>(() => Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(null, " ")));
    }

    [Fact]
    public async Task A_stock_photo_is_copied_once_into_the_tenants_folder_with_the_licence_and_credit_from_the_library_not_the_browser()
    {
        _stock.Photos.Add(Photo("1001"));
        var (scene, _) = await Scene();

        var summary = await Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(null, "1001"));

        Assert.Equal(("Stock photo (Pexels)", "Pexels License", "Photo by Jane Doe on Pexels"), (summary.KindLabel, summary.LicenseName, summary.Attribution));
        var asset = await _db.StudioAssets.SingleAsync();
        Assert.Equal((StudioAssetKind.Stock, "Pexels", "1001", "https://www.pexels.com/photo/1001/"), (asset.Kind, asset.Provider, asset.ProviderAssetId, asset.SourceUrl));
        Assert.Contains($"private/{_a:N}/", asset.StorageKey);
        Assert.False(asset.RightsConfirmed); // the tenant did not vouch for it; the library's licence applies

        // Choosing it for a second scene does not download it again.
        var (second, _) = await Scene();
        await Service().ChooseAsync(_a, second.Id, new ChooseAssetRequest(null, "1001"));
        Assert.Equal(1, _stock.Downloads);
        Assert.Single(await _db.StudioAssets.ToListAsync());
    }

    [Fact]
    public async Task A_stock_photo_that_is_gone_cannot_be_downloaded_or_is_not_a_picture_is_refused_with_a_clear_message()
    {
        var (scene, _) = await Scene();
        _stock.Photos.Add(Photo("1001"));

        Assert.Contains("no longer available", (await Assert.ThrowsAsync<ConflictException>(() => Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(null, "999")))).Message);

        _stock.FailDownload = true;
        Assert.Contains("could not be downloaded", (await Assert.ThrowsAsync<ConflictException>(() => Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(null, "1001")))).Message);

        _stock.FailDownload = false;
        _stock.Image = System.Text.Encoding.ASCII.GetBytes("<html>not a picture at all, but long enough</html>");
        Assert.Contains("not a picture", (await Assert.ThrowsAsync<ConflictException>(() => Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(null, "1001")))).Message);

        _stock.IsConfigured = false;
        await Assert.ThrowsAsync<ConflictException>(() => Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(null, "1001")));

        Assert.Empty(await _db.StudioAssets.ToListAsync());
        Assert.Empty(_store.Files);
    }

    [Fact]
    public async Task A_picture_cannot_be_changed_once_the_script_is_no_longer_a_draft()
    {
        var asset = await Service().UploadAsync(_a, Upload(Png(40, 30, 4)));
        var (scene, _) = await Scene(status: ContentVersionStatus.Approved);

        Assert.Contains("no longer a draft", (await Assert.ThrowsAsync<ConflictException>(() => Service().ChooseAsync(_a, scene.Id, new ChooseAssetRequest(asset.Id, null)))).Message);
        await Assert.ThrowsAsync<ConflictException>(() => Service().ClearAsync(_a, scene.Id));
    }

    [Fact]
    public async Task Another_tenants_scenes_and_pictures_are_never_used_or_seen()
    {
        var mine = await Service().UploadAsync(_a, Upload(Png(40, 30, 1)));
        var theirs = await Service().UploadAsync(_b, Upload(Png(40, 30, 2)));
        var (myScene, _) = await Scene(_a);
        var (theirScene, _) = await Scene(_b);

        await Assert.ThrowsAsync<NotFoundException>(() => Service().ChooseAsync(_a, myScene.Id, new ChooseAssetRequest(theirs.Id, null))); // not their picture
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ChooseAsync(_a, theirScene.Id, new ChooseAssetRequest(mine.Id, null))); // not their scene
        await Assert.ThrowsAsync<NotFoundException>(() => Service().CandidatesAsync(_a, theirScene.Id, false, null));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ClearAsync(_a, theirScene.Id));

        var candidates = await Service().CandidatesAsync(_a, myScene.Id, false, null);
        Assert.Equal(mine.Id, Assert.Single(candidates.Own).Asset.Id);
    }

    [Fact]
    public async Task A_deleted_projects_scene_is_not_found()
    {
        var (scene, version) = await Scene();
        (await _db.ContentProjects.SingleAsync(p => p.Id == version.ProjectId)).IsDeleted = true;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Service().CandidatesAsync(_a, scene.Id, false, null));
    }

    // ----- the pieces ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Image_types_and_sizes_are_read_from_the_first_bytes()
    {
        var png = ImageSniffer.Detect(Png(640, 480))!;
        Assert.Equal(("image/png", "png", 640, 480), (png.ContentType, png.Extension, png.Width, png.Height));

        // JPEG: SOI, an APP0 segment, then SOF0 with the size (height first).
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0, 0, 0, 0, 0, 0, 0 };
        var j = ImageSniffer.Detect(jpeg)!;
        Assert.Equal(("image/jpeg", "jpg", 640, 480), (j.ContentType, j.Extension, j.Width, j.Height));

        // WebP (extended): canvas width-1 and height-1 as 24-bit little-endian values.
        var webp = new byte[40];
        "RIFF"u8.CopyTo(webp);
        "WEBPVP8X"u8.CopyTo(webp.AsSpan(8));
        webp[24] = 0x7F; webp[25] = 0x02; webp[27] = 0xDF; webp[28] = 0x01;
        var w = ImageSniffer.Detect(webp)!;
        Assert.Equal(("image/webp", 640, 480), (w.ContentType, w.Width, w.Height));

        Assert.Null(ImageSniffer.Detect(new byte[] { 1, 2, 3 }));
        Assert.Null(ImageSniffer.Detect("GIF89a"u8.ToArray()));
        Assert.Equal(ImageSniffer.Checksum(Png(1, 1)), ImageSniffer.Checksum(Png(1, 1)));
        Assert.NotEqual(ImageSniffer.Checksum(Png(1, 1, 1)), ImageSniffer.Checksum(Png(1, 1, 2)));
    }

    [Fact]
    public void Words_are_matched_without_filler_and_plurals_and_tags_are_tidied()
    {
        Assert.Equal(new[] { "reception", "desk", "clinic" }, AssetMatcher.Tokens("A shot of the reception desks of our clinics"));
        Assert.Equal("Picture", AssetMatcher.NameFromFile("..\\..\\.png"));
        Assert.Equal("front desk", AssetMatcher.NameFromFile("C:\\photos\\front_desk.PNG"));
        Assert.Equal("a, b, c", AssetMatcher.NormaliseTags("A; b\nC, a"));
        Assert.Null(AssetMatcher.NormaliseTags(" , ; "));
        Assert.Equal(30, AssetMatcher.NormaliseTags(new string('x', 99))!.Length);
    }

    [Fact]
    public void The_endpoints_are_for_admins_and_managers_only_and_need_the_studio_switched_on()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ContentStudioAssetsController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Api.Controllers.StudioRoles.Contributors, authorize.Roles);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(WhatsAppSalesAutomation.Api.Filters.RequiresFeatureAttribute)));
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));
    }

    [Fact]
    public void The_stock_key_is_a_secret_platform_setting()
    {
        var setting = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All.Single(d => d.Key == "ContentStudio:Stock:PexelsApiKey");
        Assert.True(setting.IsSecret);
        Assert.False(new StockOptions().IsConfigured);
        Assert.True(new StockOptions { PexelsApiKey = "k" }.IsConfigured);
    }
}
