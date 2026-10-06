using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Onboarding;

/// <summary>Done (its data is there); Current - the first step not done, the one to do next; Pending - not done and
/// waiting behind Current, so not open yet.</summary>
public enum OnboardingStepState
{
    Completed,
    Current,
    Pending
}

public sealed record OnboardingStepDto(
    string Key,
    string Title,
    string Description,
    int Weight,
    string Route,
    OnboardingStepState State,
    DateTime? CompletedAt,
    // For a step that is not done: what is still missing, in plain words.
    string? Missing);

public sealed record OnboardingStatusDto(
    bool IsCompleted,
    int ProgressPercent,
    string? CurrentStepKey,
    DateTime? CompletedAt,
    IReadOnlyList<OnboardingStepDto> Steps);

public interface IOnboardingService
{
    /// <summary>
    /// The calling tenant's onboarding, checked live: every step is judged on its own data as it is right now, in any
    /// order, and progress is the weight of the steps that are met. The first step not met is the Current one; any
    /// others not met are Pending until it is done. Null for a caller with no tenant (a PlatformSuperAdmin).
    /// </summary>
    Task<OnboardingStatusDto?> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sequential, weighted tenant onboarding (see OnboardingCatalog). Each step is judged by the data it produces -
/// a saved profile, a subscription, a package, a verified WhatsApp number, ... - never by a "mark as done" button,
/// so it cannot be completed without actually doing it.
///
/// Checked live, not remembered: delete the only customer package and Step 3 is incomplete again, progress drops and
/// the tenant is back in setup until it is replaced. TenantOnboardingSteps is the record of what is complete now (with
/// when it first was), kept in step with each read.
/// </summary>
public class OnboardingService : IOnboardingService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;
    private readonly ITenantWhatsAppConfigProvider _whatsApp;
    private readonly IDateTimeProvider _clock;

    public OnboardingService(
        IApplicationDbContext context, ITenantContext tenantContext, ICurrentUserService currentUser,
        ITenantWhatsAppConfigProvider whatsApp, IDateTimeProvider clock)
    {
        _context = context;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _whatsApp = whatsApp;
        _clock = clock;
    }

    public async Task<OnboardingStatusDto?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return null;

