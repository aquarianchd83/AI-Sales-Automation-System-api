using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing.Refunds;

/// <summary>
/// See <see cref="IRefundService"/>. The order of events on a request is what keeps units and money in step:
/// the request is saved, then its units are held out of the wallet; approval pays the gateway FIRST and only
/// then releases the unapproved share and books the refund, so a gateway failure leaves everything held and
/// changes nothing else. Nothing here is automatic - a person approves every refund.
/// </summary>
public class RefundService : IRefundService
{
    /// <summary>The tax lines of a payment, each scaled to the share of it being refunded.</summary>
    private static string? ScaleTaxLines(string? json, decimal share)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var lines = System.Text.Json.JsonSerializer.Deserialize<List<TaxLineDto>>(json) ?? new List<TaxLineDto>();
            return System.Text.Json.JsonSerializer.Serialize(lines.Select(l => l with { Amount = -Math.Round(l.Amount * share, 2, MidpointRounding.AwayFromZero) }));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static readonly RefundStatus[] Open = { RefundStatus.Requested, RefundStatus.Failed };

    private readonly IApplicationDbContext _context;
    private readonly IQuotaLedgerService _ledger;
    private readonly IRefundGateway _gateway;
    private readonly ITenantNotifier _notifier;
    private readonly IDateTimeProvider _dateTime;
    private readonly RefundPolicyOptions _policy;

    public RefundService(
        IApplicationDbContext context,
        IQuotaLedgerService ledger,
        IRefundGateway gateway,
        ITenantNotifier notifier,
        IDateTimeProvider dateTime,
        IOptionsSnapshot<RefundPolicyOptions> policy)
    {
        _context = context;
        _ledger = ledger;
        _gateway = gateway;
        _notifier = notifier;
        _dateTime = dateTime;
        _policy = policy.Value;
    }

    public async Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await _context.Tenants.Where(t => t.Id == tenantId).Select(t => t.RefundRequestsEnabled).FirstOrDefaultAsync(cancellationToken);

