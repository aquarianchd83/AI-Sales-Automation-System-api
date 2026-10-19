using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The Draft and Final knowledge files: the Final file holds the published items of a version and nothing else; the Draft file says plainly
/// that nothing in it is approved; a copy goes to the tenant's own AWS folder, and a storage failure never loses the file or the publish.
/// </summary>
public sealed class KnowledgeFileServiceTests : IDisposable
{
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

    private sealed class FakeStore : ITenantDocumentStore
    {
        public bool IsConfigured { get; set; } = true;
        public bool Fail { get; set; }
        public List<(Guid Tenant, string Path, string Text)> Saved { get; } = new();

        public async Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("AWS is down");
            using var reader = new StreamReader(content, Encoding.UTF8);
            Saved.Add((tenantId, relativePath, await reader.ReadToEndAsync(cancellationToken)));
            return "s3://" + FolderOf(tenantId) + relativePath;
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string FolderOf(Guid tenantId) => $"private/{tenantId:N}/product-knowledge/";
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Admin _admin = new();
    private readonly FakeStore _store = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private int _order;

    public KnowledgeFileServiceTests()
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
                entry.Entity.CreatedAt = DateTime.UtcNow.AddSeconds(++tick);
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private KnowledgeFileService Files() => new(_db, _store, NullLogger<KnowledgeFileService>.Instance);

    private KnowledgePublishingService Publishing() => new(_db, _admin);

    private static string Text(KnowledgeFileResult file) => Encoding.UTF8.GetString(file.Content).TrimStart('﻿');

    private async Task<KnowledgeAnswer> Answer(string question, string category, string summary, KnowledgeVerificationStatus status, Guid? tenant = null, bool simulated = false, bool material = false)
    {
        var tenantId = tenant ?? _a;
        var q = new KnowledgeQuestion { TenantId = tenantId, Text = question, Category = category, SortOrder = ++_order };
        _db.KnowledgeQuestions.Add(q);
        var a = new KnowledgeAnswer
        {
            TenantId = tenantId, QuestionId = q.Id, Summary = summary, Detail = "- first\n- second", Confidence = KnowledgeConfidence.High, Status = status, IsSimulated = simulated, IsMaterialClaim = material,
            Provider = "OpenAI", GeneratedAt = DateTime.UtcNow, IsCurrent = true, ApprovedBy = status == KnowledgeVerificationStatus.VerifiedAndApproved ? _admin.UserId : null,
            ApprovedAt = status == KnowledgeVerificationStatus.VerifiedAndApproved ? DateTime.UtcNow : null, ReviewDueAt = material ? DateTime.UtcNow.AddDays(180) : null
        };
        _db.KnowledgeAnswers.Add(a);
        _db.AnswerEvidences.Add(new AnswerEvidence
        {
            TenantId = tenantId, AnswerId = a.Id, SourceId = Guid.NewGuid(), PassageId = Guid.NewGuid(), SourceVersion = 1, Excerpt = $"quoted   words\nfor {summary}", SourceName = "brochure.pdf", Location = "Page 2",
            PassageHash = "h" + _order
        });
        await _db.SaveChangesAsync();
        return a;
    }

    [Fact]
    public async Task The_final_file_holds_the_published_items_only_grouped_by_category_with_where_each_came_from()
    {
        await Answer("What does it cost?", "Pricing", "The fee is Rs 500.", KnowledgeVerificationStatus.VerifiedAndApproved, material: true);
        await Answer("Main benefit?", "Benefits", "Saves time.", KnowledgeVerificationStatus.VerifiedAndApproved);
        var version = await Publishing().PublishAsync(_a, new PublishRequest("October release"));

        // Added after publishing: none of these may appear in the published file.
        await Answer("Pending one?", "Benefits", "NOT YET CHECKED TEXT", KnowledgeVerificationStatus.PendingVerification);
        await Answer("Rejected one?", "Benefits", "REJECTED TEXT", KnowledgeVerificationStatus.Rejected);
        await Answer("Test one?", "Benefits", "TEST DATA TEXT", KnowledgeVerificationStatus.VerifiedAndApproved, simulated: true);

        var file = await Files().BuildFinalAsync(_a, version.Id);
        var text = Text(file);

        Assert.Equal("product-knowledge-v1.txt", file.FileName);
        Assert.StartsWith("PRODUCT KNOWLEDGE - VERSION 1 - APPROVED", text);
        Assert.Contains("by owner@clinic.example", text);
        Assert.Contains("Note: October release", text);
        Assert.True(text.IndexOf("BENEFITS", StringComparison.Ordinal) < text.IndexOf("PRICING", StringComparison.Ordinal));
        Assert.Contains("The fee is Rs 500.", text);
        Assert.Contains("Saves time.", text);
        Assert.Contains("     - first", text);
        Assert.Contains("Source: \"quoted words for The fee is Rs 500.\" (brochure.pdf, Page 2)", text); // whitespace collapsed, source named
        Assert.Contains("to be confirmed again by", text);
        Assert.Contains("2 approved answers.", text);
        Assert.DoesNotContain("NOT YET CHECKED", text);
        Assert.DoesNotContain("REJECTED TEXT", text);
        Assert.DoesNotContain("TEST DATA TEXT", text);
    }

