using System.Globalization;
using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Wallet;

/// <summary>
/// Buying wallet credits with Razorpay. The tenant always comes from the caller's JWT (never the request body), the amount is re-priced here, and
/// whether money moved is decided on the server - from the checkout signature plus Razorpay's own record of the payment, or from a signed webhook,
/// or from the reconciliation job asking Razorpay directly. All three end in <see cref="CompleteAsync"/>, which credits at most once.
/// </summary>
public interface IWalletRechargeService
{
    Task<RechargeOptionsDto> GetOptionsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<RechargeQuoteDto> QuoteAsync(Guid tenantId, decimal amount, CancellationToken cancellationToken = default);

    Task<WalletBalanceDto> GetBalanceAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<RechargeCheckoutDto> CreateOrderAsync(Guid tenantId, Guid? userId, CreateRechargeOrderRequest request, CancellationToken cancellationToken = default);

    Task<RechargeVerifyResultDto> VerifyAsync(Guid tenantId, VerifyRechargePaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>The transaction's state. A top-up still waiting is checked against Razorpay first, so a tenant who closed the browser mid-payment sees the truth.</summary>
    Task<RechargeTransactionDto> GetStatusAsync(Guid tenantId, Guid transactionId, CancellationToken cancellationToken = default);

    Task<PagedResult<RechargeTransactionDto>> GetHistoryAsync(Guid tenantId, PagedRequest paging, CancellationToken cancellationToken = default);

    /// <summary>Applies a signed Razorpay event to the wallet top-up it belongs to. <see cref="WalletWebhookResult.Matched"/> is false for any other order.</summary>
    Task<WalletWebhookResult> HandleWebhookAsync(WalletWebhookEvent webhook, CancellationToken cancellationToken = default);

    /// <summary>Settles top-ups that never heard back (browser closed, webhook lost) by asking Razorpay what happened to each order. Returns how many changed.</summary>
    Task<int> ReconcilePendingAsync(CancellationToken cancellationToken = default);
}

public class WalletRechargeService : IWalletRechargeService
{
    public const string Source = "RAZORPAY";
    private const int ReconcileBatch = 50;
    private const int MaxPendingOrders = 5;

    private readonly IApplicationDbContext _context;
    private readonly IAppSettingsStore _store;
    private readonly IRazorpayGateway _gateway;
    private readonly IPricingService _pricing;
    private readonly QuotaLedgerService _ledger;
    private readonly ICreditWalletService _wallet;
    private readonly IDateTimeProvider _clock;
    private readonly IOptionsSnapshot<WalletRechargeOptions> _options;
    private readonly ILogger<WalletRechargeService> _logger;

    public WalletRechargeService(
        IApplicationDbContext context, IAppSettingsStore store, IRazorpayGateway gateway, IPricingService pricing, QuotaLedgerService ledger,
        ICreditWalletService wallet, IDateTimeProvider clock, IOptionsSnapshot<WalletRechargeOptions> options, ILogger<WalletRechargeService> logger)
    {
        _context = context;
        _store = store;
        _gateway = gateway;
        _pricing = pricing;
        _ledger = ledger;
        _wallet = wallet;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    private WalletRechargeOptions Options => _options.Value;

    // ── Read side ───────────────────────────────────────────────────────────────────────

    public async Task<RechargeOptionsDto> GetOptionsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var o = Options;
        return new RechargeOptionsDto("INR", o.MinRechargeInr, o.MaxRechargeInr, o.CreditsPerRupee, o.PresetList(), await BalanceAsync(tenantId, cancellationToken));
    }

    public async Task<WalletBalanceDto> GetBalanceAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        new(await BalanceAsync(tenantId, cancellationToken), "credits");

    public async Task<RechargeQuoteDto> QuoteAsync(Guid tenantId, decimal amount, CancellationToken cancellationToken = default)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        var (quote, credits) = Price(tenant, amount);
        var before = await BalanceAsync(tenantId, cancellationToken);
        return new RechargeQuoteDto(quote.Subtotal, quote.Tax, quote.Total, quote.CurrencyCode, quote.TaxLines, credits, before, before + credits);
    }

