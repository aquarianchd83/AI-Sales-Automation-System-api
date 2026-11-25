using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Simulator;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The no-cost <see cref="ILeadDiscoveryAgent"/>, selected with <c>LeadDiscovery:Agent:Provider =
/// "Simulated"</c> - the same convention as WhatsAppSettings.Provider and AiProviderSettings.Provider. It
/// searches nothing: the businesses it hands back are the lead businesses on the platform admin's Simulator dummy data page
/// (<see cref="ISimulatorDataProvider"/>), so the rest of the pipeline (verification, the qualification rules, duplicate removal,
/// saving the lead and its CRM customer) can be exercised end to end without an API key, on data the admin controls.
///
/// The evidence is built to behave like the real thing: a qualifying business's phone and email appear in the text of its "fetched"
/// page, so they pass LeadQualification's verification exactly as a real one would. A flawed business (phone on no page, or permanently
/// closed) is rejected the way a real one would be. The share of flawed ones in a run is the FlawedSharePercent setting (30% to start,
/// only when three or more are asked for), taken in turn from the flawed rows.
///
/// Everything it produces is marked: names start with "[Simulated]". With the UniquePerRun setting on (the default) a random run tag is
/// added to each name and website and the last four digits of each phone are new, so repeated test runs are not thrown out as duplicates
/// of each other; with it off, the data is used exactly as written and a business already in the CRM comes back as a duplicate.
/// </summary>
public class SimulatedLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private const string Marker = "[Simulated]";

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
        var unique = data.Flag(SimulatorDataCatalog.Settings.UniquePerRun);
        var flawedPercent = data.Number(SimulatorDataCatalog.Settings.FlawedSharePercent, 0, 90);

        var tag = Random.Shared.Next(1000, 9999).ToString();
        var location = request.Locations.FirstOrDefault() ?? "Chandigarh";
        var city = location.Split(',')[0].Trim();

        // The kind of business asked for comes first; the others follow, so a run is never empty just because no row has that kind.
        var ordered = data.Businesses
            .OrderBy(b => Matches(b, request.TargetBusinessType) ? 0 : 1)
            .ToList();
        var good = ordered.Where(b => b.Variant == SimulatorLeadVariant.Qualified).ToList();
        var bad = ordered.Where(b => b.Variant != SimulatorLeadVariant.Qualified).ToList();

        var candidates = new List<DiscoveredBusinessCandidate>();
        var pages = new List<FetchedPage>();
        var seenUrls = new List<string> { $"https://search.example/?q={Uri.EscapeDataString(request.TargetBusinessType)}" };

        var asked = Math.Max(1, request.MaxCandidates);
        var flawed = asked >= 3 && bad.Count > 0 && flawedPercent > 0
            ? Math.Max(1, (int)Math.Round(asked * flawedPercent / 100.0, MidpointRounding.AwayFromZero))
            : 0;
        if (good.Count == 0)
            flawed = bad.Count > 0 ? asked : 0;

        // With UniquePerRun on, the rows are reused in turn (each copy gets its own tag); off, each row is handed out at most once.
        var goodCount = unique ? (good.Count == 0 ? 0 : asked - flawed) : Math.Min(asked - flawed, good.Count);
        var badCount = unique ? flawed : Math.Min(flawed, bad.Count);

        for (var i = 0; i < goodCount; i++)
            candidates.Add(Build(good[i % good.Count], request, unique ? $"{tag}-{i + 1}" : null, city, pages, seenUrls));
        for (var i = 0; i < badCount; i++)
            candidates.Add(Build(bad[i % bad.Count], request, unique ? $"{tag}-x{i + 1}" : null, city, pages, seenUrls));

        _logger.LogInformation(
            "Simulated lead discovery handed back {Count} dummy businesses for {BusinessType} in {Location} ({Available} on the Simulator dummy data page) - no API call was made",
            candidates.Count, request.TargetBusinessType, location, data.Businesses.Count);

        return new LeadDiscoveryAgentResult(
            IsConfigured: true,
            NotConfiguredReason: null,
            Candidates: candidates,
            Evidence: new LeadDiscoveryEvidence(seenUrls, pages),
            Usage: LeadDiscoveryUsage.None,
            Model: ModelName);
    }

    private static bool Matches(SimulatorLeadBusiness business, string target) =>
        !string.IsNullOrWhiteSpace(business.BusinessType) && !string.IsNullOrWhiteSpace(target)
        && (business.BusinessType.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase)
            || target.Contains(business.BusinessType.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DiscoveredBusinessCandidate Build(
        SimulatorLeadBusiness b, LeadDiscoveryAgentRequest request, string? runTag, string requestCity, List<FetchedPage> pages, List<string> seenUrls)
    {
        var name = $"{Marker} {b.Name}{(runTag is null ? "" : " " + runTag)}";
        var businessType = string.IsNullOrWhiteSpace(b.BusinessType) ? Capitalise(request.TargetBusinessType) : b.BusinessType!;
        var city = string.IsNullOrWhiteSpace(b.City) ? requestCity : b.City!;
        var address = string.IsNullOrWhiteSpace(b.Address) ? null : $"{b.Address}, {city}";
        var phone = runTag is null ? b.Phone : FreshTail(b.Phone);
        var website = Tagged(b.Website, runTag);
        var email = TaggedEmail(b.Email, runTag);
        var slug = Slug(b.Name) + (runTag is null ? "" : "-" + runTag);

        switch (b.Variant)
        {
            case SimulatorLeadVariant.PhoneNotOnPage:
            {
                // The listing is seen but never fetched, so the phone is on no page.
                var url = (website ?? $"https://{slug}.example").TrimEnd('/') + "/listing";
                seenUrls.Add(url);
                return new DiscoveredBusinessCandidate(name, businessType, b.ContactPerson, address, city, null, phone, null, null, null, url,
                    IsIndependentBusiness: true, IsPermanentlyClosed: false, LeadScore: b.LeadScore,
                    ScoreRationale: "Simulated (dummy data) - its phone number is on no fetched page.");
            }
            case SimulatorLeadVariant.PermanentlyClosed:
            {
                var url = (website ?? $"https://{slug}.example").TrimEnd('/') + "/contact";
                pages.Add(new FetchedPage(url, $"{name}\nThis business has permanently closed. Old number: {phone}."));
                seenUrls.Add(url);
                return new DiscoveredBusinessCandidate(name, businessType, b.ContactPerson, address, city, null, phone, url, null, null, url,
                    IsIndependentBusiness: true, IsPermanentlyClosed: true, LeadScore: b.LeadScore,
                    ScoreRationale: "Simulated (dummy data) - the business is permanently closed.");
            }
            default:
            {
                var site = website ?? $"https://{slug}.example";
                var contactUrl = site.TrimEnd('/') + "/contact";
                var text = $"{name}\n{businessType} in {city}.\nCall {phone}" + (email is null ? "." : $" or write to {email}.")
                           + (address is null ? "" : $"\nAddress: {address}.");
                pages.Add(new FetchedPage(contactUrl, text));
                seenUrls.Add(contactUrl);
                seenUrls.Add(site);
                return new DiscoveredBusinessCandidate(name, businessType, b.ContactPerson, address, city, null, phone, contactUrl, email, site, contactUrl,
                    IsIndependentBusiness: true, IsPermanentlyClosed: false, LeadScore: b.LeadScore,
                    ScoreRationale: "Simulated (dummy data) - no real research was done.");
            }
        }
    }

    /// <summary>The same number with new last four digits, so a rerun's copy is not a duplicate of the last one.</summary>
    private static string? FreshTail(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return phone;

        var chars = phone.ToCharArray();
        var replaced = 0;
        for (var i = chars.Length - 1; i >= 0 && replaced < 4; i--)
        {
            if (!char.IsDigit(chars[i])) continue;
            chars[i] = (char)('0' + Random.Shared.Next(10));
            replaced++;
        }

        return new string(chars);
    }

    /// <summary>https://name.example becomes https://name-1234-1.example.</summary>
    private static string? Tagged(string? website, string? runTag)
    {
        if (string.IsNullOrWhiteSpace(website) || runTag is null)
            return string.IsNullOrWhiteSpace(website) ? null : website.Trim();
        if (!Uri.TryCreate(website.Trim(), UriKind.Absolute, out var uri))
            return website.Trim();

        var host = uri.Host;
        var dot = host.IndexOf('.');
        var tagged = dot > 0 ? $"{host[..dot]}-{runTag}{host[dot..]}" : $"{host}-{runTag}";
        return new UriBuilder(uri) { Host = tagged }.Uri.ToString().TrimEnd('/');
    }

    private static string? TaggedEmail(string? email, string? runTag)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var at = email.IndexOf('@');
        if (runTag is null || at <= 0)
            return email.Trim();
        var domain = email[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 ? $"{email[..at]}@{domain[..dot]}-{runTag}{domain[dot..]}" : email.Trim();
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(slug) ? "sim" : slug;
    }

    private static string Capitalise(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Business" : char.ToUpperInvariant(value[0]) + value[1..];
}
