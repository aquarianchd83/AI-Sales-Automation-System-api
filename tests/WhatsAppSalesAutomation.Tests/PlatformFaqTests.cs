using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class PlatformFaqTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly FaqService _service;

    public PlatformFaqTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _service = new FaqService(_db, new PlatformAuditService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_new_entry_starts_as_a_draft()
    {
        var created = await _service.CreateAsync(
            new CreateFaqEntryRequest("How do I reset my password?", "Use the forgot-password link on the sign-in page.", "Account"),
            actorUserId: Guid.NewGuid(), actorEmail: "admin@platform.test");

        Assert.Equal(ContentStatus.Draft, created.Status);
        Assert.Equal("How do I reset my password?", created.Question);
    }

    [Fact]
    public async Task Only_published_entries_are_returned_by_the_published_read_side()
    {
        var actorUserId = Guid.NewGuid();
        var draft = await _service.CreateAsync(new CreateFaqEntryRequest("Draft question", "Answer", null), actorUserId, "a@b.c");
        var published = await _service.CreateAsync(new CreateFaqEntryRequest("Published question", "Answer", null), actorUserId, "a@b.c");

        await _service.UpdateAsync(published.Id,
            new UpdateFaqEntryRequest("Published question", "Answer", null, ContentStatus.Published, 0),
            actorUserId, "a@b.c");

        var publishedList = await _service.GetPublishedAsync();

        Assert.Single(publishedList);
        Assert.Equal(published.Id, publishedList[0].Id);
        Assert.DoesNotContain(publishedList, f => f.Id == draft.Id);
    }

    [Fact]
    public async Task Updating_an_unknown_entry_throws_not_found()
        => await Assert.ThrowsAsync<NotFoundException>(() => _service.UpdateAsync(
            Guid.NewGuid(), new UpdateFaqEntryRequest("Q", "A", null, ContentStatus.Draft, 0), Guid.NewGuid(), "a@b.c"));

    [Fact]
    public async Task Delete_removes_the_entry()
    {
        var actorUserId = Guid.NewGuid();
        var created = await _service.CreateAsync(new CreateFaqEntryRequest("To delete", "Answer", null), actorUserId, "a@b.c");

        await _service.DeleteAsync(created.Id, actorUserId, "a@b.c");

        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetByIdAsync(created.Id));
    }
}