    public async Task<RechargeTransactionDto> GetStatusAsync(Guid tenantId, Guid transactionId, CancellationToken cancellationToken = default)
    {
        var txn = await OwnedAsync(tenantId, t => t.Id == transactionId, cancellationToken)
            ?? throw new NotFoundException(nameof(PaymentTransaction), transactionId);

        if (txn.Status is PaymentTransactionStatus.Created or PaymentTransactionStatus.Authorized or PaymentTransactionStatus.Failed)
        {
            await ReconcileOneAsync(txn, cancellationToken);
            txn = await OwnedAsync(tenantId, t => t.Id == transactionId, cancellationToken) ?? txn;
        }

        return (await ToDtosAsync(tenantId, new[] { txn }, cancellationToken))[0];
    }

    public async Task<PagedResult<RechargeTransactionDto>> GetHistoryAsync(Guid tenantId, PagedRequest paging, CancellationToken cancellationToken = default)
    {
        var rows = _context.PaymentTransactions.IgnoreQueryFilters().AsNoTracking().Where(t => t.TenantId == tenantId);
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(t => t.CreatedAt).Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<RechargeTransactionDto>(await ToDtosAsync(tenantId, page, cancellationToken), total, paging.Page, paging.PageSize);
    }

    // ── Create the order ────────────────────────────────────────────────────────────────

    public async Task<RechargeCheckoutDto> CreateOrderAsync(Guid tenantId, Guid? userId, CreateRechargeOrderRequest request, CancellationToken cancellationToken = default)
    {
        var currency = (request.Currency ?? "INR").Trim().ToUpperInvariant();
        if (currency != "INR")
            throw Invalid(nameof(request.Currency), "Wallet recharge is in INR.");

        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        var (quote, credits) = Price(tenant, request.Amount);
        var credentials = await RequireCredentialsAsync(cancellationToken);

        // A guard against a script spraying orders: a handful of unpaid orders at a time is plenty for a person.
        var since = _clock.UtcNow.AddMinutes(-10);
        var pending = await _context.PaymentTransactions.IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == tenantId && t.Status == PaymentTransactionStatus.Created && t.CreatedAt > since, cancellationToken);
        if (pending >= MaxPendingOrders)
            throw new ConflictException("You have several recharges waiting for payment. Finish or wait a few minutes before starting another.");

        var amountMinor = (long)decimal.Round(quote.Total * 100m, 0, MidpointRounding.AwayFromZero);
        var reference = $"wr-{_clock.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(8).ToLowerInvariant()}";
        var notes = new Dictionary<string, string> { ["source"] = "wallet-recharge", ["reference"] = reference, ["tenant"] = tenantId.ToString("N") };

        var created = await _gateway.CreateOrderAsync(credentials, amountMinor, "INR", reference, notes, cancellationToken);
        if (!created.Success || created.Value is null)
        {
            _logger.LogWarning("Wallet recharge order could not be created at Razorpay for tenant {TenantId}: {Error}", tenantId, created.Error);
            throw new ConflictException($"Razorpay could not create the order: {created.Error}");
        }

        var txn = new PaymentTransaction
        {
            TenantId = tenantId,
            InternalTransactionId = reference,
            RazorpayOrderId = created.Value.Id,
            Amount = quote.Subtotal,
            TaxAmount = quote.Tax,
            TotalAmount = quote.Total,
            AmountMinor = amountMinor,
            Currency = "INR",
            Credits = credits,
            CreditsPerRupee = Options.CreditsPerRupee,
            TaxLinesJson = quote.TaxLinesJson,
            AmountUsdCents = quote.SubtotalUsdCents,
            FxRateToInr = quote.FxRateToInr,
            TotalInr = quote.TotalInr,
            CountryCode = quote.CountryCode,
            StateCode = quote.StateCode,
            Mode = credentials.Mode,
            CreatedByUserId = userId,
            // Stamped here as well as by the audit interceptor: the reconciliation windows are measured from it.
            CreatedAt = _clock.UtcNow
        };
        _context.PaymentTransactions.Add(txn);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Wallet recharge {Reference} created for tenant {TenantId}: order {OrderId}, {Amount} INR ({Mode})", reference, tenantId, txn.RazorpayOrderId, txn.TotalAmount, txn.Mode);

