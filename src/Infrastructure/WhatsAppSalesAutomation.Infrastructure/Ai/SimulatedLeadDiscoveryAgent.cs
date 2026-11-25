using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Simulator;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The no-cost <see cref="ILeadDiscoveryAgent"/>, selected with <c>LeadDiscovery:Agent:Provider =
/// "Simulated"</c> - the same convention as WhatsAppSettings.Provider and AiProviderSettings.Provider. It
/// searches nothing and invents nothing: the businesses it hands back are the lead business rows on the platform admin's
/// Simulator dummy data page (<see cref="ISimulatorDataProvider"/>), exactly as they are stored - same name, phone, email, website,
/// address, contact and score. Only a blank City or Business type is filled from the run itself (its location and target).
///
/// Each stored row is handed out at most once per call, and a row whose business this tenant already has (it is in the request's
/// KnownBusinesses) is skipped, so a run does not fill up with duplicates of earlier runs. When every stored business has been used,
/// a run simply finds nothing new - add more rows (or delete the earlier customers) to discover again. The run service's own
/// duplicate check still applies and reports anything else as a duplicate; the stored data is never altered to get past it.
///
/// The "fetched page" evidence is built from the stored row so the rest of the pipeline (verification, the qualification rules,
/// duplicate removal, saving the lead and its CRM customer) runs as it would for a real search: a qualifying row's phone and email
/// appear on its page; a flawed row (phone on no page, or permanently closed) is rejected the way a real one would be. The share of
/// flawed rows in a run is the FlawedSharePercent setting (only when three or more are asked for). The run is recorded with the
/// model "Simulated".
/// </summary>
public class SimulatedLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    /// <summary>Recorded as the run's model so a simulated run is unmistakable in the spend history.</summary>
    public const string ModelName = "Simulated";

    private readonly ILogger<SimulatedLeadDiscoveryAgent> _logger;
    private readonly ISimulatorDataProvider? _data;

    public SimulatedLeadDiscoveryAgent(ILogger<SimulatedLeadDiscoveryAgent> logger, ISimulatorDataProvider? data = null)
    {
        _logger = logger;
        _data = data;
    }

    public async Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        var data = _data is null ? SimulatorDataSnapshot.Defaults : await _data.GetAsync(cancellationToken);
        var flawedPercent = data.Number(SimulatorDataCatalog.Settings.FlawedSharePercent, 0, 90);

        var location = request.Locations.FirstOrDefault() ?? "Chandigarh";
        var city = location.Split(',')[0].Trim();

        // Businesses this tenant already has are left out, so stored rows are not handed back as duplicates run after run.
        var available = data.Businesses.Where(b => !IsKnown(b, request.KnownBusinesses)).ToList();

        // The kind of business asked for comes first; the others follow, so a run is never empty just because no row has that kind.
        var ordered = available.OrderBy(b => Matches(b, request.TargetBusinessType) ? 0 : 1).ToList();
        var good = ordered.Where(b => b.Variant == SimulatorLeadVariant.Qualified).ToList();
        var bad = ordered.Where(b => b.Variant != SimulatorLeadVariant.Qualified).ToList();

        var asked = Math.Max(1, request.MaxCandidates);
        var flawed = asked >= 3 && flawedPercent > 0
            ? Math.Min(bad.Count, Math.Max(1, (int)Math.Round(asked * flawedPercent / 100.0, MidpointRounding.AwayFromZero)))
            : 0;
        var picked = good.Take(asked - flawed).Concat(bad.Take(flawed)).ToList();
        // Too few qualifying rows: fill up with the remaining flawed ones rather than hand back less than there is.
        if (picked.Count < asked)
            picked.AddRange(bad.Skip(flawed).Take(asked - picked.Count));

        var pages = new List<FetchedPage>();
        var seenUrls = new List<string> { $"https://search.example/?q={Uri.EscapeDataString(request.TargetBusinessType)}" };
        var candidates = picked.Select(b => Build(b, request, city, pages, seenUrls)).ToList();

        _logger.LogInformation(
            "Simulated lead discovery handed back {Count} stored dummy businesses for {BusinessType} in {Location} ({Available} not yet known of {Stored} on the Simulator dummy data page) - no API call was made",
            candidates.Count, request.TargetBusinessType, location, available.Count, data.Businesses.Count);

        return new LeadDiscoveryAgentResult(
            IsConfigured: true,
            NotConfiguredReason: null,
            Candidates: candidates,
            Evidence: new LeadDiscoveryEvidence(seenUrls, pages),
            Usage: LeadDiscoveryUsage.None,
            Model: ModelName);
    }

    /// <summary>KnownBusinesses lists "Name" or "Name, City".</summary>
    private static bool IsKnown(SimulatorLeadBusiness business, IReadOnlyList<string> known) =>
        known.Any(k => string.Equals(k.Trim(), business.Name, StringComparison.OrdinalIgnoreCase)
                       || k.Trim().StartsWith(business.Name + ",", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(SimulatorLeadBusiness business, string target) =>
        !string.IsNullOrWhiteSpace(business.BusinessType) && !string.IsNullOrWhiteSpace(target)
        && (business.BusinessType.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase)
            || target.Contains(business.BusinessType.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DiscoveredBusinessCandidate Build(
        SimulatorLeadBusiness b, LeadDiscoveryAgentRequest request, string requestCity, List<FetchedPage> pages, List<string> seenUrls)
    {
        var name = b.Name;
        var businessType = string.IsNullOrWhiteSpace(b.BusinessType) ? Capitalise(request.TargetBusinessType) : b.BusinessType!;
        var city = string.IsNullOrWhiteSpace(b.City) ? requestCity : b.City!;
        var address = string.IsNullOrWhiteSpace(b.Address) ? null : b.Address;
        var phone = b.Phone;
        var email = string.IsNullOrWhiteSpace(b.Email) ? null : b.Email;
        var website = string.IsNullOrWhiteSpace(b.Website) ? null : b.Website!.Trim();

        // Where the simulated "search" saw it: the stored website, or a simulator page when the row has none (evidence only, not lead data).
        var pageBase = (website ?? $"https://simulator.example/business/{Slug(b.Name)}").TrimEnd('/');

        switch (b.Variant)
        {
            case SimulatorLeadVariant.PhoneNotOnPage:
            {
                // The listing is seen but never fetched, so the phone is on no page.
                var url = pageBase + "/listing";
                seenUrls.Add(url);
                return new DiscoveredBusinessCandidate(name, businessType, b.ContactPerson, address, city, null, phone, null, email, website, url,
                    IsIndependentBusiness: true, IsPermanentlyClosed: false, LeadScore: b.LeadScore,
                    ScoreRationale: "Simulator dummy data - its phone number is on no fetched page.");
            }
            case SimulatorLeadVariant.PermanentlyClosed:
            {
                var url = pageBase + "/contact";
                pages.Add(new FetchedPage(url, $"{name}\nThis business has permanently closed. Old number: {phone}."));
                seenUrls.Add(url);
                return new DiscoveredBusinessCandidate(name, businessType, b.ContactPerson, address, city, null, phone, url, email, website, url,
                    IsIndependentBusiness: true, IsPermanentlyClosed: true, LeadScore: b.LeadScore,
                    ScoreRationale: "Simulator dummy data - the business is permanently closed.");
            }
            default:
            {
                var contactUrl = pageBase + "/contact";
                var text = $"{name}\n{businessType} in {city}.\nCall {phone}" + (email is null ? "." : $" or write to {email}.")
                           + (address is null ? "" : $"\nAddress: {address}.");
                pages.Add(new FetchedPage(contactUrl, text));
                seenUrls.Add(contactUrl);
                if (website is not null)
                    seenUrls.Add(website);
                return new DiscoveredBusinessCandidate(name, businessType, b.ContactPerson, address, city, null, phone, contactUrl, email, website, contactUrl,
                    IsIndependentBusiness: true, IsPermanentlyClosed: false, LeadScore: b.LeadScore,
                    ScoreRationale: "Simulator dummy data - no real research was done.");
            }
        }
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(slug) ? "business" : slug;
    }

    private static string Capitalise(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Business" : char.ToUpperInvariant(value[0]) + value[1..];
}
