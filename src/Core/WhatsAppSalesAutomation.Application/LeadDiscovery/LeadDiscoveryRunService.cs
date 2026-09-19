using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>
/// One tenant's lead discovery run: ask the agent for candidates, qualify them (LeadQualification), drop the
/// ones already known, then save each survivor as a DiscoveredLead and a CRM Customer - repeated for up to
/// LeadDiscoveryOptions.MaxRounds rounds while the batch is not full, because rejected and duplicate
/// candidates leave gaps a single round cannot predict.
///
/// A candidate is a duplicate when it matches, on phone, website or name-plus-city, any lead this tenant
/// already discovered, or when its phone belongs to one of the tenant's customers (including soft-deleted
/// ones, which were removed on purpose). Customer data never leaves the application: the agent is only told
/// the names of businesses it found before, and customer matching happens here.
///
/// Each round's leads are saved before the next round starts, so a failure mid-run keeps what earlier
/// rounds found.
/// </summary>
public class LeadDiscoveryRunService : ILeadDiscoveryRunService
{
    /// <summary>Customer.Source for a business the discovery job found, alongside "Import" and "Manual".</summary>
    private const string CustomerSource = "Lead discovery";

    /// <summary>Customer.FirstName's column length.</summary>
    private const int CustomerNameLength = 100;

    private readonly IApplicationDbContext _context;
    private readonly ILeadDiscoveryAgent _agent;
    private readonly IPlanLimitsService _planLimits;
    private readonly IQuotaGate _quota;
    private readonly IDateTimeProvider _dateTime;
    private readonly LeadDiscoveryOptions _options;
    private readonly LeadDiscoveryPricingOptions _pricing;

    public LeadDiscoveryRunService(
        IApplicationDbContext context,
        ILeadDiscoveryAgent agent,
        IPlanLimitsService planLimits,
        IQuotaGate quota,
        IDateTimeProvider dateTime,
        IOptions<LeadDiscoveryOptions> options,
        IOptionsSnapshot<LeadDiscoveryPricingOptions> pricing)
    {
        _context = context;
        _agent = agent;
        _planLimits = planLimits;
        _quota = quota;
        _dateTime = dateTime;
        _options = options.Value;
        _pricing = pricing.Value;
    }

    public async Task<string> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var profile = await _context.LeadDiscoveryProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

        if (profile is null)
            return "Skipped: no lead discovery profile has been set up.";
        if (!profile.IsEnabled)
            return "Skipped: lead discovery is turned off in the profile.";
        if (profile.Keywords.Count == 0 || profile.Locations.Count == 0)
            return "Skipped: the profile needs at least one keyword and one location.";

        var planLimit = await _planLimits.GetLeadDiscoveryBatchLimitAsync(tenantId, cancellationToken);
        var batchSize = Math.Min(profile.BatchSize, planLimit ?? int.MaxValue);
        if (batchSize <= 0)
            return "Skipped: the tenant's plan allows no discovered leads per run.";

        // Prepaid: every candidate the agent evaluates is charged - fresh, duplicate or rejected - because the
        // provider bills the research either way. No candidates left means no run, and no agent call.
        if (await _quota.GetAvailableAsync(tenantId, QuotaType.LeadCandidates, cancellationToken) < 1)
            return "Skipped: no lead-candidate quota left - buy credits or wait for the plan to renew.";

        var runKey = Guid.NewGuid().ToString("N");

        var rules = new QualificationRules(
            profile.PhoneRequired, profile.EmailRequired, profile.IndependentBusiness, profile.MinimumLeadScore, profile.RequiredFields);