        return new RechargeCheckoutDto(
            txn.Id, reference, txn.RazorpayOrderId, credentials.KeyId, txn.Mode, amountMinor, "INR", Options.BusinessName,
            $"Wallet recharge ({credits:#,##0.##} credits)", txn.Amount, txn.TaxAmount, txn.TotalAmount, txn.Credits, new RechargePrefillDto(tenant.Name, null, null));
    }

    // ── Verify what the browser reports ─────────────────────────────────────────────────

    public async Task<RechargeVerifyResultDto> VerifyAsync(Guid tenantId, VerifyRechargePaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId) || string.IsNullOrWhiteSpace(request.RazorpayPaymentId) || string.IsNullOrWhiteSpace(request.RazorpaySignature))
            throw Invalid(nameof(request.RazorpaySignature), "The order id, payment id and signature are all required.");

        // Looked up by the tenant AND the order: another tenant's order id is simply not found.
        var orderId = request.RazorpayOrderId.Trim();
        var txn = await OwnedAsync(tenantId, t => t.RazorpayOrderId == orderId, cancellationToken)
            ?? throw new NotFoundException(nameof(PaymentTransaction), orderId);
        var paymentId = request.RazorpayPaymentId.Trim();

        async Task<RechargeVerifyResultDto> Result(bool success, bool pending, string message)
        {
            var fresh = await OwnedAsync(tenantId, t => t.Id == txn.Id, cancellationToken) ?? txn;
            var dto = (await ToDtosAsync(tenantId, new[] { fresh }, cancellationToken))[0];
            return new RechargeVerifyResultDto(success, pending, message, dto, success ? await BalanceAsync(tenantId, cancellationToken) : null);
        }

        // A replay of a checkout already settled: nothing to redo, and nothing is credited again.
        if (IsCredited(txn))
        {
            return txn.RazorpayPaymentId == paymentId
                ? await Result(true, false, "This payment was already confirmed and your wallet credited.")
                : await Result(false, false, "This recharge was already paid with a different payment.");
        }

        var credentials = await RequireCredentialsAsync(cancellationToken);
        if (!RazorpaySignatures.IsValidPaymentSignature(txn.RazorpayOrderId, paymentId, request.RazorpaySignature, credentials.KeySecret))
        {
            _logger.LogWarning("Wallet recharge {Reference}: checkout signature did not match (tenant {TenantId}); nothing credited", txn.InternalTransactionId, tenantId);
            return await Result(false, false, "The payment could not be verified: the signature does not match.");
        }

        var fetched = await _gateway.FetchPaymentAsync(credentials, paymentId, cancellationToken);
        if (!fetched.Success || fetched.Value is null)
            return await Result(false, true, "Your payment is genuine but Razorpay's record could not be read yet. Your wallet will be credited automatically once it is confirmed.");

        var outcome = await SettlePaymentAsync(txn, fetched.Value, credentials, cancellationToken);
        return outcome switch
        {
            Settlement.Credited or Settlement.AlreadyCredited => await Result(true, false, "Payment confirmed and your wallet credited."),
            Settlement.Pending => await Result(false, true, "Your payment is being confirmed. Your wallet will be credited automatically."),
            _ => await Result(false, false, (await OwnedAsync(tenantId, t => t.Id == txn.Id, cancellationToken))?.FailureReason ?? "The payment did not go through.")
        };
    }

    // ── Webhooks ────────────────────────────────────────────────────────────────────────

    public async Task<WalletWebhookResult> HandleWebhookAsync(WalletWebhookEvent webhook, CancellationToken cancellationToken = default)
    {
        PaymentTransaction? txn = null;
        if (webhook.OrderId is not null)
            txn = await _context.PaymentTransactions.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.RazorpayOrderId == webhook.OrderId, cancellationToken);
        if (txn is null && webhook.PaymentId is not null)
            txn = await _context.PaymentTransactions.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.RazorpayPaymentId == webhook.PaymentId, cancellationToken);
        if (txn is null)
            return WalletWebhookResult.NotOurs;

        var id = txn.Id;
        switch (webhook.Event)
        {
            case "payment.authorized":
                if (txn.Status is PaymentTransactionStatus.Created or PaymentTransactionStatus.Failed)
                {
                    txn.Status = PaymentTransactionStatus.Authorized;
                    txn.PaymentMethod ??= webhook.Method;
                    await _context.SaveChangesAsync(cancellationToken);
                    return new WalletWebhookResult(true, true, "Payment authorized; waiting for capture.");
                }
                return new WalletWebhookResult(true, false, "Already past authorization.");

            case "payment.captured":
            case "order.paid":
                if (IsCredited(txn))
                    return new WalletWebhookResult(true, false, "Already credited.");

                // A signed event names the payment and its amount; without them (an order-level event) ask Razorpay.
                if (webhook.PaymentId is null || webhook.PaymentAmount is null)
                {
                    var changed = await ReconcileOneAsync(txn, cancellationToken);
                    return new WalletWebhookResult(true, changed, changed ? "Settled from Razorpay's record." : "Nothing to settle yet.");
                }

                var payment = new RazorpayPaymentInfo(
                    webhook.PaymentId, webhook.OrderId, webhook.PaymentAmount.Value, webhook.PaymentCurrency ?? "INR", webhook.PaymentStatus ?? "captured",
                    webhook.Method, webhook.PaymentAmountRefunded ?? 0, webhook.ErrorDescription);
                var credentials = await RequireCredentialsAsync(cancellationToken);
                var result = await SettlePaymentAsync(txn, payment, credentials, cancellationToken);
                return new WalletWebhookResult(true, result == Settlement.Credited, result switch
                {
                    Settlement.Credited => "Payment captured - wallet credited.",
                    Settlement.AlreadyCredited => "Already credited.",
                    Settlement.Pending => "Payment not captured yet.",
                    _ => "Payment rejected: " + (txn.FailureReason ?? "it did not match the order.")
                });

            case "payment.failed":
                if (IsCredited(txn))
                    return new WalletWebhookResult(true, false, "An earlier attempt failed; the top-up is already paid.");
                txn.Status = PaymentTransactionStatus.Failed;
                txn.FailureReason = Truncate(webhook.ErrorDescription ?? "Payment failed.", 500);
                await _context.SaveChangesAsync(cancellationToken);
                return new WalletWebhookResult(true, true, "Payment failed; nothing credited.");

            case "refund.created":
            case "refund.processed":
                var applied = await ApplyRefundAsync(txn, webhook, cancellationToken);
                return new WalletWebhookResult(true, applied, applied ? "Refund recorded and credits taken back." : "Refund already recorded.");

            default:
                _logger.LogDebug("Wallet recharge {TransactionId}: {Event} is recorded but not acted on", id, webhook.Event);
                return new WalletWebhookResult(true, false, $"{webhook.Event} is recorded but not acted on.");
        }
    }

    // ── Reconciliation ──────────────────────────────────────────────────────────────────

    public async Task<int> ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var settleAfter = now.AddMinutes(-2);       // let the checkout window finish first
        var askAgainAfter = now.AddMinutes(-10);
        var recent = now.AddDays(-7);
        var failedRecent = now.AddDays(-1);

        var due = await _context.PaymentTransactions.IgnoreQueryFilters()
            .Where(t => t.CreatedAt < settleAfter
                && (t.LastReconciledAt == null || t.LastReconciledAt < askAgainAfter)
                && ((t.Status == PaymentTransactionStatus.Created || t.Status == PaymentTransactionStatus.Authorized) && t.CreatedAt > recent
                    || t.Status == PaymentTransactionStatus.Failed && t.CreatedAt > failedRecent))
            .OrderBy(t => t.CreatedAt).Take(ReconcileBatch)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var txn in due)
        {
            try
            {
                if (await ReconcileOneAsync(txn, cancellationToken))
                    changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad row must not stop the rest; it is asked again on the next pass.
                _context.ResetChangeTracker();
                _logger.LogWarning(ex, "Wallet recharge reconciliation failed for transaction {TransactionId}", txn.Id);
            }
        }

        if (due.Count > 0)
            _logger.LogInformation("Wallet recharge reconciliation: {Checked} checked, {Changed} changed", due.Count, changed);
        return changed;
    }

    /// <summary>Asks Razorpay what became of the order. True when the transaction changed.</summary>
    private async Task<bool> ReconcileOneAsync(PaymentTransaction txn, CancellationToken cancellationToken)
    {
        var id = txn.Id;
        var tenantId = txn.TenantId;
        var before = txn.Status;
        var credentials = await TryCredentialsAsync(cancellationToken);
        if (credentials is null)
            return false;

        var listed = await _gateway.FetchOrderPaymentsAsync(credentials, txn.RazorpayOrderId, cancellationToken);
        if (!listed.Success || listed.Value is null)
        {
            _logger.LogWarning("Wallet recharge {TransactionId}: could not read the order's payments from Razorpay: {Error}", id, listed.Error);
            return false;
        }

        // Capturing beats authorizing beats failing: a retry after a failed attempt can still succeed on the same order.
        var best = listed.Value.FirstOrDefault(p => p.Status is "captured" or "refunded")
                   ?? listed.Value.FirstOrDefault(p => p.Status == "authorized")
                   ?? listed.Value.OrderByDescending(p => p.Id, StringComparer.Ordinal).FirstOrDefault(p => p.Status == "failed");

        if (best is not null)
        {
            var settled = await SettlePaymentAsync(txn, best, credentials, cancellationToken);
            return settled == Settlement.Credited || (await ReloadStatusAsync(tenantId, id, cancellationToken)) != before;
        }

        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(5, Options.AbandonAfterMinutes));
        if (txn.Status == PaymentTransactionStatus.Created && txn.CreatedAt < cutoff)
        {
            txn.Status = PaymentTransactionStatus.Expired;
            txn.FailureReason = "The checkout was not completed.";
            txn.LastReconciledAt = _clock.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        txn.LastReconciledAt = _clock.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return false;
    }

    // ── The one place a payment is checked and, if genuine, credited ─────────────────────

    private enum Settlement { Credited, AlreadyCredited, Pending, Rejected }

    /// <summary>Checks Razorpay's record of a payment against the transaction (order, amount, currency), captures it if it is only authorized, and credits
    /// the wallet once it is captured. Every path - the browser's verify call, a webhook, reconciliation - comes through here.</summary>
    private async Task<Settlement> SettlePaymentAsync(PaymentTransaction txn, RazorpayPaymentInfo payment, RazorpayCredentials credentials, CancellationToken cancellationToken)
    {
        if (IsCredited(txn))
            return txn.RazorpayPaymentId == payment.Id ? Settlement.AlreadyCredited : Settlement.Rejected;

        if (payment.OrderId != txn.RazorpayOrderId || payment.Amount != txn.AmountMinor || !string.Equals(payment.Currency, txn.Currency, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Wallet recharge {Reference}: payment {PaymentId} does not match the order (amount {Amount} vs {Expected}); nothing credited",
                txn.InternalTransactionId, payment.Id, payment.Amount, txn.AmountMinor);
            txn.Status = PaymentTransactionStatus.Failed;
            txn.FailureReason = "The payment does not match the order's amount or currency.";
            await _context.SaveChangesAsync(cancellationToken);
            return Settlement.Rejected;
        }

        if (payment.Status == "authorized")
        {
            var captured = await _gateway.CapturePaymentAsync(credentials, payment.Id, txn.AmountMinor, txn.Currency, cancellationToken);
            if (!captured.Success || captured.Value is null)
            {
                txn.Status = PaymentTransactionStatus.Authorized;
                txn.PaymentMethod ??= payment.Method;
                await _context.SaveChangesAsync(cancellationToken);
                return Settlement.Pending;
            }
            payment = captured.Value;
        }

        if (payment.Status is "captured" or "refunded")
        {
            await CompleteAsync(txn, payment.Id, payment.Method, cancellationToken);
            return Settlement.Credited;
        }

        if (payment.Status == "failed")
        {
            txn.Status = PaymentTransactionStatus.Failed;
            txn.FailureReason = Truncate(payment.ErrorDescription ?? "Payment failed.", 500);
            await _context.SaveChangesAsync(cancellationToken);
            return Settlement.Rejected;
        }

        txn.PaymentMethod ??= payment.Method;
        await _context.SaveChangesAsync(cancellationToken);
        return Settlement.Pending;
    }

    /// <summary>
    /// Marks the transaction credited, records the payment and writes the wallet credit in ONE database transaction, so either all three happen or none.
    /// Exactly once: the unique index on a gateway payment id and the ledger's per-transaction operation key both refuse a second credit, so two
    /// deliveries racing (webhook and browser, or a redelivered webhook) can only ever add the credits once - the loser finds it already done.
    /// </summary>
    private async Task CompleteAsync(PaymentTransaction txn, string paymentId, string? method, CancellationToken cancellationToken)
    {
        var id = txn.Id;
        var now = _clock.UtcNow;

        await using var tx = await _context.BeginTransactionAsync(cancellationToken);
        try
        {
            var payment = new Payment
            {
                TenantId = txn.TenantId,
                Kind = PaymentKind.WalletTopUp,
                PlanName = "Wallet top-up",
                AmountCents = txn.AmountUsdCents,
                CurrencyCode = txn.Currency,
                CurrencySymbol = "₹",
                LocalAmount = txn.Amount,
                CountryCode = txn.CountryCode,
                StateCode = txn.StateCode,
                TaxLocal = txn.TaxAmount,
                TaxLinesJson = txn.TaxLinesJson,
                TotalLocal = txn.TotalAmount,
                AmountInr = txn.TotalInr,
                FxRateToInr = txn.FxRateToInr,
                Provider = "Razorpay",
                ProviderPaymentId = paymentId,
                PaymentMethod = method ?? txn.PaymentMethod,
                Status = PaymentStatus.Succeeded,
                PaidAtUtc = now
            };
            _context.Payments.Add(payment);

            txn.Status = PaymentTransactionStatus.Credited;
            txn.RazorpayPaymentId = paymentId;
            txn.PaymentMethod = method ?? txn.PaymentMethod;
            txn.CapturedAt ??= now;
            txn.CreditedAt = now;
            txn.PaymentId = payment.Id;
            txn.FailureReason = null;
            await _context.SaveChangesAsync(cancellationToken);

            await _ledger.GrantTopUpAsync(txn.TenantId, txn.Credits, txn.Id, payment.Id, $"{Source} wallet top-up {paymentId}", cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("Wallet recharge {Reference} credited: tenant {TenantId} +{Credits} credits (payment {PaymentId})", txn.InternalTransactionId, txn.TenantId, txn.Credits, paymentId);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _context.ResetChangeTracker();

            // Lost a race: another delivery of the same payment got there first. Anything else is a real failure.
            var done = await _context.PaymentTransactions.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(t => t.Id == id && t.Status == PaymentTransactionStatus.Credited, cancellationToken);
            if (!done)
                throw;
            _logger.LogInformation("Wallet recharge {TransactionId}: payment {PaymentId} was credited by a parallel delivery", id, paymentId);
        }
    }

    private async Task<bool> ApplyRefundAsync(PaymentTransaction txn, WalletWebhookEvent webhook, CancellationToken cancellationToken)
    {
        if (!IsCredited(txn) || txn.PaymentId is null)
            return false;

        var refunded = Math.Min(txn.AmountMinor, Math.Max(txn.RefundedMinor, webhook.PaymentAmountRefunded ?? 0));
        if (refunded == txn.RefundedMinor && webhook.PaymentAmountRefunded is null && webhook.RefundAmount is { } single)
            refunded = Math.Min(txn.AmountMinor, txn.RefundedMinor + single);

        var delta = refunded - txn.RefundedMinor;
        if (delta <= 0)
            return false;

        // The refunded share of the credits is taken back by a compensating ledger entry - history is never edited.
        var clawBack = decimal.Round(txn.Credits * delta / txn.AmountMinor, 2, MidpointRounding.AwayFromZero);

        await using var tx = await _context.BeginTransactionAsync(cancellationToken);
        await _ledger.ClawBackCreditsAsync(txn.TenantId, txn.PaymentId.Value, clawBack, $"{Source} refund {webhook.RefundId ?? string.Empty}".Trim(), null, cancellationToken);
        txn.RefundedMinor = refunded;
        txn.Status = refunded >= txn.AmountMinor ? PaymentTransactionStatus.Refunded : PaymentTransactionStatus.PartiallyRefunded;
        await _context.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("Wallet recharge {TransactionId}: refund of {Delta} paise recorded, {Credits} credits taken back", txn.Id, delta, clawBack);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private static bool IsCredited(PaymentTransaction t) =>
        t.Status is PaymentTransactionStatus.Credited or PaymentTransactionStatus.PartiallyRefunded or PaymentTransactionStatus.Refunded;

    private async Task<PaymentTransactionStatus> ReloadStatusAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await _context.PaymentTransactions.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == id && t.TenantId == tenantId).Select(t => t.Status).FirstAsync(cancellationToken);

    private Task<PaymentTransaction?> OwnedAsync(Guid tenantId, System.Linq.Expressions.Expression<Func<PaymentTransaction, bool>> where, CancellationToken cancellationToken) =>
        _context.PaymentTransactions.IgnoreQueryFilters().Where(t => t.TenantId == tenantId).Where(where).FirstOrDefaultAsync(cancellationToken);

    private async Task<decimal> BalanceAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await _wallet.GetSummaryAsync(tenantId, cancellationToken)).Balance;

    private async Task<Domain.Entities.Tenancy.Tenant> RequireTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _context.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Tenancy.Tenant), tenantId);

    /// <summary>The server's own price for a top-up: validated against the configured limits, taxed, and converted to credits.</summary>
    private (PriceQuote Quote, decimal Credits) Price(Domain.Entities.Tenancy.Tenant tenant, decimal amount)
    {
        var o = Options;
        if (amount < o.MinRechargeInr || amount > o.MaxRechargeInr)
            throw Invalid(nameof(amount), $"Enter an amount between {o.MinRechargeInr.ToString("#,##0", CultureInfo.InvariantCulture)} and {o.MaxRechargeInr.ToString("#,##0", CultureInfo.InvariantCulture)}.");
        if (decimal.Round(amount, 2) != amount)
            throw Invalid(nameof(amount), "Use at most two decimal places.");
        if (o.CreditsPerRupee <= 0)
            throw new ConflictException("Wallet recharge is not configured: the credits-per-rupee rate must be above zero.");

        var quote = _pricing.Quote(amount, tenant.CountryCode, tenant.StateCode)
            ?? throw new ConflictException("Wallet recharge is not available for your account.");
        if (quote.CurrencyCode != "INR")
            throw new ConflictException("Wallet recharge through Razorpay is available for accounts billed in INR.");

        var credits = decimal.Round(amount * o.CreditsPerRupee, 2, MidpointRounding.AwayFromZero);
        if (credits <= 0)
            throw Invalid(nameof(amount), "That amount buys no credits.");
        return (quote, credits);
    }

    private async Task<RazorpayCredentials> RequireCredentialsAsync(CancellationToken cancellationToken) =>
        await TryCredentialsAsync(cancellationToken) ?? throw new ConflictException("Online payment is not set up yet. Please contact support.");

    private async Task<RazorpayCredentials?> TryCredentialsAsync(CancellationToken cancellationToken)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        var keyId = stored.TryGetValue(RazorpayService.KeyIdKey, out var k) ? k?.Trim() ?? string.Empty : string.Empty;
        var secret = stored.TryGetValue(RazorpayService.KeySecretKey, out var s) ? s?.Trim() ?? string.Empty : string.Empty;
        return keyId.Length == 0 || secret.Length == 0 ? null : new RazorpayCredentials(keyId, secret);
    }

    private async Task<List<RechargeTransactionDto>> ToDtosAsync(Guid tenantId, IReadOnlyList<PaymentTransaction> rows, CancellationToken cancellationToken)
    {
        // The balance a credit left behind is read off the immutable ledger entry it wrote.
        var keys = rows.Where(IsCredited).Select(r => $"topup:{r.Id}").ToList();
        var entries = keys.Count == 0
            ? new Dictionary<string, decimal>()
            : (await _context.QuotaLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.TenantId == tenantId && e.EntryType == QuotaEntryType.Purchase && keys.Contains(e.OperationKey))
                    .Select(e => new { e.OperationKey, e.BalanceAfter }).ToListAsync(cancellationToken))
                .ToDictionary(e => e.OperationKey, e => e.BalanceAfter);

        return rows.Select(r =>
        {
            decimal? after = entries.TryGetValue($"topup:{r.Id}", out var b) ? b : null;
            return new RechargeTransactionDto(
                r.Id, r.InternalTransactionId, r.RazorpayOrderId, r.RazorpayPaymentId, r.Amount, r.TaxAmount, r.TotalAmount, r.Currency, r.Credits,
                r.Status.ToString(), r.PaymentMethod, r.FailureReason, r.Mode, after is null ? null : after - r.Credits, after, r.CreatedAt, r.CapturedAt, r.CreditedAt);
        }).ToList();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