        var tenant = await _context.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken);
        var recorded = await _context.TenantOnboardingSteps.Where(s => s.TenantId == tenantId)
            .ToDictionaryAsync(s => s.StepKey, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var now = _clock.UtcNow;
        var steps = new List<OnboardingStepDto>();
        var foundCurrent = false;

        foreach (var step in OnboardingCatalog.Steps)
        {
            recorded.TryGetValue(step.Key, out var row);

            var missing = await CheckAsync(step.Key, tenant, tenantId, cancellationToken);
            if (missing is null)
            {
                // Met. Keep the time it first was; a step met again after being undone starts a new record.
                if (row is null)
                {
                    row = new TenantOnboardingStep
                    {
                        TenantId = tenantId, StepKey = step.Key, CompletedAt = now, CompletedBy = _currentUser.UserId
                    };
                    _context.TenantOnboardingSteps.Add(row);
                }

                steps.Add(ToDto(step, OnboardingStepState.Completed, row.CompletedAt, null));
                continue;
            }

            // Not met: not complete (and no longer, if it used to be).
            if (row is not null)
                _context.TenantOnboardingSteps.Remove(row);

            steps.Add(ToDto(step, foundCurrent ? OnboardingStepState.Pending : OnboardingStepState.Current, null, missing));
            foundCurrent = true;
        }

        // Completed while every step is met; back to in progress the moment one is not.
        tenant.OnboardingCompletedAt = foundCurrent ? null : tenant.OnboardingCompletedAt ?? now;

        await _context.SaveChangesAsync(cancellationToken);

        var progress = steps.Where(s => s.State == OnboardingStepState.Completed).Sum(s => s.Weight);
        return new OnboardingStatusDto(
            tenant.OnboardingCompletedAt is not null,
            progress,
            steps.FirstOrDefault(s => s.State == OnboardingStepState.Current)?.Key,
            tenant.OnboardingCompletedAt,
            steps);
    }

    private static OnboardingStepDto ToDto(OnboardingStepDefinition step, OnboardingStepState state, DateTime? completedAt, string? missing) =>
        new(step.Key, step.Title, step.Description, step.Weight, step.Route, state, completedAt, missing);

    /// <summary>Null when the step's requirement is met; otherwise what is still missing.</summary>
    private async Task<string?> CheckAsync(string key, Tenant tenant, Guid tenantId, CancellationToken cancellationToken)
    {
        switch (key)
        {
            case OnboardingCatalog.Profile:
            {
                var gaps = new List<string>();
                if (string.IsNullOrWhiteSpace(tenant.Name)) gaps.Add("company name");
                if (string.IsNullOrWhiteSpace(tenant.Industry)) gaps.Add("industry");
                if (string.IsNullOrWhiteSpace(tenant.BusinessDescription)) gaps.Add("business description");
                if (string.IsNullOrWhiteSpace(tenant.SupportEmail)) gaps.Add("support email");
                if (string.IsNullOrWhiteSpace(tenant.SupportPhone)) gaps.Add("support phone");
                if (string.IsNullOrWhiteSpace(tenant.CountryCode)) gaps.Add("country");
                return gaps.Count == 0 ? null : $"Still needed: {string.Join(", ", gaps)}.";
            }

            case OnboardingCatalog.Plan:
                return await _context.Subscriptions.AnyAsync(s => s.PlanId != null && s.Status != SubscriptionStatus.Canceled, cancellationToken)
                    ? null
                    : "Choose a plan.";

            case OnboardingCatalog.CustomerPackage:
                return await _context.SalesPackages.AnyAsync(p => p.IsActive, cancellationToken)
                    ? null
                    : "Create an active package for your customers.";

            case OnboardingCatalog.LeadDiscovery:
            {
                var profile = await _context.LeadDiscoveryProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
                if (profile is null)
                    return "Save your lead discovery profile.";
                return !string.IsNullOrWhiteSpace(profile.TargetBusinessType) && profile.Locations.Count > 0
                    ? null
                    : "Add the type of business you are looking for and at least one location.";
            }

            case OnboardingCatalog.WhatsApp:
            {
                var config = await _whatsApp.GetConfigForTenantAsync(tenantId, cancellationToken);
                if (config is null || !config.IsConnected)
                    return "Save your WhatsApp phone number ID, access token and app secret.";
                if (config.IsVerified)
                    return null;
                return config.VerificationError is { } error
                    ? $"Verification failed: {error}"
                    : "Verify the connection.";
            }

            case OnboardingCatalog.MessageTemplate:
                // Submitted counts: Meta's approval can take hours and must not hold the tenant up. A rejected one does not.
                return await _context.MessageTemplates.AnyAsync(t => t.IsActive && t.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Rejected, cancellationToken)
                    ? null
                    : "Create a message template (a rejected one does not count).";

            case OnboardingCatalog.Customer:
                return await _context.Customers.AnyAsync(cancellationToken) ? null : "Add a customer.";

            case OnboardingCatalog.Campaign:
                return await _context.Campaigns.AnyAsync(c => c.Steps.Any() && c.CampaignCustomers.Any(), cancellationToken)
                    ? null
                    : "Create a campaign with at least one message step and one customer.";

            case OnboardingCatalog.KnowledgeBase:
            {
                var processed = await _context.KnowledgeIngestionJobs.AnyAsync(j =>
                    j.TenantId == tenantId && j.State == KnowledgeIngestionState.Completed
                    && _context.KnowledgeBaseArticles.Any(a => a.Id == j.ArticleId && a.TenantId == tenantId && !a.IsDeleted), cancellationToken);
                return processed ? null : "Upload your voucher and wait until it has been processed.";
            }

            default:
                throw new InvalidOperationException($"No completion rule for onboarding step '{key}'.");
        }
    }
}
