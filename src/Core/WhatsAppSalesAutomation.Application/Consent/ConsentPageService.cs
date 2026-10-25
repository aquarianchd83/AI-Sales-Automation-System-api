using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <inheritdoc />
public class ConsentPageService : IConsentPageService
{
    /// <summary>The OptInSource written when the customer agrees on the consent page.</summary>
    public const string ConsentSource = "ConsentPage";

    /// <summary>The wording the customer agrees to promises follow-ups, so the opt-in is for marketing as well as service messages, on WhatsApp only.</summary>
    internal const ConsentScope AgreedScope = ConsentScope.Marketing;

    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _dateTime;
    private readonly IConsentWhatsAppStarter _starter;
    private readonly ILogger<ConsentPageService> _logger;

    public ConsentPageService(
        IApplicationDbContext context, ITenantContext tenantContext, IDateTimeProvider dateTime,
        IConsentWhatsAppStarter starter, ILogger<ConsentPageService> logger)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dateTime = dateTime;
        _starter = starter;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------------
    // Viewing - read only
    // ---------------------------------------------------------------------------------------------

    public async Task<PublicConsentPageDto> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        var invitation = await ResolveAsync(token, cancellationToken);
        if (invitation is null)
            return Invalid();

        if (invitation.DecisionStatus is ConsentDecisionStatus.Granted or ConsentDecisionStatus.NotInterested or ConsentDecisionStatus.Revoked)
            return Answered(invitation, await BusinessNameAsync(invitation, cancellationToken));

        if (IsExpired(invitation))
            return new PublicConsentPageDto(PublicConsentStates.Expired, null, null, "This link has expired.");

