using System.Text;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The tenant's sources end to end against a database: upload checks, the tenant's AWS folder, duplicate detection, the queue, retry and
/// re-scan, deletion, downloads, tenant isolation - and the background reading: page-numbered passages, scanned pages following the
/// platform's switch, website pages with duplicates, the extracted text file. Storage, the AI and the crawler are fakes.
/// </summary>
public sealed class KnowledgeSourceServiceTests : IDisposable
{
    // ----- fakes -----------------------------------------------------------------------------------------------------------------------

    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    /// <summary>The private AWS store in memory, with the real folder layout.</summary>
    private sealed class MemoryStore : ITenantDocumentStore
    {
        public bool IsConfigured { get; set; } = true;
        public Dictionary<string, (byte[] Bytes, string ContentType)> Files { get; } = new();
        public List<string> Deleted { get; } = new();

        public string FolderOf(Guid tenantId) => $"private/{tenantId:N}/product-knowledge/";

        public async Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var key = "s3:" + FolderOf(tenantId) + relativePath;
            Files[key] = (buffer.ToArray(), contentType);
            return key;
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Files.TryGetValue(storageKey, out var f) ? Task.FromResult<Stream>(new MemoryStream(f.Bytes)) : throw new FileNotFoundException(storageKey);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            Deleted.Add(storageKey);
            Files.Remove(storageKey);
            return Task.CompletedTask;
        }

