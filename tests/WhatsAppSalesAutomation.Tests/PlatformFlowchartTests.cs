using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class PlatformFlowchartTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly FlowchartService _service;

    public PlatformFlowchartTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _service = new FlowchartService(_db, new PlatformAuditService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_new_flowchart_starts_as_a_draft()
    {
        var created = await _service.CreateAsync(
            new CreateFlowchartRequest("Onboarding flow", "How a new tenant gets started", "Onboarding", "{\"nodes\":[]}"),
            actorUserId: Guid.NewGuid(), actorEmail: "admin@platform.test");

        Assert.Equal(ContentStatus.Draft, created.Status);
        Assert.Equal("Onboarding flow", created.Title);
    }

    [Fact]
    public async Task Only_published_flowcharts_are_returned_by_the_published_read_side()
    {
        var actorUserId = Guid.NewGuid();
        var draft = await _service.CreateAsync(new CreateFlowchartRequest("Draft flow", null, null, "{}"), actorUserId, "a@b.c");
        var published = await _service.CreateAsync(new CreateFlowchartRequest("Published flow", null, null, "{}"), actorUserId, "a@b.c");

        await _service.UpdateAsync(published.Id,
            new UpdateFlowchartRequest("Published flow", null, null, "{}", ContentStatus.Published, 0),
            actorUserId, "a@b.c");

        var publishedList = await _service.GetPublishedAsync();

        Assert.Single(publishedList);
        Assert.Equal(published.Id, publishedList[0].Id);
        Assert.DoesNotContain(publishedList, f => f.Id == draft.Id);
    }

    [Fact]
    public async Task Ordering_by_display_order_controls_the_management_list()
    {
        var actorUserId = Guid.NewGuid();
        var second = await _service.CreateAsync(new CreateFlowchartRequest("Second", null, null, "{}", DisplayOrder: 2), actorUserId, "a@b.c");
        var first = await _service.CreateAsync(new CreateFlowchartRequest("First", null, null, "{}", DisplayOrder: 1), actorUserId, "a@b.c");

        var all = await _service.GetAllAsync();

        Assert.Equal(first.Id, all[0].Id);
        Assert.Equal(second.Id, all[1].Id);
    }

    [Fact]
    public async Task Deleting_an_unknown_flowchart_throws_not_found()
        => await Assert.ThrowsAsync<NotFoundException>(() => _service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), "a@b.c"));

    [Fact]
    public async Task Delete_removes_the_flowchart()
    {
        var actorUserId = Guid.NewGuid();
        var created = await _service.CreateAsync(new CreateFlowchartRequest("To delete", null, null, "{}"), actorUserId, "a@b.c");

        await _service.DeleteAsync(created.Id, actorUserId, "a@b.c");

        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetByIdAsync(created.Id));
    }
}
