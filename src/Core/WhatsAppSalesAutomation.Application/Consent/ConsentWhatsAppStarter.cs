using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// Starts the WhatsApp conversation for a Granted consent - the campaign's first message - through the existing campaign pipeline, so every
/// sending rule (connection, quota, daily limit, template, opt-out) still applies. Runs only after the consent has been saved, and at most
/// once per consent: the start is claimed on the invitation row before anything is sent, and a failure leaves the consent as it was.
///
/// A message the provider rejected is retried by the campaign pipeline's own retry job (it owns the message row, its backoff and its attempt
/// budget), never by sending a second one from here. Starting again therefore only ever does something when no message exists yet; otherwise it
/// just brings this record in line with what the pipeline did.
/// </summary>
public interface IConsentWhatsAppStarter
{
    /// <summary>
    /// Claims and performs the start. Returns false when nothing was sent (not granted, already started, a message already exists, another
    /// worker holds the claim, or the attempt budget is spent). A provider failure is recorded on the invitation, never raised.
    /// </summary>
    Task<bool> StartAsync(ConsentInvitation invitation, CancellationToken cancellationToken = default);
}

public class ConsentWhatsAppStarter : IConsentWhatsAppStarter
{
    private readonly IApplicationDbContext _context;
    private readonly ICampaignSendService _campaignSend;
    private readonly IDateTimeProvider _dateTime;
    private readonly ConsentPageOptions _options;
    private readonly ILogger<ConsentWhatsAppStarter> _logger;

    public ConsentWhatsAppStarter(
        IApplicationDbContext context, ICampaignSendService campaignSend, IDateTimeProvider dateTime,
        IOptions<ConsentPageOptions> options, ILogger<ConsentWhatsAppStarter> logger)
    {
        _context = context;
        _campaignSend = campaignSend;
        _dateTime = dateTime;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> StartAsync(ConsentInvitation invitation, CancellationToken cancellationToken = default)
    {
        if (invitation.DecisionStatus != ConsentDecisionStatus.Granted || invitation.WhatsAppInitiation == WhatsAppInitiationStatus.Started)
            return false;

        // A first message that already exists belongs to the campaign pipeline: sent, or failed and awaiting its own retry. Not sent again.
        if (await ReconcileWithPipelineAsync(invitation, cancellationToken))
            return false;

        if (invitation.WhatsAppAttemptCount >= _options.MaxWhatsAppAttempts)
            return false;

        // The claim. Saved before anything is sent: of two workers racing for the same consent only one saves this (the row is guarded by
        // its RowVersion), and the loser stops here without sending.
        invitation.WhatsAppInitiation = WhatsAppInitiationStatus.Started;
        invitation.WhatsAppInitiatedAtUtc = _dateTime.UtcNow;
        invitation.WhatsAppAttemptCount++;
        invitation.WhatsAppInitiationError = null;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation("Another worker is already starting the WhatsApp conversation for consent invitation {InvitationId}", invitation.Id);
            _context.ResetChangeTracker();
            return false;
        }

        string? failure = null;
        try
        {
            failure = await SendFirstMessageAsync(invitation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Starting the WhatsApp conversation failed for consent invitation {InvitationId}", invitation.Id);
            failure = $"The WhatsApp conversation could not be started: {ex.Message}";
        }

        if (failure is not null)
        {
            // The consent is untouched. Only the start is marked retryable.
            _context.ResetChangeTracker();
            var fresh = await _context.ConsentInvitations.FirstAsync(i => i.Id == invitation.Id, cancellationToken);
            fresh.WhatsAppInitiation = WhatsAppInitiationStatus.Failed;
            fresh.WhatsAppInitiationError = failure.Length > 1000 ? failure[..1000] : failure;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return failure is null;
    }

    /// <summary>True when the customer already has a message in the campaign - sent (this record is brought up to date) or failed (the pipeline retries it).</summary>
    private async Task<bool> ReconcileWithPipelineAsync(ConsentInvitation invitation, CancellationToken cancellationToken)
    {
        if (invitation.CampaignId is not { } campaignId)
            return false;

        var memberId = await _context.CampaignCustomers.AsNoTracking()
            .Where(cc => cc.CampaignId == campaignId && cc.CustomerId == invitation.CustomerId)
            .Select(cc => (Guid?)cc.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (memberId is null)
            return false;

        var latest = await _context.Messages.AsNoTracking()
            .Where(m => m.CampaignCustomerId == memberId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
            return false;

        if (latest.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read)
        {
            invitation.WhatsAppInitiation = WhatsAppInitiationStatus.Started;
            invitation.WhatsAppInitiatedAtUtc ??= _dateTime.UtcNow;
            invitation.WhatsAppInitiationError = null;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>Null when the first message was handed to the pipeline (sent, or held by a sending rule that releases it later); otherwise why not.</summary>
    private async Task<string?> SendFirstMessageAsync(ConsentInvitation invitation, CancellationToken cancellationToken)
    {
        if (invitation.CampaignId is not { } campaignId)
            return "There is no campaign to start a conversation from.";

        var member = await _context.CampaignCustomers
            .FirstOrDefaultAsync(cc => cc.CampaignId == campaignId && cc.CustomerId == invitation.CustomerId, cancellationToken);
        if (member is null)
            return "The customer is not in the campaign.";

        var outcome = await _campaignSend.SendFirstMessageAsync(member.Id, cancellationToken);
        _logger.LogInformation("Consent invitation {InvitationId} granted; first message: {Status}", invitation.Id, outcome.Status);

        return outcome.Status == FirstMessageStatus.Failed ? outcome.Detail ?? "The WhatsApp provider rejected the message." : null;
    }
}