        var knownBusinesses = await _context.DiscoveredLeads
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.CreatedAt)
            .Take(_options.MaxKnownBusinessesInPrompt)
            .Select(l => l.City == null ? l.BusinessName : l.BusinessName + ", " + l.City)
            .ToListAsync(cancellationToken);

        var stats = new RunStats(batchSize);
        var model = string.Empty;

        while (stats.Saved < batchSize && stats.Rounds < _options.MaxRounds)
        {
            // Never ask the agent for more candidates than the tenant can still pay for.
            var affordable = await _quota.GetAvailableAsync(tenantId, QuotaType.LeadCandidates, cancellationToken);
            if (affordable < 1)
            {
                stats.QuotaExhausted = true;
                break;
            }

            stats.Rounds++;
            var remaining = batchSize - stats.Saved;

            var result = await _agent.DiscoverAsync(new LeadDiscoveryAgentRequest(
                profile.TargetBusinessType,
                profile.Keywords,
                profile.Locations,
                (int)Math.Min(Math.Min(remaining, _options.MaxCandidatesPerRound), Math.Floor(affordable)),
                profile.RequiredFields,
                profile.PhoneRequired,
                profile.EmailRequired,
                profile.IndependentBusiness,
                profile.MinimumLeadScore,
                profile.AdditionalCriteria,
                knownBusinesses), cancellationToken);

            if (!result.IsConfigured)
                return $"Skipped: {result.NotConfiguredReason}";

            model = result.Model;
            stats.Candidates += result.Candidates.Count;
            stats.Usage += result.Usage;

            if (result.Candidates.Count == 0)
                break;

            var rejectedBefore = stats.Rejected;
            var qualified = new List<VerifiedLead>();
            foreach (var assessment in LeadQualification.AssessAll(result.Candidates, result.Evidence, rules))
            {
                if (assessment.Lead is not null)
                    qualified.Add(assessment.Lead);
                else
                    stats.Reject(assessment.RejectionReason!);
            }

            var fresh = await RemoveDuplicatesAsync(tenantId, qualified, cancellationToken);
            stats.Duplicates += qualified.Count - fresh.Count;

            var toSave = fresh.Take(remaining).ToList();

            // Charged for what was evaluated this round, with the split recorded on the ledger entry so the tenant
            // can see why 25 candidates cost 25 units when only 12 became leads.
            await _quota.ConsumeLeadCandidatesAsync(
                tenantId,
                result.Candidates.Count,
                $"lead:{runKey}:r{stats.Rounds}",
                runKey,
                $"{result.Candidates.Count} candidates: {toSave.Count} new, {qualified.Count - fresh.Count} duplicate, {stats.Rejected - rejectedBefore} rejected",
                cancellationToken);

            if (toSave.Count == 0)
                break;

            foreach (var lead in toSave)
            {
                var discovered = ToEntity(tenantId, lead);
                discovered.CustomerId = AddCustomer(tenantId, lead);
                _context.DiscoveredLeads.Add(discovered);
                knownBusinesses.Add(lead.City is null ? lead.BusinessName : $"{lead.BusinessName}, {lead.City}");

                if (discovered.CustomerId is not null)
                    stats.CustomersAdded++;
            }

            await _context.SaveChangesAsync(cancellationToken);
            stats.Saved += toSave.Count;
        }

        await RecordRunAsync(tenantId, model, stats, cancellationToken);

        return stats.ToSummary();
    }

    /// <summary>
    /// Writes the tenant-visible record of what this run cost and produced (see
    /// <see cref="LeadDiscoveryRun"/>), priced at the configured rates for the model that ran it.
    ///
    /// Only for a run that actually researched: a run skipped before reaching the agent returned earlier and
    /// never gets here, and one that made no round has nothing to report. A simulated run does get a row, with
    /// zeros, so the tenant can see a run happened and cost nothing.
    /// </summary>
    private async Task RecordRunAsync(Guid tenantId, string model, RunStats stats, CancellationToken cancellationToken)
    {
        if (stats.Rounds == 0)
            return;

        _context.LeadDiscoveryRuns.Add(new LeadDiscoveryRun
        {
            TenantId = tenantId,
            RanAtUtc = _dateTime.UtcNow,
            Model = Truncate(model, LeadDiscoveryLimits.Model) ?? string.Empty,
            Rounds = stats.Rounds,
            CandidatesConsidered = stats.Candidates,
            LeadsSaved = stats.Saved,
            Duplicates = stats.Duplicates,
            Rejected = stats.Rejected,
            InputTokens = stats.Usage.InputTokens,
            OutputTokens = stats.Usage.OutputTokens,
            CacheReadTokens = stats.Usage.CacheReadInputTokens,
            CacheWriteTokens = stats.Usage.CacheCreationInputTokens,
            WebSearches = stats.Usage.WebSearches,
            WebFetches = stats.Usage.WebFetches,
            EstimatedCostUsd = LeadDiscoveryCost.Estimate(stats.Usage, model, _pricing)
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The leads that match neither an existing discovered lead, a customer, nor an earlier lead in
    /// the same list. Order is preserved.</summary>
    private async Task<List<VerifiedLead>> RemoveDuplicatesAsync(Guid tenantId, IReadOnlyList<VerifiedLead> leads, CancellationToken cancellationToken)
    {
        if (leads.Count == 0)
            return new List<VerifiedLead>();

        var phoneKeys = leads.Select(l => l.PhoneKey).OfType<string>().Distinct().ToList();
        var websiteKeys = leads.Select(l => l.WebsiteKey).OfType<string>().Distinct().ToList();
        var nameKeys = leads.Select(l => l.NameKey).Distinct().ToList();
        var phoneNumbers = leads.Select(l => l.PhoneE164).OfType<string>().Distinct().ToList();

        var existing = await _context.DiscoveredLeads
            .Where(l => l.TenantId == tenantId
                        && ((l.PhoneKey != null && phoneKeys.Contains(l.PhoneKey))
                            || (l.WebsiteKey != null && websiteKeys.Contains(l.WebsiteKey))
                            || nameKeys.Contains(l.NameKey)))
            .Select(l => new { l.PhoneKey, l.WebsiteKey, l.NameKey })
            .ToListAsync(cancellationToken);

        // IgnoreQueryFilters so soft-deleted customers still count; the tenant is re-applied by hand.
        var customerPhones = await _context.Customers.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && phoneNumbers.Contains(c.PhoneNumberE164))
            .Select(c => c.PhoneNumberE164)
            .ToListAsync(cancellationToken);

        var takenPhoneKeys = existing.Select(e => e.PhoneKey).OfType<string>().ToHashSet();
        var takenWebsiteKeys = existing.Select(e => e.WebsiteKey).OfType<string>().ToHashSet();
        var takenNameKeys = existing.Select(e => e.NameKey).ToHashSet();
        var takenPhoneNumbers = customerPhones.ToHashSet();

        var fresh = new List<VerifiedLead>();
        foreach (var lead in leads)
        {
            var isDuplicate = (lead.PhoneKey is { } phoneKey && takenPhoneKeys.Contains(phoneKey))
                              || (lead.PhoneE164 is { } e164 && takenPhoneNumbers.Contains(e164))
                              || (lead.WebsiteKey is { } websiteKey && takenWebsiteKeys.Contains(websiteKey))
                              || takenNameKeys.Contains(lead.NameKey);

            if (isDuplicate)
                continue;

            fresh.Add(lead);
            if (lead.PhoneKey is not null) takenPhoneKeys.Add(lead.PhoneKey);
            if (lead.WebsiteKey is not null) takenWebsiteKeys.Add(lead.WebsiteKey);
            takenNameKeys.Add(lead.NameKey);
        }

        return fresh;
    }

    private static DiscoveredLead ToEntity(Guid tenantId, VerifiedLead lead) => new()
    {
        TenantId = tenantId,
        BusinessName = Truncate(lead.BusinessName, LeadDiscoveryLimits.BusinessName)!,
        BusinessType = Truncate(lead.BusinessType, LeadDiscoveryLimits.BusinessType)!,
        ContactPerson = Truncate(lead.ContactPerson, LeadDiscoveryLimits.ContactPerson),
        Address = Truncate(lead.Address, LeadDiscoveryLimits.Address),
        City = Truncate(lead.City, LeadDiscoveryLimits.City),
        State = Truncate(lead.State, LeadDiscoveryLimits.State),
        Phone = Truncate(lead.Phone, LeadDiscoveryLimits.Phone),
        PhoneE164 = lead.PhoneE164,
        PhoneVerified = lead.Phone is not null,
        PhoneSourceUrl = Truncate(lead.PhoneSourceUrl, LeadDiscoveryLimits.Url),
        Email = Truncate(lead.Email, LeadDiscoveryLimits.Email),
        Website = Truncate(lead.Website, LeadDiscoveryLimits.Url),
        SourceUrl = Truncate(lead.SourceUrl, LeadDiscoveryLimits.Url)!,
        LeadScore = lead.LeadScore,
        ScoreRationale = Truncate(lead.ScoreRationale, LeadDiscoveryLimits.ScoreRationale),
        PhoneKey = lead.PhoneKey,
        WebsiteKey = Truncate(lead.WebsiteKey, LeadDiscoveryLimits.DedupeKey),
        NameKey = Truncate(lead.NameKey, LeadDiscoveryLimits.DedupeKey)!
    };

    /// <summary>
    /// Adds the business to the CRM as a <see cref="Customer"/> and returns its id, or null when the lead has
    /// no phone number in E.164 form - Customer requires one and it is unique per tenant, so a business whose
    /// number could not be normalized stays a discovery row only.
    ///
    /// OptInStatus is left at its PendingOptIn default and no consent field (OptInTimestamp, OptInSource) is
    /// written: consent is a person's decision, recorded through the customer's own opt-in endpoint.
    /// CampaignSendService and CampaignService only ever send to OptedIn customers, so nothing added here can
    /// be messaged until someone opts it in by hand.
    ///
    /// Numbers already belonging to a customer were removed as duplicates before this point, so this cannot
    /// collide with the unique phone index.
    /// </summary>
    private Guid? AddCustomer(Guid tenantId, VerifiedLead lead)
    {
        if (lead.PhoneE164 is not { } phoneE164)
            return null;

        var customer = new Customer
        {
            TenantId = tenantId,
            PhoneNumberE164 = phoneE164,
            // Customer is person-shaped, a discovered lead is a business: the named contact when the research
            // found one, the business name otherwise, so the CRM row is recognisable either way.
            FirstName = Truncate(lead.ContactPerson ?? lead.BusinessName, CustomerNameLength),
            Email = Truncate(lead.Email, LeadDiscoveryLimits.Email),
            Source = CustomerSource
        };

        _context.Customers.Add(customer);
        return customer.Id;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    private sealed class RunStats
    {
        private readonly int _batchSize;
        private readonly Dictionary<string, int> _rejections = new();

        public RunStats(int batchSize) => _batchSize = batchSize;

        public int Rounds { get; set; }
        public int Candidates { get; set; }
        public int Saved { get; set; }
        public int CustomersAdded { get; set; }
        public int Duplicates { get; set; }
        public bool QuotaExhausted { get; set; }
        public LeadDiscoveryUsage Usage { get; set; } = LeadDiscoveryUsage.None;

        public int Rejected => _rejections.Values.Sum();

        public void Reject(string reason) => _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;

        /// <summary>Counts first, then what the run consumed - enough to price a run from the job list without
        /// opening the Anthropic Console. A simulated run reports zeros for the token counts.</summary>
        public string ToSummary()
        {
            var summary = $"saved={Saved} customers={CustomersAdded} batchSize={_batchSize} rounds={Rounds} " +
                          $"candidates={Candidates} duplicates={Duplicates} rejected={_rejections.Values.Sum()} " +
                          $"inputTokens={Usage.InputTokens} outputTokens={Usage.OutputTokens} " +
                          $"cacheReadTokens={Usage.CacheReadInputTokens} cacheWriteTokens={Usage.CacheCreationInputTokens} " +
                          $"webSearches={Usage.WebSearches} webFetches={Usage.WebFetches}";

            if (QuotaExhausted)
                summary += " stoppedEarly=quotaExhausted";

            return _rejections.Count == 0
                ? summary
                : $"{summary} rejections: {string.Join("; ", _rejections.Select(r => $"{r.Key}={r.Value}"))}";
        }
    }
}