        public string Text(string key) => Encoding.UTF8.GetString(Files[key].Bytes);
    }

    private sealed class RecordingQueue : IKnowledgeSourceQueue
    {
        public List<(Guid SourceId, Guid TenantId)> Queued { get; } = new();
        public void Enqueue(Guid sourceId, Guid tenantId) => Queued.Add((sourceId, tenantId));
    }

    private sealed class FixedLimits : ISourceLimitsProvider
    {
        public SourceLimits Value { get; set; } = new(1, 5, 2, 10, 2, 10, 1024, true);
        public SourceLimits Get() => Value;
    }

    private sealed class Routing : IAiFeatureRouting
    {
        public AiFeatureChoice Scanned { get; set; } = new(false, "OpenAI", IsOff: true);
        public AiFeatureChoice Resolve(string feature) => feature == AiFeatures.ProductScannedReading ? Scanned : new AiFeatureChoice(false, "OpenAI");
    }

    private sealed class ScriptedAi : IAiTaskRunner
    {
        public List<AiTaskRequest> Calls { get; } = new();
        public Func<AiTaskRequest, AiTaskResult> Respond { get; set; } = r => Result(true, $"Text read from page: {r.User}", AiExecutionMode.Live);

        public static AiTaskResult Result(bool ok, string? text, AiExecutionMode mode) =>
            new(ok, text, mode, "OpenAI", "gpt", false, ok ? null : "PROVIDER_ERROR", ok ? null : "the provider failed", Guid.NewGuid(), Array.Empty<AiAttempt>(), 10, 10, 0.001m);

        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class ScriptedCrawler : IWebsiteCrawler
    {
        public CrawlResult Result { get; set; } = new("https://clinic.example/", Array.Empty<CrawledPage>(), "No page of the website could be read.");
        public List<CrawlOptions> Options { get; } = new();

        public Task<CrawlResult> CrawlAsync(string startUrl, CrawlOptions options, CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            return Task.FromResult(Result);
        }
    }

    // ----- setup -------------------------------------------------------------------------------------------------------------------

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly MemoryStore _store = new();
    private readonly RecordingQueue _queue = new();
    private readonly FixedLimits _limits = new();
    private readonly Routing _routing = new();
    private readonly ScriptedAi _ai = new();
    private readonly ScriptedCrawler _crawler = new();
    private readonly PagedDocumentReader _reader = new(new IDocumentFormatExtractor[] { new TextDocumentExtractor(), new HtmlDocumentExtractor(), new DocxDocumentExtractor() });
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    public KnowledgeSourceServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _tenant.TenantId = _tenantA;
        _db = new SqliteApplicationDbContext(options, _tenant, new NoUser());
        _db.Database.EnsureCreated();
        var tick = 0;
        _db.BeforeSave = db =>
        {
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added))
                entry.Entity.CreatedAt = DateTime.UtcNow.AddSeconds(++tick);
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private KnowledgeSourceService Service() => new(_db, _store, _reader, _queue, _limits, _routing);

    private KnowledgeSourceProcessor Processor() =>
        new(_db, _store, _reader, _crawler, _ai, _routing, _limits, NullLogger<KnowledgeSourceProcessor>.Instance);

    private Task<KnowledgeSourceDto> Upload(string name, byte[] bytes) =>
        Service().UploadAsync(_tenantA, _user, name, "application/pdf", new MemoryStream(bytes), bytes.Length);

    private async Task<KnowledgeSource> Reload(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.KnowledgeSources.SingleAsync(s => s.Id == id);
    }

    private static readonly byte[] TwoPagePdf = ProductKnowledgeReadingTests.Pdf(
        "Consultation fee is Rs 500 for the first visit to the clinic.",
        "Cataract surgery starts at Rs 25,000 per eye at our hospital.");

    // ----- upload ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_upload_is_stored_privately_in_the_tenants_aws_folder_and_queued_for_reading()
    {
        var source = await Upload("Brochure 2026.pdf", TwoPagePdf);

        Assert.Equal("Queued", source.Status);
        Assert.Equal("Document", source.Kind);
        Assert.Equal(TwoPagePdf.Length, source.SizeBytes);
        Assert.Equal((source.Id, _tenantA), Assert.Single(_queue.Queued));

        var key = Assert.Single(_store.Files.Keys);
        Assert.Equal($"s3:private/{_tenantA:N}/product-knowledge/sources/{source.Id:N}/original.pdf", key);
        Assert.Equal(TwoPagePdf, _store.Files[key].Bytes);
    }

    [Fact]
    public async Task The_same_file_uploaded_again_is_recognised_and_not_read_twice()
    {
        await Upload("brochure.pdf", TwoPagePdf);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Upload("brochure-copy.pdf", TwoPagePdf));

        Assert.Contains("already uploaded this file as \"brochure.pdf\"", ex.Message);
        Assert.Single(_queue.Queued);
        Assert.Single(_store.Files);
    }

    [Fact]
    public async Task The_same_file_from_another_tenant_is_not_a_duplicate()
    {
        _db.KnowledgeSources.Add(new KnowledgeSource { TenantId = _tenantB, Name = "theirs.pdf", ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TwoPagePdf)).ToLowerInvariant() });
        await _db.SaveChangesAsync();

        var mine = await Upload("mine.pdf", TwoPagePdf);

        Assert.Equal("Queued", mine.Status);
    }

    [Fact]
    public async Task Size_type_and_content_are_checked_before_anything_is_stored()
    {
        var big = new byte[_limits.Value.MaxFileBytes + 1];
        Assert.Contains("most you can upload is 1 MB", (await Assert.ThrowsAsync<ValidationException>(() => Upload("big.pdf", big))).Message);

        Assert.Contains("not supported", (await Assert.ThrowsAsync<ValidationException>(() => Upload("tool.exe", new byte[] { 1 }))).Message);
        Assert.Contains("does not look like a real .pdf", (await Assert.ThrowsAsync<ValidationException>(() => Upload("fake.pdf", Encoding.UTF8.GetBytes("MZ..")))).Message);

        Assert.Empty(_store.Files);
        Assert.Empty(_queue.Queued);
    }

    [Fact]
    public async Task The_platforms_source_limit_is_enforced()
    {
        _limits.Value = _limits.Value with { MaxSourcesPerTenant = 1 };
        await Upload("one.pdf", TwoPagePdf);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Upload("two.txt", Encoding.UTF8.GetBytes("Another document with enough text in it.")));

        Assert.Contains("the most allowed", ex.Message);
    }

    [Fact]
    public async Task Without_aws_settings_an_upload_is_refused_with_a_sentence_saying_so()
    {
        _store.IsConfigured = false;

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(() => Upload("brochure.pdf", TwoPagePdf));

        Assert.Contains("AWS Settings", ex.Message);
    }

    // ----- websites ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("clinic.example", "https://clinic.example/")]
    [InlineData("  HTTPS://WWW.Clinic.Example/about#team ", "https://www.clinic.example/about")]
    public async Task A_website_is_added_with_its_address_normalised_and_queued(string typed, string stored)
    {
        var source = await Service().AddWebsiteAsync(_tenantA, _user, new AddWebsiteSourceRequest(typed));

        Assert.Equal("Website", source.Kind);
        Assert.Equal(stored, source.Url);
        Assert.Single(_queue.Queued);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a site")]
    [InlineData("ftp://clinic.example")]
    [InlineData("http://10.0.0.5")]
    [InlineData("https://clinic.example:8443")]
    [InlineData("https://user:pw@clinic.example")]
    public async Task An_unusable_website_address_is_refused(string typed)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service().AddWebsiteAsync(_tenantA, _user, new AddWebsiteSourceRequest(typed)));
        Assert.Empty(_queue.Queued);
    }

    [Fact]
    public async Task The_same_website_twice_is_refused_with_a_pointer_to_scan_again()
    {
        await Service().AddWebsiteAsync(_tenantA, _user, new AddWebsiteSourceRequest("clinic.example"));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().AddWebsiteAsync(_tenantA, _user, new AddWebsiteSourceRequest("https://clinic.example/")));

        Assert.Contains("Scan again", ex.Message);
    }

    // ----- reading documents -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pdf_is_read_into_page_numbered_passages_and_its_text_is_saved_in_the_tenants_folder()
    {
        var uploaded = await Upload("brochure.pdf", TwoPagePdf);

        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Ready, source.Status);
        Assert.Equal(1, source.Version);
        Assert.Equal(1, source.Attempts);
        Assert.Equal(2, source.PageCount);
        var passages = await _db.SourcePassages.Where(p => p.SourceId == source.Id).OrderBy(p => p.Order).ToListAsync();
        Assert.Equal(new int?[] { 1, 2 }, passages.Select(p => p.PageNumber));
        Assert.Contains("Rs 500", passages[0].Text);
        Assert.All(passages, p => Assert.Equal(64, p.Hash.Length));
        Assert.All(passages, p => Assert.Equal(_tenantA, p.TenantId));

        Assert.Equal($"s3:private/{_tenantA:N}/product-knowledge/sources/{source.Id:N}/extracted-v1.txt", source.TextStorageKey);
        var text = _store.Text(source.TextStorageKey!);
        Assert.Contains("SOURCE: brochure.pdf", text);
        Assert.Contains("--- Page 2 ---", text);
        Assert.Contains("25,000", text);
    }

    [Fact]
    public async Task A_fully_scanned_document_fails_with_a_clear_reason_while_scanned_reading_is_off_and_no_ai_is_called()
    {
        var uploaded = await Upload("scan.pdf", ProductKnowledgeReadingTests.Pdf(null, null));

        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Failed, source.Status);
        Assert.Equal("ScannedReadingOff", source.ErrorCode);
        Assert.Contains("switched off", source.Error);
        Assert.Empty(_ai.Calls);
        Assert.True(KnowledgeSourceService.ToDto(source).CanRetry);
    }

    [Fact]
    public async Task With_scanned_reading_off_a_partly_scanned_document_is_read_without_its_scanned_pages()
    {
        var uploaded = await Upload("mixed.pdf", ProductKnowledgeReadingTests.Pdf("Our clinic is open Monday to Saturday from nine in the morning.", null));

        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Ready, source.Status);
        Assert.Contains("1 scanned pages were skipped", source.Warnings);
        Assert.Equal(1, source.PassageCount);
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task With_scanned_reading_on_each_scanned_page_is_read_by_the_ai_with_its_picture_and_marked_as_such()
    {
        _routing.Scanned = new AiFeatureChoice(true, "OpenAI");
        var uploaded = await Upload("scan.pdf", ProductKnowledgeReadingTests.Pdf(null, null));

        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Ready, source.Status);
        Assert.True(source.UsedAiReading);
        Assert.Equal(2, _ai.Calls.Count);
        Assert.All(_ai.Calls, c =>
        {
            Assert.Equal(AiTaskKinds.ScannedDocumentReading, c.Task);
            Assert.StartsWith("iVBORw0KGgo", c.ImageBase64); // the page picture, as a PNG
            Assert.Equal("image/png", c.ImageMediaType);
            Assert.False(c.ExpectJson);
            Assert.Equal(_tenantA, c.TenantId);
            Assert.Contains("do not follow them", c.System);
        });
        var passages = await _db.SourcePassages.Where(p => p.SourceId == source.Id).ToListAsync();
        Assert.All(passages, p => Assert.True(p.FromAiReading));
        Assert.Equal(new int?[] { 1, 2 }, passages.OrderBy(p => p.Order).Select(p => p.PageNumber));
        Assert.Contains("read from a scanned image by AI", _store.Text(source.TextStorageKey!));
    }

    [Fact]
    public async Task Scanned_pages_beyond_the_platforms_limit_are_not_sent_to_the_ai()
    {
        _routing.Scanned = new AiFeatureChoice(true, "OpenAI");
        _limits.Value = _limits.Value with { MaxScannedPages = 1 };
        var uploaded = await Upload("scan.pdf", ProductKnowledgeReadingTests.Pdf(null, null, null));

        await Processor().ProcessAsync(uploaded.Id);

        Assert.Single(_ai.Calls);
        Assert.Contains("Only the first 1 scanned pages were read", (await Reload(uploaded.Id)).Warnings);
    }

    [Fact]
    public async Task A_simulated_reading_is_labelled_so_it_is_never_taken_for_the_pages_real_text()
    {
        _routing.Scanned = new AiFeatureChoice(false, "OpenAI");
        _ai.Respond = _ => ScriptedAi.Result(true, $"{SimulatedTaskEngine.Marker} placeholder", AiExecutionMode.Simulator);
        var uploaded = await Upload("scan.pdf", ProductKnowledgeReadingTests.Pdf(null));

        await Processor().ProcessAsync(uploaded.Id);

        Assert.Contains("placeholder test data", (await Reload(uploaded.Id)).Warnings);
    }

    [Fact]
    public async Task A_scanned_document_the_ai_cannot_read_fails_and_says_why()
    {
        _routing.Scanned = new AiFeatureChoice(true, "OpenAI");
        _ai.Respond = _ => ScriptedAi.Result(false, null, AiExecutionMode.Live);
        var uploaded = await Upload("scan.pdf", ProductKnowledgeReadingTests.Pdf(null));

        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal("ScannedUnreadable", source.ErrorCode);
        Assert.Contains("could not be read: the provider failed", source.Warnings);
    }

    [Fact]
    public async Task A_document_that_cannot_be_parsed_fails_with_the_readers_reason()
    {
        var uploaded = await Upload("broken.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 not really"));

        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Failed, source.Status);
        Assert.Equal("Corrupt", source.ErrorCode);
    }

    [Fact]
    public async Task A_source_that_is_not_queued_is_left_alone()
    {
        var uploaded = await Upload("brochure.pdf", TwoPagePdf);
        await Processor().ProcessAsync(uploaded.Id);

        await Processor().ProcessAsync(uploaded.Id); // a duplicate job run

        Assert.Equal(1, (await Reload(uploaded.Id)).Attempts);
    }

    // ----- reading websites ------------------------------------------------------------------------------------------------------------

    private async Task<Guid> Website()
    {
        var source = await Service().AddWebsiteAsync(_tenantA, _user, new AddWebsiteSourceRequest("clinic.example"));
        return source.Id;
    }

    [Fact]
    public async Task A_website_is_read_into_passages_with_the_page_address_and_a_repeated_page_is_a_duplicate()
    {
        _crawler.Result = new CrawlResult("https://clinic.example/", new[]
        {
            new CrawledPage("https://clinic.example/", 0, "Read", 200, "Home", "# Home\n\nWelcome to Clinic, eye care since 2005.", null),
            new CrawledPage("https://clinic.example/index.html", 1, "Read", 200, "Home", "# Home\n\nWelcome to Clinic,  eye care since 2005.", null),
            new CrawledPage("https://clinic.example/prices", 1, "Read", 200, "Prices", "Consultation Rs 500.", null),
            new CrawledPage("https://clinic.example/gone", 1, "Failed", 404, null, null, "The website answered 404."),
            new CrawledPage("https://clinic.example/private", 1, "Blocked", null, null, null, "The site's robots.txt asks not to read this page."),
        }, null);
        var id = await Website();

        await Processor().ProcessAsync(id);

        var source = await Reload(id);
        Assert.Equal(KnowledgeSourceStatus.Ready, source.Status);
        Assert.Equal(2, source.PageCount);
        var options = Assert.Single(_crawler.Options);
        Assert.Equal((10, 2, 1024 * 1024L, true), (options.MaxPages, options.MaxDepth, options.MaxPageBytes, options.RespectRobots));

        var pages = await _db.KnowledgeSourcePages.Where(p => p.SourceId == id).ToListAsync();
        Assert.Equal(KnowledgeSourcePageStatus.Duplicate, pages.Single(p => p.Url.EndsWith("index.html")).Status);
        Assert.Equal(KnowledgeSourcePageStatus.Failed, pages.Single(p => p.Url.EndsWith("/gone")).Status);
        Assert.Equal(KnowledgeSourcePageStatus.Blocked, pages.Single(p => p.Url.EndsWith("/private")).Status);

        var passages = await _db.SourcePassages.Where(p => p.SourceId == id).OrderBy(p => p.Order).ToListAsync();
        Assert.Equal(new[] { "https://clinic.example/", "https://clinic.example/prices" }, passages.Select(p => p.Url));
        Assert.Equal("Prices", passages[1].Heading); // the page title stands in for a missing heading
        Assert.Contains("1 pages could not be read", source.Warnings);
    }

    [Fact]
    public async Task A_website_that_cannot_be_read_fails_and_still_shows_which_pages_were_tried()
    {
        _crawler.Result = new CrawlResult("https://clinic.example/",
            new[] { new CrawledPage("https://clinic.example/", 0, "Failed", 500, null, null, "The website answered 500.") }, "The website answered 500.");
        var id = await Website();

        await Processor().ProcessAsync(id);

        var detail = await Service().GetAsync(_tenantA, id);
        Assert.Equal("Failed", detail.Source.Status);
        Assert.Equal("Unreachable", detail.Source.ErrorCode);
        Assert.Contains("500", detail.Source.Error);
        Assert.Equal("Failed", Assert.Single(detail.Pages).Status);
    }

    [Fact]
    public async Task Scanning_again_makes_a_new_version_and_a_failed_rescan_keeps_the_last_good_reading()
    {
        _crawler.Result = new CrawlResult("https://clinic.example/", new[] { new CrawledPage("https://clinic.example/", 0, "Read", 200, "Home", "Fee is Rs 500 for a visit.", null) }, null);
        var id = await Website();
        await Processor().ProcessAsync(id);
        var firstText = (await Reload(id)).TextStorageKey!;

        _crawler.Result = new CrawlResult("https://clinic.example/", new[] { new CrawledPage("https://clinic.example/", 0, "Read", 200, "Home", "Fee is Rs 600 for a visit.", null) }, null);
        Assert.True((await Service().RetryAsync(_tenantA, id)).Status == "Queued");
        await Processor().ProcessAsync(id);

        var second = await Reload(id);
        Assert.Equal(2, second.Version);
        var passage = Assert.Single(await _db.SourcePassages.Where(p => p.SourceId == id).ToListAsync());
        Assert.Equal(2, passage.SourceVersion);
        Assert.Contains("Rs 600", passage.Text);
        Assert.Contains(firstText, _store.Deleted);
        Assert.EndsWith("extracted-v2.txt", second.TextStorageKey);

        _crawler.Result = new CrawlResult("https://clinic.example/", Array.Empty<CrawledPage>(), "The website could not be reached.");
        await Service().RetryAsync(_tenantA, id);
        await Processor().ProcessAsync(id);

        var third = await Reload(id);
        Assert.Equal(KnowledgeSourceStatus.Failed, third.Status);
        Assert.Equal(2, third.Version);
        Assert.Contains("Rs 600", Assert.Single(await _db.SourcePassages.Where(p => p.SourceId == id).ToListAsync()).Text);
    }

    // ----- retry, delete, download, isolation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_failed_source_or_a_read_website_can_be_retried()
    {
        var doc = await Upload("brochure.pdf", TwoPagePdf);
        await Assert.ThrowsAsync<ConflictException>(() => Service().RetryAsync(_tenantA, doc.Id)); // still queued

        await Processor().ProcessAsync(doc.Id);
        Assert.Contains("upload the new version", (await Assert.ThrowsAsync<ConflictException>(() => Service().RetryAsync(_tenantA, doc.Id))).Message);
    }

    [Fact]
    public async Task A_failed_document_can_be_retried_and_is_read_again()
    {
        var uploaded = await Upload("scan.pdf", ProductKnowledgeReadingTests.Pdf(null));
        await Processor().ProcessAsync(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Failed, (await Reload(uploaded.Id)).Status);

        _routing.Scanned = new AiFeatureChoice(true, "OpenAI"); // the platform admin switched scanned reading on
        var retried = await Service().RetryAsync(_tenantA, uploaded.Id);
        Assert.Equal("Queued", retried.Status);
        Assert.Null(retried.Error);
        await Processor().ProcessAsync(uploaded.Id);

        var source = await Reload(uploaded.Id);
        Assert.Equal(KnowledgeSourceStatus.Ready, source.Status);
        Assert.Equal(2, source.Attempts);
        Assert.Equal(2, _queue.Queued.Count);
    }

    [Fact]
    public async Task Deleting_a_source_removes_its_files_and_passages_and_the_same_file_can_be_uploaded_again()
    {
        var uploaded = await Upload("brochure.pdf", TwoPagePdf);
        await Processor().ProcessAsync(uploaded.Id);

        await Service().DeleteAsync(_tenantA, uploaded.Id);

        Assert.Empty(_store.Files);
        Assert.Equal(2, _store.Deleted.Count);
        Assert.Empty(await _db.SourcePassages.ToListAsync());
        Assert.Empty((await Service().ListAsync(_tenantA)).Sources);
        Assert.True((await _db.KnowledgeSources.IgnoreQueryFilters().SingleAsync(s => s.Id == uploaded.Id)).IsDeleted);

        var again = await Upload("brochure.pdf", TwoPagePdf);
        Assert.Equal("Queued", again.Status);
    }

    [Fact]
    public async Task The_original_and_the_extracted_text_are_handed_back_through_the_api_only()
    {
        var uploaded = await Upload("Price List.pdf", TwoPagePdf);
        await Processor().ProcessAsync(uploaded.Id);

        var original = await Service().OpenFileAsync(_tenantA, uploaded.Id, extracted: false);
        Assert.Equal("Price List.pdf", original.FileName);
        Assert.Equal(TwoPagePdf, ((MemoryStream)original.Content).ToArray());

        var text = await Service().OpenFileAsync(_tenantA, uploaded.Id, extracted: true);
        Assert.Equal("Price List-extracted-v1.txt", text.FileName);
        Assert.StartsWith("text/plain", text.ContentType);
    }

    [Fact]
    public async Task Another_tenants_source_is_not_found_by_any_operation()
    {
        var theirs = new KnowledgeSource { TenantId = _tenantB, Name = "theirs.pdf", Kind = KnowledgeSourceKind.Document, Status = KnowledgeSourceStatus.Failed,
            StorageKey = $"s3:private/{_tenantB:N}/product-knowledge/sources/x/original.pdf" };
        _db.KnowledgeSources.Add(theirs);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(_tenantA, theirs.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RetryAsync(_tenantA, theirs.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteAsync(_tenantA, theirs.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenFileAsync(_tenantA, theirs.Id, false));
        Assert.Empty((await Service().ListAsync(_tenantA)).Sources);

        // Even scoped as tenant B, a file key pointing outside B's folder is refused.
        theirs.StorageKey = $"s3:private/{_tenantA:N}/product-knowledge/sources/y/original.pdf";
        await _db.SaveChangesAsync();
        _tenant.TenantId = _tenantB;
        await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenFileAsync(_tenantB, theirs.Id, false));
    }

    [Fact]
    public async Task The_overview_says_how_scanned_pages_are_read_and_what_can_be_uploaded()
    {
        var overview = await Service().ListAsync(_tenantA);

        Assert.Equal("Off", overview.ScannedReading);
        Assert.Equal(5, overview.MaxSources);
        Assert.Equal(1, overview.MaxFileMb);
        Assert.Contains(".pdf", overview.SupportedExtensions);
        Assert.Contains(".png", overview.SupportedExtensions);
        Assert.True(overview.StorageReady);

        _routing.Scanned = new AiFeatureChoice(true, "Google");
        Assert.Equal("Api", (await Service().ListAsync(_tenantA)).ScannedReading);
    }

    [Fact]
    public void A_storage_path_can_never_step_out_of_the_tenants_folder()
    {
        Assert.Equal("sources/abc/original.pdf", S3TenantDocumentStore.Clean("../../sources/./abc/original.pdf"));
        Assert.Equal("etcpasswd", S3TenantDocumentStore.Clean("/etc:passwd"));
        Assert.Throws<ArgumentException>(() => S3TenantDocumentStore.Clean("../.."));
    }

    [Fact]
    public void Every_limit_is_a_platform_setting_with_a_default_and_the_endpoints_are_tenant_admin_only()
    {
        var keys = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All.Where(d => d.Key.StartsWith("ProductKnowledge:")).ToList();
        Assert.Equal(8, keys.Count);
        Assert.All(keys, d => Assert.False(string.IsNullOrWhiteSpace(d.DefaultValue), d.Key));
        Assert.Equal("10", keys.Single(d => d.Key == "ProductKnowledge:MaxFileMb").DefaultValue);
        Assert.Equal("true", keys.Single(d => d.Key == "ProductKnowledge:CrawlRespectRobots").DefaultValue);

        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ProductKnowledgeSourcesController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Domain.Constants.AppRoles.Admin, authorize.Roles);
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));
    }

    [Fact]
    public void Out_of_range_settings_are_clamped_so_a_typo_cannot_open_the_gates()
    {
        var provider = new SourceLimitsProvider(new FixedOptions<ProductKnowledgeSettings>(new ProductKnowledgeSettings
        {
            MaxFileMb = 100000, CrawlMaxPages = -3, CrawlMaxDepth = 99, MaxScannedPages = 9999, CrawlPageTimeoutSeconds = 0
        }));

        var limits = provider.Get();

        Assert.Equal((200, 1, 5, 200, 3), (limits.MaxFileMb, limits.CrawlMaxPages, limits.CrawlMaxDepth, limits.MaxScannedPages, limits.CrawlPageTimeoutSeconds));
    }

    // ----- the platform's two switches on Integrations ----------------------------------------------------------------------------

    [Fact]
    public async Task With_document_upload_switched_off_no_file_is_taken_or_read_again_and_what_was_read_stays()
    {
        var uploaded = await Upload("brochure.pdf", TwoPagePdf);
        await Processor().ProcessAsync(uploaded.Id);
        _limits.Value = _limits.Value with { DocumentUploadEnabled = false };

        var ex = await Assert.ThrowsAsync<FeatureDisabledException>(() => Upload("other.txt", Encoding.UTF8.GetBytes("Another document with enough text in it.")));
        Assert.Contains("Uploading documents is switched off", ex.Message);
        Assert.Single(_store.Files.Keys, k => k.EndsWith("original.pdf"));

        Assert.Equal(2, await _db.SourcePassages.CountAsync(p => p.SourceId == uploaded.Id)); // the earlier reading stays
        var overview = await Service().ListAsync(_tenantA);
        Assert.False(overview.DocumentUploadEnabled);
        Assert.True(overview.WebsiteScanningEnabled);
    }

    [Fact]
    public async Task With_website_scanning_switched_off_no_website_is_added_or_scanned_again()
    {
        _crawler.Result = new CrawlResult("https://clinic.example/", new[] { new CrawledPage("https://clinic.example/", 0, "Read", 200, "Home", "Fee is Rs 500.", null) }, null);
        var id = await Website();
        await Processor().ProcessAsync(id);
        _limits.Value = _limits.Value with { WebsiteScanningEnabled = false };

        Assert.Contains("Reading websites is switched off",
            (await Assert.ThrowsAsync<FeatureDisabledException>(() => Service().AddWebsiteAsync(_tenantA, _user, new AddWebsiteSourceRequest("other.example")))).Message);
        await Assert.ThrowsAsync<FeatureDisabledException>(() => Service().RetryAsync(_tenantA, id));
        Assert.Single(_crawler.Options); // never crawled again
        Assert.Equal(1, await _db.SourcePassages.CountAsync(p => p.SourceId == id));
    }

    [Fact]
    public async Task A_source_queued_just_before_its_switch_went_off_is_not_read()
    {
        var id = await Website();
        _limits.Value = _limits.Value with { WebsiteScanningEnabled = false };

        await Processor().ProcessAsync(id);

        var source = await Reload(id);
        Assert.Equal(KnowledgeSourceStatus.Failed, source.Status);
        Assert.Equal("WebsiteScanningOff", source.ErrorCode);
        Assert.Empty(_crawler.Options);
        Assert.Equal(0, source.Attempts);
    }

    [Fact]
    public void Both_switches_are_integrations_settings_that_start_on()
    {
        var keys = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All.Where(d => d.Key.StartsWith("Integrations:ProductSources:")).ToList();

        Assert.Equal(new[] { "Integrations:ProductSources:DocumentUpload", "Integrations:ProductSources:WebsiteScanning" }, keys.Select(k => k.Key));
        Assert.All(keys, k => Assert.Equal(("Integrations", "true"), (k.Category, k.DefaultValue)));

        var defaults = new SourceLimitsProvider(new FixedOptions<ProductKnowledgeSettings>(new ProductKnowledgeSettings())).Get();
        Assert.True(defaults.DocumentUploadEnabled && defaults.WebsiteScanningEnabled);
        var off = new SourceLimitsProvider(new FixedOptions<ProductKnowledgeSettings>(new ProductKnowledgeSettings()),
            new FixedOptions<ProductSourceSwitches>(new ProductSourceSwitches { WebsiteScanning = false })).Get();
        Assert.False(off.WebsiteScanningEnabled);
        Assert.True(off.DocumentUploadEnabled);
    }
}
