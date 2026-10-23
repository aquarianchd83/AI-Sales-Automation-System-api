using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The platform wallet: one balance made of every top-up the admin recorded, each with its date and AI provider.</summary>
public sealed class PlatformWalletServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new() { UtcNow = new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc) };
    private readonly SqliteApplicationDbContext _db;
    private readonly PlatformWalletService _wallet;

    public PlatformWalletServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _wallet = new PlatformWalletService(_db, _clock, new AddWalletTopUpValidator());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static AddWalletTopUpRequest TopUp(string date, AiModelProvider provider, decimal amount) =>
        new(DateOnly.Parse(date), provider, amount);

    [Fact]
    public async Task A_new_wallet_is_empty()
    {
        var summary = await _wallet.GetSummaryAsync();

        Assert.Equal(0m, summary.Balance);
        Assert.Equal(0, summary.TopUpCount);
        Assert.Null(summary.LastTopUpDate);
    }

    [Fact]
    public async Task One_balance_adds_up_every_top_up_whichever_provider_it_was_for()
    {
        await _wallet.AddTopUpAsync(TopUp("2026-10-01", AiModelProvider.OpenAI, 1000m), null);
        await _wallet.AddTopUpAsync(TopUp("2026-10-10", AiModelProvider.Anthropic, 2500.50m), null);
        await _wallet.AddTopUpAsync(TopUp("2026-10-05", AiModelProvider.Google, 499.50m), null);

        var summary = await _wallet.GetSummaryAsync();

        Assert.Equal(4000m, summary.Balance);
        Assert.Equal(3, summary.TopUpCount);
        Assert.Equal(new DateOnly(2026, 10, 10), summary.LastTopUpDate);
    }

    [Fact]
    public async Task History_lists_the_latest_date_first_and_pages()
    {
        await _wallet.AddTopUpAsync(TopUp("2026-10-01", AiModelProvider.OpenAI, 1m), null);
        await _wallet.AddTopUpAsync(TopUp("2026-10-10", AiModelProvider.Anthropic, 2m), null);
        await _wallet.AddTopUpAsync(TopUp("2026-10-05", AiModelProvider.Google, 3m), null);

        var first = await _wallet.GetTopUpsAsync(new PagedRequest { Page = 1, PageSize = 2 });
        var second = await _wallet.GetTopUpsAsync(new PagedRequest { Page = 2, PageSize = 2 });

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(new[] { new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 5) }, first.Items.Select(t => t.Date));
        Assert.Equal(AiModelProvider.Anthropic, first.Items[0].Provider);
        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(second.Items).Date);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.555")]
    [InlineData("100000001")]
    public async Task An_amount_must_be_above_zero_and_sane(string amount)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _wallet.AddTopUpAsync(TopUp("2026-10-01", AiModelProvider.OpenAI, decimal.Parse(amount)), null));

        Assert.Equal(0, (await _wallet.GetSummaryAsync()).TopUpCount);
    }

    [Fact]
    public async Task A_date_in_the_future_or_an_unknown_provider_is_refused()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _wallet.AddTopUpAsync(TopUp("2026-10-25", AiModelProvider.OpenAI, 10m), null));
        await Assert.ThrowsAsync<ValidationException>(() => _wallet.AddTopUpAsync(TopUp("2026-10-01", (AiModelProvider)99, 10m), null));

        // Today, and a day ahead for a timezone that is already tomorrow, are fine.
        await _wallet.AddTopUpAsync(TopUp("2026-10-23", AiModelProvider.OpenAI, 10m), null);
        await _wallet.AddTopUpAsync(TopUp("2026-10-24", AiModelProvider.OpenAI, 10m), null);
        Assert.Equal(2, (await _wallet.GetSummaryAsync()).TopUpCount);
    }
}