    [Fact]
    public async Task The_final_file_of_an_old_version_stays_as_it_was_published_and_says_it_was_replaced()
    {
        var answer = await Answer("Main benefit?", "Benefits", "Saves time.", KnowledgeVerificationStatus.VerifiedAndApproved);
        var v1 = await Publishing().PublishAsync(_a, new PublishRequest(null));
        answer.Summary = "Saves an hour a day."; // an edit afterwards
        await Answer("Another?", "Benefits", "Cuts cost.", KnowledgeVerificationStatus.VerifiedAndApproved);
        await Publishing().PublishAsync(_a, new PublishRequest(null));

        var old = Text(await Files().BuildFinalAsync(_a, v1.Id));

        Assert.Contains("replaced by a newer version", old);
        Assert.Contains("Saves time.", old);
        Assert.DoesNotContain("Cuts cost.", old);
        Assert.Contains("1 approved answers.", old);
    }

    [Fact]
    public async Task The_draft_file_says_nothing_is_approved_and_shows_every_question_with_its_status_conflicts_and_gaps()
    {
        var pending = await Answer("Main benefit?", "Benefits", "Saves time.", KnowledgeVerificationStatus.PendingVerification);
        await Answer("Placeholder?", "Benefits", "Lorem.", KnowledgeVerificationStatus.PendingVerification, simulated: true);
        var price = await Answer("Price?", "Pricing", "Costs Rs 999.", KnowledgeVerificationStatus.PendingVerification, material: true);
        _db.KnowledgeConflicts.Add(new KnowledgeConflict { TenantId = _a, AnswerId = price.Id, Fact = "price", DetectedAt = DateTime.UtcNow });
        var unknown = await Answer("Guarantee?", "Pricing", "", KnowledgeVerificationStatus.InformationNotAvailable);
        unknown.Summary = null;
        pending.Limitation = "The duration is not stated.";
        pending.ClarifyingQuestion = "How long is a session?";
        _db.KnowledgeQuestions.Add(new KnowledgeQuestion { TenantId = _a, Text = "Untouched?", Category = "Benefits", SortOrder = 99 });
        await _db.SaveChangesAsync();

        var text = Text(await Files().BuildDraftAsync(_a));

        Assert.StartsWith("PRODUCT KNOWLEDGE - DRAFT - NOT APPROVED", text);
        Assert.Contains("Nothing in it is approved or published", text);
        Assert.Contains("Status: WAITING FOR YOUR CHECK - NOT APPROVED", text);
        Assert.Contains("TEST DATA: written by the platform's test mode", text);
        Assert.Contains("CONFLICT (unresolved): your sources disagree about the price.", text);
        Assert.Contains("Answer: none - the sources do not answer this.", text);
        Assert.Contains("Not settled by the sources: The duration is not stated.", text);
        Assert.Contains("Still to ask you: How long is a session?", text);
        Assert.Contains("Status: NOT DRAFTED YET", text);
        Assert.Contains("5 questions, 4 still unresolved.", text);
    }

    [Fact]
    public async Task A_copy_goes_to_the_tenants_own_private_folder_and_never_another_tenants()
    {
        await Answer("Main benefit?", "Benefits", "Saves time.", KnowledgeVerificationStatus.VerifiedAndApproved);
        var version = await Publishing().PublishAsync(_a, new PublishRequest(null));

        var final = await Files().BuildFinalAsync(_a, version.Id);
        var draft = await Files().BuildDraftAsync(_a);

        Assert.Contains($"private/{_a:N}/product-knowledge/files/final/product-knowledge-v1.txt", final.StoredKey);
        Assert.Contains("files/draft/product-knowledge-draft.txt", draft.StoredKey);
        Assert.All(_store.Saved, s => Assert.Equal(_a, s.Tenant));
        Assert.Contains("VERSION 1", _store.Saved.First(s => s.Path.Contains("final")).Text);
    }

    [Fact]
    public async Task When_aws_is_not_set_up_or_is_down_the_file_is_still_returned_and_saving_the_copy_never_fails_the_caller()
    {
        await Answer("Main benefit?", "Benefits", "Saves time.", KnowledgeVerificationStatus.VerifiedAndApproved);
        var version = await Publishing().PublishAsync(_a, new PublishRequest(null));

        _store.IsConfigured = false;
        var notConfigured = await Files().BuildFinalAsync(_a, version.Id);
        Assert.Null(notConfigured.StoredKey);
        Assert.Contains("Saves time.", Text(notConfigured));

        _store.IsConfigured = true;
        _store.Fail = true;
        var failing = await Files().BuildDraftAsync(_a);
        Assert.Null(failing.StoredKey);
        await Files().SaveFinalCopyAsync(_a, version.Id); // does not throw
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task Another_tenants_version_is_not_found_and_the_file_has_a_byte_order_mark_for_windows_editors()
    {
        await Answer("Theirs?", "Benefits", "Theirs.", KnowledgeVerificationStatus.VerifiedAndApproved, tenant: _b);
        _tenant.TenantId = _b;
        var theirs = await Publishing().PublishAsync(_b, new PublishRequest(null));
        _tenant.TenantId = _a;

        await Assert.ThrowsAsync<NotFoundException>(() => Files().BuildFinalAsync(_a, theirs.Id));

        var draft = await Files().BuildDraftAsync(_a);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, draft.Content.Take(3).ToArray());
        Assert.DoesNotContain("Theirs.", Text(draft));
    }
}
