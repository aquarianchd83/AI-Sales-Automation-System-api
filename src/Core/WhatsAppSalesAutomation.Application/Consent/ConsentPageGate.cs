using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <inheritdoc />
public class ConsentPageGate : IConsentPageGate
{
    public const string NotificationTitle = "WhatsApp Consent Page Setup Required";

    public const string NotificationBody =
        "Your WhatsApp Consent Page configuration is incomplete or unpublished. Please complete and publish your product information and WhatsApp consent " +
        "statement before starting customer discovery. No discovery credits have been deducted.";

    private readonly IApplicationDbContext _context;
    private readonly ITenantNotifier _notifier;
    private readonly ConsentPageOptions _options;
    private readonly ILogger<ConsentPageGate> _logger;

    public ConsentPageGate(
        IApplicationDbContext context, ITenantNotifier notifier, IOptions<ConsentPageOptions> options, ILogger<ConsentPageGate> logger)
    {
        _context = context;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConsentPageReadiness> EvaluateAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Looked up by the tenant given, not the ambient one: this runs from background jobs as well as from requests.
        var page = await _context.TenantConsentPages.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

        if (page is null)
            return NotReady(ConsentPageReadinessStates.NotConfigured, new[] { "businessName", "productDescription" },
                "The WhatsApp Consent Page has not been set up.");

        if (page.PublishedVersion == 0)
        {
            var issues = ConsentPageRules.ValidateForPublish(
                page.BusinessName, page.ProductDescription, page.AdditionalInfo, page.ConsentStatement, page.PrivacyPolicyUrl, page.TermsUrl);
            return issues.Count > 0
                ? NotReady(ConsentPageReadinessStates.Incomplete, issues.Select(i => i.Field).Distinct().ToList(), "The WhatsApp Consent Page is incomplete.")
                : NotReady(ConsentPageReadinessStates.Unpublished, Array.Empty<string>(), "The WhatsApp Consent Page has not been published.");
        }

        if (!page.IsActive)
            return NotReady(ConsentPageReadinessStates.Inactive, Array.Empty<string>(), "The WhatsApp Consent Page is switched off.");

        var live = await _context.ConsentPageVersions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Version == page.PublishedVersion, cancellationToken);
        if (live is null)
            return NotReady(ConsentPageReadinessStates.Unpublished, Array.Empty<string>(), "The published version of the WhatsApp Consent Page was not found.");

        // The snapshot is re-checked: whatever was live must still be valid, not just once have been.
        var liveIssues = ConsentPageRules.ValidateForPublish(live.BusinessName, live.ProductDescription, live.AdditionalInfo, live.ConsentStatement, live.PrivacyPolicyUrl, live.TermsUrl);
        if (liveIssues.Count > 0)
            return NotReady(ConsentPageReadinessStates.Incomplete, liveIssues.Select(i => i.Field).Distinct().ToList(), "The published WhatsApp Consent Page is no longer valid.");

        if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
            return NotReady(ConsentPageReadinessStates.PlatformNotConfigured, Array.Empty<string>(),
                "The platform has no public address for the consent page (ConsentPage:PublicBaseUrl).");

        return new ConsentPageReadiness(true, ConsentPageReadinessStates.Ready, Array.Empty<string>(), live, string.Empty);
    }

    public async Task NotifyAsync(Guid tenantId, ConsentPageReadiness readiness, CancellationToken cancellationToken = default)
    {
        if (readiness.IsReady)
            return;

        if (readiness.PlatformProblem)
        {
            // Nothing the tenant can do; the operator needs to hear it from the logs.
            _logger.LogError("Consent page cannot be linked for tenant {TenantId}: {Reason}", tenantId, readiness.Reason);
            return;
        }

        try
        {
            var published = await _context.TenantConsentPages.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.TenantId == tenantId).Select(p => p.PublishedVersion).FirstOrDefaultAsync(cancellationToken);

            // One notification per unresolved problem: the same page version in the same state with the same fields missing is one episode.
            // Fixing and publishing bumps the version, so the same problem coming back later is a new episode and notifies again.
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", readiness.Missing.OrderBy(m => m))))).Substring(0, 8);
            var episode = $"v{published}:{readiness.State}:{signature}";

            await _notifier.NotifyAsync(new TenantNotificationRequest(
                tenantId, TenantNotificationKind.ConsentPageSetupRequired, null, episode, NotificationTitle, NotificationBody, AlsoWhatsApp: false),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not notify tenant {TenantId} that the consent page needs setting up", tenantId);
        }
    }

    private static ConsentPageReadiness NotReady(string state, IReadOnlyList<string> missing, string reason) =>
        new(false, state, missing, null, reason);
}
