using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Adding a top-up: the date it was made, the AI provider it was for, and the amount in rupees.</summary>
public record AddWalletTopUpRequest(DateOnly Date, AiModelProvider Provider, decimal Amount);

public record WalletTopUpDto(Guid Id, DateOnly Date, AiModelProvider Provider, decimal Amount, DateTime AddedAtUtc);

/// <summary>The one platform wallet: the total added so far across every provider.</summary>
public record PlatformWalletSummaryDto(decimal Balance, int TopUpCount, DateOnly? LastTopUpDate);

public class AddWalletTopUpValidator : AbstractValidator<AddWalletTopUpRequest>
{
    public const decimal MaxAmount = 100_000_000m;

    public AddWalletTopUpValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MaxAmount).PrecisionScale(18, 2, false)
            .WithMessage($"Amount must be above 0, at most {MaxAmount:N0}, with no more than 2 decimals.");
        RuleFor(x => x.Date).Must(d => d > DateOnly.MinValue).WithMessage("Date is required.");
    }
}

/// <summary>The platform admin's wallet of money loaded into the AI providers - one balance, with every top-up kept as history.</summary>
public interface IPlatformWalletService
{
    Task<PlatformWalletSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Every top-up, the latest date first.</summary>
    Task<PagedResult<WalletTopUpDto>> GetTopUpsAsync(PagedRequest query, CancellationToken cancellationToken = default);

    Task<WalletTopUpDto> AddTopUpAsync(AddWalletTopUpRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
}

public class PlatformWalletService : IPlatformWalletService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<AddWalletTopUpRequest> _validator;

    public PlatformWalletService(IApplicationDbContext context, IDateTimeProvider dateTime, IValidator<AddWalletTopUpRequest> validator)
    {
        _context = context;
        _dateTime = dateTime;
        _validator = validator;
    }

    public async Task<PlatformWalletSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var rows = _context.PlatformWalletTopUps.AsNoTracking();
        var count = await rows.CountAsync(cancellationToken);
        if (count == 0)
            return new PlatformWalletSummaryDto(0m, 0, null);

        // Summed client-side: SQLite has no decimal SUM, and there are never many top-ups.
        var amounts = await rows.Select(t => t.AmountInr).ToListAsync(cancellationToken);
        var dates = await rows.Select(t => t.TopUpDate).ToListAsync(cancellationToken);
        return new PlatformWalletSummaryDto(amounts.Sum(), count, dates.Max());
    }

    public async Task<PagedResult<WalletTopUpDto>> GetTopUpsAsync(PagedRequest query, CancellationToken cancellationToken = default)
    {
        var rows = _context.PlatformWalletTopUps.AsNoTracking();
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(t => t.TopUpDate).ThenByDescending(t => t.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var items = page.Select(t => new WalletTopUpDto(t.Id, t.TopUpDate, t.Provider, t.AmountInr, t.CreatedAt)).ToList();
        return new PagedResult<WalletTopUpDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<WalletTopUpDto> AddTopUpAsync(AddWalletTopUpRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        // A top-up is something that already happened: today's date is fine in any timezone, tomorrow's is not.
        var latestAllowed = DateOnly.FromDateTime(_dateTime.UtcNow).AddDays(1);
        if (request.Date > latestAllowed)
            throw new ValidationException(new[] { new FluentValidation.Results.ValidationFailure(nameof(request.Date), "The date cannot be in the future.") });

        var topUp = new PlatformWalletTopUp
        {
            TopUpDate = request.Date,
            Provider = request.Provider,
            AmountInr = request.Amount,
            ActorUserId = actorUserId
        };
        _context.PlatformWalletTopUps.Add(topUp);
        await _context.SaveChangesAsync(cancellationToken);

        return new WalletTopUpDto(topUp.Id, topUp.TopUpDate, topUp.Provider, topUp.AmountInr, topUp.CreatedAt);
    }
}