    public async Task SetEnabledAsync(Guid tenantId, bool enabled, CancellationToken cancellationToken = default)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Tenancy.Tenant), tenantId);
        tenant.RefundRequestsEnabled = enabled;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<RefundEligibilityDto> GetEligibilityAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken = default)
    {
        await RequireEnabledAsync(tenantId, cancellationToken);
        return await EvaluateAsync(await LoadPaymentAsync(tenantId, paymentId, cancellationToken), ignorePolicyLimits: false, cancellationToken);
    }

    public async Task<RefundRequestDto> RequestAsync(Guid tenantId, Guid paymentId, string reason, Guid requestedByUserId, CancellationToken cancellationToken = default)
    {
        await RequireEnabledAsync(tenantId, cancellationToken);
        RequireText(reason, "reason", "Tell us why you're asking for a refund.");

        var payment = await LoadPaymentAsync(tenantId, paymentId, cancellationToken);
        var eligibility = await EvaluateAsync(payment, ignorePolicyLimits: false, cancellationToken);
        if (!eligibility.Eligible)
            throw new ConflictException(eligibility.Reason ?? "This payment can't be refunded.");

        var request = await CreateRequestAsync(payment, eligibility, reason.Trim(), requestedByUserId, cancellationToken);
        return (await ToDtosAsync(new[] { request }, cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<RefundRequestDto>> ListForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var requests = await _context.RefundRequests.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ToDtosAsync(requests, cancellationToken);
    }

    public async Task<RefundRequestDto> CancelAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await LoadRequestAsync(requestId, cancellationToken);
        if (request.TenantId != tenantId)
            throw new NotFoundException(nameof(RefundRequest), requestId);
        if (request.Status != RefundStatus.Requested)
            throw new ConflictException("Only a request that is still waiting can be withdrawn.");

        await _ledger.ReleaseRefundHoldAsync(request.TenantId, request.Id, keepFraction: 0m, cancellationToken);
        request.Status = RefundStatus.Cancelled;
        await _context.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(new[] { request }, cancellationToken)).Single();
    }

    public async Task<PagedResult<RefundRequestDto>> ListAsync(PlatformRefundQuery query, CancellationToken cancellationToken = default)
    {
        var requests = _context.RefundRequests.IgnoreQueryFilters().AsQueryable();
        if (query.Status is { } status)
            requests = requests.Where(r => r.Status == status);
        if (query.TenantId is { } tenantId)
            requests = requests.Where(r => r.TenantId == tenantId);

        var total = await requests.CountAsync(cancellationToken);
        var page = await requests
            .OrderByDescending(r => r.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<RefundRequestDto>(await ToDtosAsync(page, cancellationToken), total, query.Page, query.PageSize);
    }

    public async Task<RefundRequestDto> ApproveAsync(Guid requestId, ReviewRefundRequest review, Guid reviewerUserId, CancellationToken cancellationToken = default)
    {
        var request = await LoadRequestAsync(requestId, cancellationToken);
        if (!Open.Contains(request.Status))
            throw new ConflictException($"This request is already {request.Status}.");

        var alreadyRefunded = await _context.RefundRequests.IgnoreQueryFilters()
            .AnyAsync(r => r.PaymentId == request.PaymentId && r.Id != request.Id && r.Status == RefundStatus.Refunded, cancellationToken);
        if (alreadyRefunded)
            throw new ConflictException("This payment has already been refunded.");

        var approvedCents = review.ApprovedAmountCents ?? request.EligibleAmountCents;
        if (approvedCents <= 0 || approvedCents > request.EligibleAmountCents)
            throw new ConflictException($"The approved amount must be between 1 and {request.EligibleAmountCents} cents.");

        var payment = await LoadPaymentAsync(request.TenantId, request.PaymentId, cancellationToken);
        var fraction = (decimal)approvedCents / request.EligibleAmountCents;
        var refundLocal = Math.Round(request.EligibleLocalAmount * fraction, 2);

        // Idempotent: makes sure the units are out of the wallet even if the original hold never completed.
        await _ledger.HoldForRefundAsync(request.TenantId, request.PaymentId, request.Id, cancellationToken);

        var gateway = await _gateway.RefundAsync(payment, approvedCents, refundLocal, $"refund:{request.Id}", cancellationToken);
        var now = _dateTime.UtcNow;
        request.ReviewedByUserId = reviewerUserId;
        request.ReviewedAtUtc = now;
        request.ReviewNote = string.IsNullOrWhiteSpace(review.Note) ? null : review.Note.Trim();

        if (!gateway.Success)
        {
            // Everything stays held; an operator retries or rejects. Nothing else has changed.
            request.Status = RefundStatus.Failed;
            request.FailureReason = gateway.Error ?? "The payment gateway did not complete the refund.";
            await _context.SaveChangesAsync(cancellationToken);
            return (await ToDtosAsync(new[] { request }, cancellationToken)).Single();
        }

        // Paid out. What was held beyond the approved share (a partial approval) goes back to the wallet.
        await _ledger.ReleaseRefundHoldAsync(request.TenantId, request.Id, fraction, cancellationToken);

        var share = payment.LocalAmount > 0 ? refundLocal / payment.LocalAmount : 0m;
        var refundTax = Math.Round(payment.TaxLocal * share, 2, MidpointRounding.AwayFromZero);

        var refund = new Payment
        {
            TenantId = payment.TenantId,
            Kind = PaymentKind.Refund,
            PlanId = payment.PlanId,
            CreditPackId = payment.CreditPackId,
            RefundOfPaymentId = payment.Id,
            PlanName = $"Refund: {payment.PlanName}",
            AmountCents = -approvedCents,
            CurrencyCode = payment.CurrencyCode,
            CurrencySymbol = payment.CurrencySymbol,
            LocalAmount = -refundLocal,
            // The refund returns its share of the tax as well, in the original currency and at the original rate.
            CountryCode = payment.CountryCode,
            StateCode = payment.StateCode,
            TaxLocal = -refundTax,
            TaxLinesJson = ScaleTaxLines(payment.TaxLinesJson, share),
            TotalLocal = -(refundLocal + refundTax),
            FxRateToInr = payment.FxRateToInr,
            AmountInr = -Math.Round((refundLocal + refundTax) * payment.FxRateToInr, 2, MidpointRounding.AwayFromZero),
            Provider = _gateway.Name,
            PaidAtUtc = now
        };
        _context.Payments.Add(refund);

        request.Status = RefundStatus.Refunded;
        request.FailureReason = null;
        request.RefundedAmountCents = approvedCents;
        request.RefundedLocalAmount = refundLocal;
        request.RefundPaymentId = refund.Id;
        request.ProviderReference = gateway.Reference;

        if (payment.Kind == PaymentKind.Subscription)
        {
            // A refunded subscription ends the plan: there is nothing left to renew.
            var subscription = await _context.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == payment.TenantId, cancellationToken);
            if (subscription is not null && subscription.PlanId == payment.PlanId)
            {
                subscription.Status = SubscriptionStatus.Canceled;
                subscription.PlanId = null;
                subscription.CurrentPeriodStartUtc = null;
                subscription.CurrentPeriodEndUtc = null;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyAsync(new TenantNotificationRequest(
            request.TenantId, TenantNotificationKind.RefundApproved, null, request.Id.ToString(),
            "Your refund was approved",
            $"We've refunded {payment.CurrencySymbol}{refundLocal + refundTax:0.00} (including tax) for {payment.PlanName}. It goes back to your original payment method.",
            AlsoWhatsApp: false), cancellationToken);

        return (await ToDtosAsync(new[] { request }, cancellationToken)).Single();
    }

    public async Task<RefundRequestDto> RejectAsync(Guid requestId, string note, Guid reviewerUserId, CancellationToken cancellationToken = default)
    {
        RequireText(note, "note", "Give the reason for rejecting - the tenant will see it.");

        var request = await LoadRequestAsync(requestId, cancellationToken);
        if (!Open.Contains(request.Status))
            throw new ConflictException($"This request is already {request.Status}.");

        await _ledger.ReleaseRefundHoldAsync(request.TenantId, request.Id, keepFraction: 0m, cancellationToken);

        request.Status = RefundStatus.Rejected;
        request.ReviewedByUserId = reviewerUserId;
        request.ReviewedAtUtc = _dateTime.UtcNow;
        request.ReviewNote = note.Trim();
        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyAsync(new TenantNotificationRequest(
            request.TenantId, TenantNotificationKind.RefundRejected, null, request.Id.ToString(),
            "Your refund request was declined",
            $"We couldn't approve your refund request: {request.ReviewNote}",
            AlsoWhatsApp: false), cancellationToken);

        return (await ToDtosAsync(new[] { request }, cancellationToken)).Single();
    }

    public async Task<RefundRequestDto> RefundDirectAsync(Guid tenantId, Guid paymentId, string reason, Guid reviewerUserId, CancellationToken cancellationToken = default)
    {
        RequireText(reason, "reason", "A refund needs a reason.");

        var payment = await LoadPaymentAsync(tenantId, paymentId, cancellationToken);

        // An operator may refund outside the tenant-facing window and usage limits (a goodwill or error refund),
        // but never money that isn't there: nothing unspent, or already refunded, is still refused.
        var eligibility = await EvaluateAsync(payment, ignorePolicyLimits: true, cancellationToken);
        if (!eligibility.Eligible)
            throw new ConflictException(eligibility.Reason ?? "This payment can't be refunded.");

        var request = await CreateRequestAsync(payment, eligibility, reason.Trim(), reviewerUserId, cancellationToken);
        return await ApproveAsync(request.Id, new ReviewRefundRequest(reason), reviewerUserId, cancellationToken);
    }

    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _dateTime.UtcNow.AddDays(-_policy.RequestExpiryDays);
        var stale = await _context.RefundRequests.IgnoreQueryFilters()
            .Where(r => r.Status == RefundStatus.Requested && r.CreatedAt <= cutoff)
            .ToListAsync(cancellationToken);

        foreach (var request in stale)
        {
            await _ledger.ReleaseRefundHoldAsync(request.TenantId, request.Id, keepFraction: 0m, cancellationToken);
            request.Status = RefundStatus.Expired;
            request.ReviewedAtUtc = _dateTime.UtcNow;
            request.ReviewNote = "Closed automatically - no decision was made in time.";
            await _context.SaveChangesAsync(cancellationToken);

            await _notifier.NotifyAsync(new TenantNotificationRequest(
                request.TenantId, TenantNotificationKind.RefundExpired, null, request.Id.ToString(),
                "Your refund request was closed",
                "Your refund request wasn't answered in time and has been closed, and your credits are available again. You can ask again if you still need it.",
                AlsoWhatsApp: false), cancellationToken);
        }

        return stale.Count;
    }

    private async Task<RefundRequest> CreateRequestAsync(Payment payment, RefundEligibilityDto eligibility, string reason, Guid requestedBy, CancellationToken cancellationToken)
    {
        var request = new RefundRequest
        {
            TenantId = payment.TenantId,
            PaymentId = payment.Id,
            Reason = reason,
            RequestedByUserId = requestedBy,
            EligibleAmountCents = eligibility.AmountCents,
            EligibleLocalAmount = eligibility.LocalAmount
        };
        _context.RefundRequests.Add(request);
        await _context.SaveChangesAsync(cancellationToken);

        // The units are taken out of the wallet now, so they can't be spent while the request waits.
        await _ledger.HoldForRefundAsync(payment.TenantId, payment.Id, request.Id, cancellationToken);
        return request;
    }

    /// <summary>The rules, in one place. <paramref name="ignorePolicyLimits"/> is the operator's discretionary
    /// path: the time window and the usage cap are skipped, everything about the money itself is not.</summary>
    private async Task<RefundEligibilityDto> EvaluateAsync(Payment payment, bool ignorePolicyLimits, CancellationToken cancellationToken)
    {
        RefundEligibilityDto No(string reason, DateTime? windowEnds = null) =>
            new(payment.Id, false, reason, 0, 0m, payment.CurrencyCode, payment.CurrencySymbol, windowEnds);

        if (payment.Kind == PaymentKind.Refund || payment.AmountCents <= 0)
            return No("A refund can't be refunded.");

        var alreadyAsked = await _context.RefundRequests.IgnoreQueryFilters()
            .AnyAsync(r => r.PaymentId == payment.Id && (r.Status == RefundStatus.Requested || r.Status == RefundStatus.Refunded || r.Status == RefundStatus.Failed), cancellationToken);
        if (alreadyAsked)
            return No("A refund has already been requested or issued for this payment.");

        var now = _dateTime.UtcNow;
        var windowDays = payment.Kind == PaymentKind.CreditPack ? _policy.CreditWindowDays : _policy.SubscriptionWindowDays;
        var windowEnds = payment.PaidAtUtc.AddDays(windowDays);
        if (!ignorePolicyLimits && now > windowEnds)
            return No($"The {windowDays}-day refund window for this payment has closed.", windowEnds);

        var usage = await _ledger.GetPaymentGrantUsageAsync(payment.TenantId, payment.Id, cancellationToken);
        if (usage.Count == 0)
            return No("Nothing from this payment is left to refund.", windowEnds);

        decimal fraction;
        if (payment.Kind == PaymentKind.CreditPack)
        {
            var granted = usage.Sum(u => u.UnitsGranted);
            var remaining = usage.Sum(u => u.UnitsRemaining);
            if (granted <= 0 || remaining <= 0)
                return No("These credits have all been used or have expired, so there is nothing left to refund.", windowEnds);

            // Only what is still unspent is refundable, at the price it was bought for.
            fraction = remaining / granted;
        }
        else
        {
            if (!ignorePolicyLimits)
            {
                foreach (var u in usage.Where(u => u.UnitsGranted > 0))
                {
                    var used = (u.UnitsGranted - u.UnitsRemaining) / u.UnitsGranted;
                    if (used >= _policy.SubscriptionMaxUsageFraction)
                        return No($"You've used {used:P0} of your {u.QuotaType} quota. Subscriptions can only be refunded below {_policy.SubscriptionMaxUsageFraction:P0} usage.", windowEnds);
                }
            }

            // Prorated: every whole day of service already given is not refundable.
            var elapsedDays = Math.Max(0, (int)Math.Floor((now - payment.PaidAtUtc).TotalDays));
            fraction = Math.Clamp((decimal)(_policy.SubscriptionPeriodDays - elapsedDays) / _policy.SubscriptionPeriodDays, 0m, 1m);
        }

        var amountCents = (int)Math.Floor(payment.AmountCents * fraction);
        if (amountCents <= 0)
            return No("The refundable amount works out to zero.", windowEnds);

        return new RefundEligibilityDto(
            payment.Id, true, null, amountCents, Math.Round(payment.LocalAmount * fraction, 2),
            payment.CurrencyCode, payment.CurrencySymbol, windowEnds);
    }

    private async Task RequireEnabledAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(tenantId, cancellationToken))
            throw new FeatureDisabledException("Refund requests aren't available on your account. Contact support if you need one.");
    }

    private static void RequireText(string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 1000)
            throw new FluentValidation.ValidationException(new[] { new ValidationFailure(field, message) });
    }

    private async Task<Payment> LoadPaymentAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken) =>
        await _context.Payments.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == paymentId && p.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Payment), paymentId);

    private async Task<RefundRequest> LoadRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        await _context.RefundRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new NotFoundException(nameof(RefundRequest), requestId);

    private async Task<IReadOnlyList<RefundRequestDto>> ToDtosAsync(IReadOnlyCollection<RefundRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return Array.Empty<RefundRequestDto>();

        var tenantIds = requests.Select(r => r.TenantId).Distinct().ToList();
        var paymentIds = requests.Select(r => r.PaymentId).Distinct().ToList();

        var tenants = await _context.Tenants.Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var payments = await _context.Payments.IgnoreQueryFilters().Where(p => paymentIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        return requests.Select(r =>
        {
            var payment = payments.GetValueOrDefault(r.PaymentId);
            return new RefundRequestDto(
                r.Id, r.TenantId, tenants.GetValueOrDefault(r.TenantId, string.Empty), r.PaymentId,
                payment?.PlanName ?? string.Empty, r.Status, r.Reason,
                r.EligibleAmountCents, r.EligibleLocalAmount,
                payment?.CurrencyCode ?? "USD", payment?.CurrencySymbol ?? "$",
                r.RefundedAmountCents, r.RefundedLocalAmount, r.ReviewNote, r.FailureReason, r.CreatedAt, r.ReviewedAtUtc);
        }).ToList();
    }
}
