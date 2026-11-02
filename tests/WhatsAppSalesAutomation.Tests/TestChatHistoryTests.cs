using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A tenant's Test chat replies reach the AI provider but take no credits, so they are not in the credit ledger. The credit history lists them apart,
/// as free, so the tenant can see they happened.</summary>
public sealed class TestChatHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new();
    private readonly Tenant _tenant = new() { Name = "T", Slug = "t" };
    private readonly Tenant _other = new() { Name = "Other", Slug = "other" };

    public TestChatHistoryTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _db.Tenants.AddRange(_tenant, _other);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private QuotaLedgerService Ledger() => new(_db, _clock);

    private async Task AddCallAsync(
        Guid tenantId, DateTime atUtc, string? origin = AiCallOrigins.TestChat, string task = AiTaskKinds.ConversationReply,
        AiExecutionStatus status = AiExecutionStatus.Succeeded, AiExecutionMode mode = AiExecutionMode.Live, string? detail = null)
    {
        _db.AiExecutions.Add(new AiExecution
        {
            TenantId = tenantId, Task = task, Provider = "OpenAI", RequestedProvider = "OpenAI", Model = "OpenAI:gpt-5-nano", Origin = origin, Detail = detail,
            Mode = mode, Status = status, EstimatedCostUsd = 0.002m, CreatedAt = atUtc
        });
        await _db.SaveChangesAsync();
    }

    private static readonly DateTime Noon = new(2026, 10, 30, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Only_this_tenants_successful_test_chat_replies_are_listed_newest_first()
    {
        await AddCallAsync(_tenant.Id, Noon.AddMinutes(-5));
        await AddCallAsync(_tenant.Id, Noon);

        // Not test chat replies of this tenant: a customer's real reply, another tenant's test, a failed attempt, another kind of call, a simulated one.
        await AddCallAsync(_tenant.Id, Noon, origin: null);
        await AddCallAsync(_other.Id, Noon);
        await AddCallAsync(_tenant.Id, Noon, status: AiExecutionStatus.Failed);
        await AddCallAsync(_tenant.Id, Noon, task: AiTaskKinds.KnowledgeEmbedding);
        await AddCallAsync(_tenant.Id, Noon, mode: AiExecutionMode.Simulator);

        var page = await Ledger().GetTestChatAsync(_tenant.Id, new QuotaLedgerQuery());

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { Noon, Noon.AddMinutes(-5) }, page.Items.Select(i => i.OccurredAtUtc));
        Assert.All(page.Items, i => Assert.Equal("OpenAI:gpt-5-nano", i.Model));
    }

    [Fact]
    public async Task The_list_is_paged()
    {
        for (var i = 0; i < 5; i++)
            await AddCallAsync(_tenant.Id, Noon.AddMinutes(-i));

        var second = await Ledger().GetTestChatAsync(_tenant.Id, new QuotaLedgerQuery { Page = 2, PageSize = 2 });

        Assert.Equal(5, second.TotalCount);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(Noon.AddMinutes(-2), second.Items[0].OccurredAtUtc);
    }

    [Fact]
    public async Task A_day_is_the_tenants_own_day_not_a_utc_day()
    {
        // 19:00 UTC on 29 Oct is 00:30 on 30 Oct in India (the default zone).
        await AddCallAsync(_tenant.Id, new DateTime(2026, 10, 29, 19, 0, 0, DateTimeKind.Utc));

        var on30 = await Ledger().GetTestChatAsync(_tenant.Id, new QuotaLedgerQuery { From = new DateOnly(2026, 10, 30), To = new DateOnly(2026, 10, 30) });
        var on29 = await Ledger().GetTestChatAsync(_tenant.Id, new QuotaLedgerQuery { From = new DateOnly(2026, 10, 29), To = new DateOnly(2026, 10, 29) });

        Assert.Equal(1, on30.TotalCount);
        Assert.Equal(0, on29.TotalCount);
    }

    [Fact]
    public async Task The_summary_counts_test_chat_replies_in_the_range_apart_from_the_credit_entries()
    {
        await AddCallAsync(_tenant.Id, Noon);
        await AddCallAsync(_tenant.Id, Noon.AddMinutes(-30));
        await AddCallAsync(_tenant.Id, Noon.AddDays(-10));

        var all = await Ledger().GetLedgerSummaryAsync(_tenant.Id, 30);
        var today = await Ledger().GetLedgerSummaryAsync(_tenant.Id, 30, from: new DateOnly(2026, 10, 30), to: new DateOnly(2026, 10, 30));

        Assert.Equal(3, all.TestChatReplies);
        Assert.Equal(2, today.TestChatReplies);
        // Free, so they are not credit movements.
        Assert.Equal(0, all.TotalEntries);
        Assert.All(all.Daily, d => Assert.Equal(0m, d.Used));
    }

    private sealed class CapturingLog : IAiExecutionLog
    {
        public List<AiExecution> Written { get; } = new();

        public Task WriteAsync(AiExecution execution, CancellationToken cancellationToken = default)
        {
            Written.Add(execution);
            return Task.CompletedTask;
        }
    }

    private sealed class OneTenant : WhatsAppSalesAutomation.Application.Common.Interfaces.ITenantContext
    {
        public Guid? TenantId { get; } = Guid.NewGuid();
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    [Fact]
    public async Task A_call_recorded_inside_the_test_chat_scope_is_stamped_and_one_outside_is_not()
    {
        var log = new CapturingLog();
        var policy = new AiTaskPolicyProvider(
            new FixedOptions<AiFeatureOptions>(new()),
            new FixedOptions<AiProviderSettings>(new()));
        var recorder = new AiCallRecorder(
            log, policy, new OneTenant(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AiCallRecorder>.Instance);

        using (AiCallOrigin.Use(AiCallOrigins.TestChat))
            await recorder.RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, "OpenAI", "gpt-5-nano", true, 10);
        await recorder.RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, "OpenAI", "gpt-5-nano", true, 10);

        Assert.Equal(AiCallOrigins.TestChat, log.Written[0].Origin);
        Assert.Null(log.Written[1].Origin);
    }

    [Fact]
    public async Task The_first_words_typed_in_a_test_are_kept_with_the_call_and_listed_as_its_text()
    {
        var log = new CapturingLog();
        var policy = new AiTaskPolicyProvider(new FixedOptions<AiFeatureOptions>(new()), new FixedOptions<AiProviderSettings>(new()));
        var recorder = new AiCallRecorder(log, policy, new OneTenant(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AiCallRecorder>.Instance);

        using (AiCallOrigin.Use(AiCallOrigins.TestChat, "tell me something about it"))
            await recorder.RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, "OpenAI", "gpt-5-nano", true, 10);
        // Outside a test the words belong to nobody: a customer's reply keeps no detail on the call.
        await recorder.RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, "OpenAI", "gpt-5-nano", true, 10);

        Assert.Equal("tell me something about it", log.Written[0].Detail);
        Assert.Null(log.Written[1].Detail);

        await AddCallAsync(_tenant.Id, Noon, detail: "tell me something about it");
        var page = await Ledger().GetTestChatAsync(_tenant.Id, new QuotaLedgerQuery());
        Assert.Equal("tell me something about it", Assert.Single(page.Items).Text);
    }

    [Fact]
    public void The_origin_scope_marks_calls_inside_it_and_nothing_outside()
    {
        Assert.Null(AiCallOrigin.Current);

        using (AiCallOrigin.Use(AiCallOrigins.TestChat))
            Assert.Equal(AiCallOrigins.TestChat, AiCallOrigin.Current);

        Assert.Null(AiCallOrigin.Current);
    }

    [Fact]
    public async Task The_origin_scope_follows_the_call_across_awaits()
    {
        using var scope = AiCallOrigin.Use(AiCallOrigins.TestChat);

        await Task.Delay(1);

        Assert.Equal(AiCallOrigins.TestChat, AiCallOrigin.Current);
    }
}
