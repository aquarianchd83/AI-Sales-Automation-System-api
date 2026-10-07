using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai;

public class AiUsageService : IAiUsageService
{
    public const string InsufficientCreditsMessage = "Insufficient AI credits. Please purchase additional credits to continue using this feature.";

    private const decimal UnitsPerOperation = 1m;

    private readonly IApplicationDbContext _context;
    private readonly IQuotaLedgerService _ledger;
    private readonly IQuotaAlertService _alerts;
    private readonly IDateTimeProvider _dateTime;
    private readonly IPlatformAiConfigProvider _aiConfig;
    private readonly ILogger<AiUsageService> _logger;

    public AiUsageService(
        IApplicationDbContext context,
        IQuotaLedgerService ledger,
        IQuotaAlertService alerts,
        IDateTimeProvider dateTime,
        IPlatformAiConfigProvider aiConfig,
        ILogger<AiUsageService> logger)
    {
        _context = context;
        _ledger = ledger;
        _alerts = alerts;
        _dateTime = dateTime;
        _aiConfig = aiConfig;
        _logger = logger;
    }

    public async Task<AiAuthorization> AuthorizeAsync(Guid tenantId, string operation, string referenceId, CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        // A retry of an operation that already holds a credit must not spend a second one. A failed (refunded) or
        // blocked earlier attempt does not count: the retry is a fresh attempt with its own ledger key.
        var existing = await _context.AiTransactions.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Operation == operation && t.ReferenceId == referenceId
                        && (t.Status == AiTransactionStatus.Authorized || t.Status == AiTransactionStatus.Completed))
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } existingId)
            return new AiAuthorization(true, existingId, AiDenialReason.None, null);

        var tenant = await _context.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var subscription = await _context.Subscriptions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        var source = tenant is not null && TenantTrial.IsActive(tenant, now) ? AiUsageSource.Trial : AiUsageSource.Paid;
        var denial = CheckState(tenant, subscription, source, now);
        if (denial != AiDenialReason.None)
            return await BlockAsync(tenantId, subscription?.Id, operation, referenceId, source, denial, 0m, now, cancellationToken);

        var transactionId = Guid.NewGuid();
        var operationKey = $"ai:{transactionId}";
        var consumed = await _ledger.ConsumeAsync(
            new ConsumeRequest(tenantId, QuotaType.AiConversations, UnitsPerOperation, operationKey, "AiTransaction", transactionId.ToString(), operation),
            cancellationToken);

        if (!consumed.Sufficient)
        {
            var reason = source == AiUsageSource.Trial ? AiDenialReason.TrialLimitReached : AiDenialReason.InsufficientCredits;
            await TryNotifyExhaustedAsync(tenantId, cancellationToken);
            return await BlockAsync(tenantId, subscription?.Id, operation, referenceId, source, reason, consumed.BalanceAfter, now, cancellationToken);
        }

        var config = _aiConfig.Get();
        try
        {
            _context.AiTransactions.Add(new AiTransaction
            {
                Id = transactionId,
                TenantId = tenantId,
                SubscriptionId = subscription?.Id,
                Operation = operation,
                Source = source,
                Status = AiTransactionStatus.Authorized,
                Provider = config.Provider,
                Model = ModelFor(config),
                OperationKey = operationKey,
                ReferenceId = referenceId,
                CreditsBefore = consumed.BalanceAfter + consumed.Consumed,
                CreditsConsumed = consumed.Consumed,
                CreditsAfter = consumed.BalanceAfter,
                RequestedAtUtc = now
            });
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Never keep a credit that has no audit row behind it.
            await _ledger.ReverseConsumptionAsync(tenantId, operationKey, CancellationToken.None);
            throw;
        }

        // Spending the last credit is the moment to say so, not the next refusal.
        if (consumed.BalanceAfter <= 0)
            await TryNotifyExhaustedAsync(tenantId, cancellationToken);

        return new AiAuthorization(true, transactionId, AiDenialReason.None, null);
    }

    public async Task CompleteAsync(Guid transactionId, string provider, string model, int? promptTokens, int? completionTokens, CancellationToken cancellationToken = default)
    {
        var transaction = await _context.AiTransactions.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == transactionId, cancellationToken);
        if (transaction is null || transaction.Status != AiTransactionStatus.Authorized)
            return;

        transaction.Status = AiTransactionStatus.Completed;
        transaction.Provider = provider;
        transaction.Model = model;
        transaction.PromptTokens = promptTokens;
        transaction.CompletionTokens = completionTokens;
        transaction.CompletedAtUtc = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(Guid transactionId, string reason, string? provider, string? model, CancellationToken cancellationToken = default)
    {
        var open = await _context.AiTransactions.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == transactionId && t.Status == AiTransactionStatus.Authorized)
            .Select(t => new { t.TenantId, t.OperationKey })
            .FirstOrDefaultAsync(cancellationToken);
        if (open is null)
            return;

        // Reversal first and the row after: the ledger resets the change tracker when it retries a conflict.
        var released = await _ledger.ReverseConsumptionAsync(open.TenantId, open.OperationKey, cancellationToken);

        var transaction = await _context.AiTransactions.IgnoreQueryFilters().FirstAsync(t => t.Id == transactionId, cancellationToken);
        transaction.Status = AiTransactionStatus.Failed;
        transaction.FailureReason = Truncate(reason, 500);
        transaction.CreditsRefunded = released ? transaction.CreditsConsumed : 0m;
        transaction.CreditsAfter = transaction.CreditsBefore - transaction.CreditsConsumed + transaction.CreditsRefunded;
        if (!string.IsNullOrWhiteSpace(provider))
            transaction.Provider = provider;
        if (!string.IsNullOrWhiteSpace(model))
            transaction.Model = model;
        transaction.CompletedAtUtc = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("AI transaction {TransactionId} failed ({Reason}); {Refunded} credit(s) returned to tenant {TenantId}",
            transactionId, reason, transaction.CreditsRefunded, open.TenantId);
    }

    public async Task<PagedResult<AiTransactionDto>> GetHistoryAsync(Guid tenantId, AiTransactionQuery query, CancellationToken cancellationToken = default)
    {
        var rows = _context.AiTransactions.IgnoreQueryFilters().Where(t => t.TenantId == tenantId);
        if (query.Status is { } status)
            rows = rows.Where(t => t.Status == status);

        var total = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderByDescending(t => t.RequestedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new AiTransactionDto(
                t.Id, t.Operation, t.Source, t.Status, t.DenialReason, t.Provider, t.Model,
                t.CreditsBefore, t.CreditsConsumed, t.CreditsAfter, t.CreditsRefunded, t.FailureReason, t.RequestedAtUtc, t.CompletedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResult<AiTransactionDto>(items, total, query.Page, query.PageSize);
    }

    /// <summary>The subscription-state table: suspended, deleted, cancelled and expired subscriptions never reach the
    /// credit check; a trial is allowed until it ends (its allowance is then the limit); a paying tenant needs a live
    /// subscription and credit.</summary>
    private static AiDenialReason CheckState(Tenant? tenant, Subscription? subscription, AiUsageSource source, DateTime now)
    {
        if (tenant is null)
            return AiDenialReason.AccountInactive;

        switch (tenant.Status)
        {
            case TenantStatus.Suspended:
            case TenantStatus.Deleted:
                return AiDenialReason.AccountInactive;
            case TenantStatus.Cancelled:
                return AiDenialReason.SubscriptionCancelled;
        }

        if (source == AiUsageSource.Trial)
            return AiDenialReason.None;

        if (tenant.Status == TenantStatus.Trial)
            return AiDenialReason.TrialExpired;

        if (subscription is null || subscription.Status == SubscriptionStatus.Canceled)
            return AiDenialReason.SubscriptionCancelled;

        if (subscription.Status == SubscriptionStatus.PastDue || (subscription.CurrentPeriodEndUtc is { } end && end <= now))
            return AiDenialReason.SubscriptionExpired;

        return AiDenialReason.None;
    }

    private async Task<AiAuthorization> BlockAsync(
        Guid tenantId, Guid? subscriptionId, string operation, string referenceId, AiUsageSource source,
        AiDenialReason reason, decimal balance, DateTime now, CancellationToken cancellationToken)
    {
        var config = _aiConfig.Get();
        var message = MessageFor(reason);

        _context.AiTransactions.Add(new AiTransaction
        {
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            Operation = operation,
            Source = source,
            Status = AiTransactionStatus.Blocked,
            DenialReason = reason,
            Provider = config.Provider,
            Model = ModelFor(config),
            OperationKey = $"ai-blocked:{Guid.NewGuid()}",
            ReferenceId = referenceId,
            CreditsBefore = balance,
            CreditsAfter = balance,
            FailureReason = message,
            RequestedAtUtc = now,
            CompletedAtUtc = now
        });
        await _context.SaveChangesAsync(cancellationToken);

        return new AiAuthorization(false, null, reason, message);
    }

    private async Task TryNotifyExhaustedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await _alerts.NotifyIfExhaustedAsync(tenantId, QuotaType.AiConversations, cancellationToken);
        }
        catch (Exception ex)
        {
            // A notice that could not be raised never changes the decision.
            _logger.LogWarning(ex, "Could not raise the AI-credits-exhausted notice for tenant {TenantId}", tenantId);
        }
    }

    public static string MessageFor(AiDenialReason reason) => reason switch
    {
        AiDenialReason.TrialLimitReached => "Your free trial AI allowance is used up. Upgrade to a paid plan and purchase AI credits to continue using this feature.",
        AiDenialReason.TrialExpired => "Your free trial has ended. Upgrade to a paid plan and purchase AI credits to continue using this feature.",
        AiDenialReason.InsufficientCredits => InsufficientCreditsMessage,
        AiDenialReason.SubscriptionExpired => "Your subscription has expired. Renew it to continue using AI features.",
        AiDenialReason.SubscriptionCancelled => "Your subscription is cancelled. Subscribe to a plan to continue using AI features.",
        AiDenialReason.AccountInactive => "This account is not active, so AI features are unavailable.",
        _ => string.Empty
    };

    private static string ModelFor(AiCredentials c) => c.Provider.ToLowerInvariant() switch
    {
        "anthropic" => c.AnthropicModel,
        "openai" => c.OpenAiChatModel,
        "google" => c.GoogleChatModel,
        _ => "rule-based"
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