        var live = await LiveVersionAsync(invitation.TenantId, cancellationToken);
        return live is null
            ? new PublicConsentPageDto(PublicConsentStates.Unavailable, null, null, "This page is not available right now. Please try again later.")
            : new PublicConsentPageDto(PublicConsentStates.Ready, ConsentPageRules.ToContent(live), null, null);
    }

    // ---------------------------------------------------------------------------------------------
    // Deciding
    // ---------------------------------------------------------------------------------------------

    public async Task<ConsentDecisionResultDto> DecideAsync(
        string token, ConsentDecisionRequest request, ConsentClientInfo client, CancellationToken cancellationToken = default)
    {
        var agree = string.Equals(request.Decision, "agree", StringComparison.OrdinalIgnoreCase);
        var decline = string.Equals(request.Decision, "decline", StringComparison.OrdinalIgnoreCase);
        if (!agree && !decline)
            return Result(ConsentDecisionOutcomes.BadRequest, null, "Choose Yes or No.");

        // The checkbox is enforced here, on the server. Hiding the button is a courtesy, not the rule.
        if (agree && !request.ConsentChecked)
            return Result(ConsentDecisionOutcomes.BadRequest, null, "Tick the box to confirm you agree to receive WhatsApp messages.");

        // Two attempts: a racing writer that wins the row makes the first save fail, and the second pass then sees what it decided.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var invitation = await ResolveAsync(token, cancellationToken);
            if (invitation is null)
                return Result(ConsentDecisionOutcomes.Invalid, null, "This link is not valid.");

            var existing = Existing(invitation, agree, await BusinessNameAsync(invitation, cancellationToken));
            if (existing is not null)
            {
                // A repeat of the same choice is a no-op - except that a consent whose WhatsApp start failed is given another go.
                if (existing.Outcome == ConsentDecisionOutcomes.AlreadyRecorded && agree)
                    await _starter.StartAsync(invitation, cancellationToken);
                return existing;
            }

            if (IsExpired(invitation))
            {
                invitation.DecisionStatus = ConsentDecisionStatus.Expired;
                await TrySaveAsync(cancellationToken);
                return Result(ConsentDecisionOutcomes.Expired, null, "This link has expired.");
            }

            if (invitation.HeldForConsentPage || invitation.TokenHash is null)
                return Result(ConsentDecisionOutcomes.Invalid, null, "This link is not valid.");

            var live = await LiveVersionAsync(invitation.TenantId, cancellationToken);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == invitation.CustomerId && c.TenantId == invitation.TenantId, cancellationToken);
            if (customer is null)
                return Result(ConsentDecisionOutcomes.Invalid, null, "This link is not valid.");

            var now = _dateTime.UtcNow;
            string? consentText = null;
            int? pageVersion = live?.Version;

            if (agree)
            {
                if (live is null)
                    return Result(ConsentDecisionOutcomes.Unavailable, null, "This page is not available right now. Please try again later.");

                // They agreed to the words they were shown. If those have changed since the page loaded, they must read the new ones.
                if (request.PageVersion != live.Version)
                    return Result(ConsentDecisionOutcomes.PageChanged, null, "This page was updated. Please review it and choose again.",
                        new PublicConsentPageDto(PublicConsentStates.Ready, ConsentPageRules.ToContent(live), null, null));

                // A number that has opted out stays opted out until the business records a fresh, deliberate opt-in.
                if (customer.OptInStatus == OptInStatus.OptedOut)
                    return Result(ConsentDecisionOutcomes.NotAllowed, null, "This number has opted out of WhatsApp messages from this business.");

                consentText = live.ConsentStatement;
            }

            invitation.DecisionStatus = agree ? ConsentDecisionStatus.Granted : ConsentDecisionStatus.NotInterested;
            invitation.DecisionAtUtc = now;
            invitation.DecisionSource = ConsentSource;
            invitation.DecisionPageVersion = pageVersion;
            invitation.DecisionConsentText = consentText;
            invitation.DecisionIpHash = ConsentLinkToken.HashClient(client.IpAddress);
            invitation.DecisionUserAgent = Truncate(client.UserAgent, 200);
            invitation.ConsentRecordedAtUtc = agree ? now : null;

            if (agree)
                RecordConsent(customer, invitation, live!, now);
            else
                AddHistory(customer, invitation, CustomerHistoryEventTypes.ConsentDeclined, customer.OptInStatus.ToString(), "NotInterested",
                    "The customer chose \"No, Not Interested\" on the consent page. No WhatsApp conversation will be started from this request.", now);

            if (!await TrySaveAsync(cancellationToken))
            {
                // Someone else decided in between. Look again and report what is now true.
                _context.ResetChangeTracker();
                continue;
            }

            // The consent is committed. Only now is the conversation started - after the transaction, and never inside it.
            if (agree)
                await _starter.StartAsync(invitation, cancellationToken);

            _logger.LogInformation("Consent invitation {InvitationId} decided: {Decision}", invitation.Id, invitation.DecisionStatus);
            return Result(ConsentDecisionOutcomes.Recorded, invitation.DecisionStatus.ToString(), ThankYou(invitation, live?.BusinessName));
        }

        return Result(ConsentDecisionOutcomes.Conflict, null, "Your answer could not be saved because it was being changed. Please try again.");
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>The invitation a link belongs to, found by the hash of its secret. Looked up across tenants on purpose - the link has no tenant
    /// yet; the invitation says whose it is, and only then does that tenant become the ambient one.</summary>
    private async Task<ConsentInvitation?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (!ConsentLinkToken.LooksValid(token))
            return null;

        var hash = ConsentLinkToken.Hash(token);
        var invitation = await _context.ConsentInvitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        if (invitation is null)
            return null;

        _tenantContext.SetTenant(invitation.TenantId);
        return invitation;
    }

    private Task<ConsentPageVersion?> LiveVersionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (from page in _context.TenantConsentPages.IgnoreQueryFilters()
         join version in _context.ConsentPageVersions.IgnoreQueryFilters() on new { page.TenantId, Version = page.PublishedVersion } equals new { version.TenantId, version.Version }
         where page.TenantId == tenantId && page.IsActive && page.PublishedVersion > 0
         select version).AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    private bool IsExpired(ConsentInvitation invitation) =>
        invitation.DecisionStatus == ConsentDecisionStatus.Expired
        || (invitation.TokenExpiresAtUtc is { } expires && expires <= _dateTime.UtcNow);

    /// <summary>What a decision that is already on record means for this request, or null when the request is a new decision.</summary>
    private ConsentDecisionResultDto? Existing(ConsentInvitation invitation, bool agree, string? businessName)
    {
        switch (invitation.DecisionStatus)
        {
            case ConsentDecisionStatus.Granted when agree:
                return Result(ConsentDecisionOutcomes.AlreadyRecorded, nameof(ConsentDecisionStatus.Granted), ThankYou(invitation, businessName));
            case ConsentDecisionStatus.NotInterested when !agree:
                return Result(ConsentDecisionOutcomes.AlreadyRecorded, nameof(ConsentDecisionStatus.NotInterested), ThankYou(invitation, businessName));
            case ConsentDecisionStatus.Granted:
                return Result(ConsentDecisionOutcomes.Conflict, nameof(ConsentDecisionStatus.Granted),
                    "You already chose to receive WhatsApp messages. To stop them, reply STOP on WhatsApp.");
            case ConsentDecisionStatus.NotInterested:
                return Result(ConsentDecisionOutcomes.Conflict, nameof(ConsentDecisionStatus.NotInterested),
                    "You already told us you are not interested, so this link can no longer be used to agree.");
            case ConsentDecisionStatus.Revoked:
                return Result(ConsentDecisionOutcomes.Conflict, nameof(ConsentDecisionStatus.Revoked),
                    "WhatsApp messages from this business were switched off for this number.");
            default:
                return null;
        }
    }

    private void RecordConsent(Customer customer, ConsentInvitation invitation, ConsentPageVersion live, DateTime now)
    {
        var previous = customer.OptInStatus.ToString();
        customer.OptInStatus = OptInStatus.OptedIn;
        customer.OptInTimestamp = now;
        customer.OptInSource = ConsentSource;
        customer.OptInProof = $"Consent page, request {invitation.Id}, page version {live.Version}, agreed at {now:yyyy-MM-dd HH:mm:ss} UTC";
        customer.OptInScope = AgreedScope;
        customer.OptOutTimestamp = null;

        AddHistory(customer, invitation, CustomerHistoryEventTypes.ConsentRecorded, previous, OptInStatus.OptedIn.ToString(),
            $"Explicit WhatsApp opt-in. The customer ticked the consent box and chose \"Yes, I Agree\" on the consent page. Evidence: request {invitation.Id}, " +
            $"page version {live.Version}, source {ConsentSource}, scope {AgreedScope}, tenant {customer.TenantId}, at {now:yyyy-MM-dd HH:mm:ss} UTC. " +
            "Covers WhatsApp messaging from this business only.", now);
    }

    private void AddHistory(Customer customer, ConsentInvitation invitation, string eventType, string? previous, string? next, string reason, DateTime at)
    {
        _context.CustomerHistoryEvents.Add(new CustomerHistoryEvent
        {
            TenantId = customer.TenantId,
            CustomerId = customer.Id,
            EventType = eventType,
            OccurredAtUtc = at,
            PreviousStatus = previous,
            NewStatus = next,
            Reason = reason,
            CampaignId = invitation.CampaignId,
            ExecutionId = invitation.ExecutionId,
            CorrelationId = invitation.CorrelationId
        });
    }

    /// <summary>False when a concurrent writer got there first (the row's version moved on).</summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static PublicConsentPageDto Answered(ConsentInvitation invitation, string? businessName) =>
        new(PublicConsentStates.Answered, null, invitation.DecisionStatus.ToString(), ThankYou(invitation, businessName));

    /// <summary>The business named on the page version this customer decided on - the words they saw, not whatever is live now.</summary>
    private Task<string?> BusinessNameAsync(ConsentInvitation invitation, CancellationToken cancellationToken) =>
        _context.ConsentPageVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.TenantId == invitation.TenantId && v.Version == (invitation.DecisionPageVersion ?? 0))
            .Select(v => v.BusinessName).FirstOrDefaultAsync(cancellationToken);

    private static PublicConsentPageDto Invalid() => new(PublicConsentStates.Invalid, null, null, "This link is not valid.");

    private static ConsentDecisionResultDto Result(string outcome, string? decision, string message, PublicConsentPageDto? page = null) =>
        new(outcome, decision, message, page);

    private static string ThankYou(ConsentInvitation invitation, string? businessName) => invitation.DecisionStatus switch
    {
        ConsentDecisionStatus.Granted => $"Thank you! Your preference has been saved. You may now receive WhatsApp messages from {(string.IsNullOrWhiteSpace(businessName) ? "this business" : businessName)}.",
        ConsentDecisionStatus.NotInterested => "Thank you for letting us know. Your preference has been saved. No WhatsApp conversation will be started from this request.",
        _ => "Thank you."
    };

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
