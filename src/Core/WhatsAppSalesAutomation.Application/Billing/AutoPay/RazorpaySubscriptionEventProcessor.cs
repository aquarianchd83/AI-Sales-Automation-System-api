using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

public enum RazorpayEventOutcome
{
    /// <summary>Applied to one of our mandates.</summary>
    Completed,

    /// <summary>Genuine, but not about a mandate of ours.</summary>
    Ignored,

    /// <summary>Applying it failed. It stays in the table as Failed and is tried again.</summary>
    Failed,

    /// <summary>It was applied (or ignored) before - a second run of the same job, or the retry pass arriving late.</summary>
    AlreadyDone,

    NotFound
}

/// <summary>Applies one stored Razorpay subscription webhook. Run by the background job, never inside the webhook request.</summary>
public interface IRazorpaySubscriptionEventProcessor
{
    /// <param name="webhookEventRowId">The <c>RazorpayWebhookEvent</c> row's id.</param>
    /// <returns>Never throws for a problem with the event itself: that is recorded on the row and reported as <see cref="RazorpayEventOutcome.Failed"/>.</returns>
    Task<RazorpayEventOutcome> ProcessAsync(Guid webhookEventRowId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a stored subscription event into a change to our mandate, and passes any payment it carries on to
/// <see cref="IAutoPayEntitlements"/>.
///
/// Razorpay redelivers events and does not send them in order, so nothing here assumes either:
/// <list type="bullet">
/// <item>A row already Completed or Ignored is left alone - processing the same event twice does nothing the second time.</item>
/// <item>Every subscription event carries the whole subscription as it stood when the event was raised. One that is newer than anything
/// applied so far is taken as it is. One that is older, or raised in the same second as the last one (so its place in the order cannot be
/// known), is not trusted: Razorpay is asked directly where the subscription stands now, and that answer is used.</item>
/// <item>A mandate that has ended is never brought back, whatever arrives late.</item>
/// <item>A payment is passed on whatever the order - a debit that happened is owed its record even when its event turns up after a
/// cancellation - and the entitlements side is what guarantees it is counted once.</item>
/// </list>
/// </summary>
public class RazorpaySubscriptionEventProcessor : IRazorpaySubscriptionEventProcessor
{
    private const int MaxErrorLength = 1000;
    private const int MaxNoteLength = 500;

    private readonly IApplicationDbContext _context;
    private readonly IAppSettingsStore _store;
    private readonly IRazorpayGateway _gateway;
    private readonly IAutoPayEntitlements _entitlements;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<RazorpaySubscriptionEventProcessor> _logger;

    public RazorpaySubscriptionEventProcessor(
        IApplicationDbContext context,
        IAppSettingsStore store,
        IRazorpayGateway gateway,
        IAutoPayEntitlements entitlements,
        IDateTimeProvider clock,
        ILogger<RazorpaySubscriptionEventProcessor> logger)
    {
        _context = context;
        _store = store;
        _gateway = gateway;
        _entitlements = entitlements;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RazorpayEventOutcome> ProcessAsync(Guid webhookEventRowId, CancellationToken cancellationToken = default)
    {
        var record = await _context.RazorpayWebhookEvents.FirstOrDefaultAsync(e => e.Id == webhookEventRowId, cancellationToken);
        if (record is null)
        {
            _logger.LogWarning("Razorpay webhook event row {Id} does not exist", webhookEventRowId);
            return RazorpayEventOutcome.NotFound;
        }

        if (record.ProcessingStatus is RazorpayWebhookProcessingStatus.Completed or RazorpayWebhookProcessingStatus.Ignored)
            return RazorpayEventOutcome.AlreadyDone;

        try
        {
            var (applied, note) = await ApplyAsync(record, cancellationToken);

            record.ProcessingStatus = applied ? RazorpayWebhookProcessingStatus.Completed : RazorpayWebhookProcessingStatus.Ignored;
            record.Applied = applied;
            record.Note = Truncate(note, MaxNoteLength);
            record.ErrorMessage = null;
            record.ProcessedAtUtc = _clock.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Razorpay webhook {Event} ({EventId}) for {SubscriptionId}: {Note}", record.Event, record.EventId, record.SubscriptionId, note);
            return applied ? RazorpayEventOutcome.Completed : RazorpayEventOutcome.Ignored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordFailureAsync(webhookEventRowId, ex, cancellationToken);
        }
    }

    private async Task<(bool Applied, string Note)> ApplyAsync(RazorpayWebhookEvent record, CancellationToken cancellationToken)
    {
        var hook = RazorpaySubscriptionWebhook.Parse(record.Payload);
        if (hook is null)
            return (false, "The stored body could not be read.");
        if (!hook.Event.StartsWith(RazorpayService.SubscriptionEventPrefix, StringComparison.Ordinal) || hook.Subscription is null)
            return (false, "Not a subscription event.");

        var mandate = await _context.RazorpaySubscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.RazorpaySubscriptionId == hook.Subscription.Id, cancellationToken);
        if (mandate is null)
        {
            // Ours by its notes, yet no row: the event overtook the checkout's own save, or the row is missing. Either way it must not be
            // dropped - failing keeps it in the table to be tried again, and visible if it never resolves.
            if (hook.CreatedByThisPlatform)
                throw new InvalidOperationException($"Subscription {hook.Subscription.Id} was created by this platform but has no mandate record yet.");
            return (false, "Not a subscription this platform created.");
        }

        var previous = mandate.Status;
        var eventAt = hook.CreatedAtUtc ?? record.EventCreatedAtUtc ?? record.CreatedAt;
        var how = await ApplyStateAsync(mandate, hook, eventAt, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        // The mandate's state is saved before anything is granted for it; if what follows fails, the event is retried and this is repeated
        // harmlessly.
        if (hook.Payment is { } payment)
        {
            if (hook.Event == "subscription.charged" && payment.Status == "captured")
                await _entitlements.ChargeSucceededAsync(mandate, payment, cancellationToken);
            else if (payment.Status == "failed")
                await _entitlements.ChargeFailedAsync(mandate, payment, cancellationToken);
        }
        else if (hook.Event == "subscription.charged")
        {
            // A charge with no payment to record must not pass as handled.
            throw new InvalidOperationException($"subscription.charged for {mandate.RazorpaySubscriptionId} carried no payment.");
        }

        if (mandate.Status != previous)
            await _entitlements.MandateChangedAsync(mandate, previous, cancellationToken);

        return (true, $"{hook.Event}: {previous} -> {mandate.Status} ({how}).");
    }

    /// <summary>Brings the mandate row up to date from the event, or from Razorpay itself when the event cannot be trusted to be the latest.</summary>
    private async Task<string> ApplyStateAsync(RazorpaySubscription mandate, RazorpaySubscriptionWebhook hook, DateTime eventAt, CancellationToken cancellationToken)
    {
        if (RazorpayMandateState.IsEnded(mandate.Status))
            return "the mandate has ended; its state was left alone";

        var now = _clock.UtcNow;

        if (mandate.LastEventAtUtc is null || eventAt > mandate.LastEventAtUtc)
        {
            Apply(mandate, hook.Subscription!, now);
            mandate.LastEventAtUtc = eventAt;
            mandate.LastEvent = Truncate(hook.Event, 60);
            return "taken from the event";
        }

        // Older than, or as old as, what has already been applied: its place in the order is unknown, so Razorpay's own record decides.
        var credentials = (await AutoPaySettings.ReadAsync(_store, cancellationToken)).Credentials
            ?? throw new InvalidOperationException("Razorpay is not set up, so an out-of-order event could not be checked against it.");
        if (credentials.Mode != mandate.Mode)
            return $"out of order, and the {mandate.Mode} mandate cannot be checked with {credentials.Mode} keys; its state was left alone";

        var fetched = await _gateway.FetchSubscriptionAsync(credentials, mandate.RazorpaySubscriptionId, cancellationToken);
        if (!fetched.Success || fetched.Value is null)
            throw new InvalidOperationException($"An out-of-order event could not be checked against Razorpay: {fetched.Error}");

        Apply(mandate, fetched.Value, now);
        return "out of order; taken from Razorpay's own record";
    }

    private void Apply(RazorpaySubscription mandate, RazorpaySubscriptionInfo info, DateTime now)
    {
        if (!RazorpayMandateState.Apply(mandate, info, now))
            _logger.LogWarning("Razorpay subscription {SubscriptionId} reported an unknown status '{Status}'; kept {Current}", info.Id, info.Status, mandate.Status);
    }

    private async Task<RazorpayEventOutcome> RecordFailureAsync(Guid webhookEventRowId, Exception exception, CancellationToken cancellationToken)
    {
        // Whatever was half-changed in memory is dropped; only the failure itself is written.
        _context.ResetChangeTracker();
        var record = await _context.RazorpayWebhookEvents.FirstOrDefaultAsync(e => e.Id == webhookEventRowId, cancellationToken);
        if (record is null)
            return RazorpayEventOutcome.NotFound;

        record.ProcessingStatus = RazorpayWebhookProcessingStatus.Failed;
        record.RetryCount++;
        record.ErrorMessage = Truncate(exception.Message, MaxErrorLength);
        record.ProcessedAtUtc = _clock.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogError(exception,
            "Razorpay webhook {Event} ({EventId}) for {SubscriptionId} could not be applied (attempt {Attempt})",
            record.Event, record.EventId, record.SubscriptionId, record.RetryCount);
        return RazorpayEventOutcome.Failed;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
