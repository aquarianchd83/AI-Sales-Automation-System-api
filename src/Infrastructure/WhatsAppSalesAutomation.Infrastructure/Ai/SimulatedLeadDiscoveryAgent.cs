using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The no-cost <see cref="ILeadDiscoveryAgent"/>, selected with <c>LeadDiscovery:Agent:Provider =
/// "Simulated"</c> - the same convention as WhatsAppSettings.Provider and AiProviderSettings.Provider. It
/// invents businesses instead of searching the web, so the rest of the pipeline (verification, the
/// qualification rules, duplicate removal, saving the lead and its CRM customer) can be exercised end to end
/// without an API key and without spending anything.
///
/// The invented evidence is built to behave like the real thing: each candidate's phone number and email
/// appear in the text of its "fetched" page, so they pass LeadQualification's verification exactly as a real
/// one would. Two deliberately flawed candidates are added to every round that asks for three or more - one
/// whose phone is on no page, one permanently closed - so the rejection paths show up in the run summary too.
///
/// Everything it produces is marked: names start with "[Simulated]", and websites and emails use the
/// reserved <c>.example</c> domain, which cannot exist on the real internet. That makes the rows easy to spot
/// in the CRM and easy to delete afterwards. A random tag per run keeps repeated test runs from being thrown
/// out as duplicates of each other.
/// </summary>
public class SimulatedLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private const string Marker = "[Simulated]";

    /// <summary>Recorded as the run's model so a simulated run is unmistakable in the spend history.</summary>
    public const string ModelName = "Simulated";

    private readonly ILogger<SimulatedLeadDiscoveryAgent> _logger;

    public SimulatedLeadDiscoveryAgent(ILogger<SimulatedLeadDiscoveryAgent> logger)
    {
        _logger = logger;
    }

    public Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        var tag = Random.Shared.Next(1000, 9999).ToString();
        var location = request.Locations.FirstOrDefault() ?? "Chandigarh";
        var city = location.Split(',')[0].Trim();

        var candidates = new List<DiscoveredBusinessCandidate>();
        var pages = new List<FetchedPage>();
        var seenUrls = new List<string> { $"https://search.example/?q={Uri.EscapeDataString(request.TargetBusinessType)}" };

        for (var i = 1; i <= Math.Max(1, request.MaxCandidates); i++)
            candidates.Add(Invent(request, tag, city, i, pages, seenUrls));

        if (request.MaxCandidates >= 3)
        {
            candidates.Add(WithoutVerifiablePhone(request, tag, city, seenUrls));
            candidates.Add(PermanentlyClosed(request, tag, city, pages, seenUrls));
        }

        _logger.LogInformation(
            "Simulated lead discovery produced {Count} candidates for {BusinessType} in {Location} - no API call was made",
            candidates.Count, request.TargetBusinessType, location);

        return Task.FromResult(new LeadDiscoveryAgentResult(
            IsConfigured: true,
            NotConfiguredReason: null,
            Candidates: candidates,
            Evidence: new LeadDiscoveryEvidence(seenUrls, pages),
            Usage: LeadDiscoveryUsage.None,
            Model: ModelName));
    }

    private static DiscoveredBusinessCandidate Invent(
        LeadDiscoveryAgentRequest request, string tag, string city, int index, List<FetchedPage> pages, List<string> seenUrls)
    {
        var slug = $"sim-{tag}-{index}";
        var name = $"{Marker} {Capitalise(request.TargetBusinessType)} {tag}-{index}";
        var website = $"https://{slug}.example";
        var contactUrl = $"{website}/contact";
        var email = $"hello@{slug}.example";
        var phone = $"+91 9{Random.Shared.Next(10_000_000, 99_999_999)}{index % 10}";

        pages.Add(new FetchedPage(contactUrl,
            $"{name}\n{request.TargetBusinessType} in {city}.\nCall {phone} or write to {email}.\nAddress: {index * 7} Example Road, {city}."));
        seenUrls.Add(contactUrl);
        seenUrls.Add(website);

        return new DiscoveredBusinessCandidate(
            name,
            Capitalise(request.TargetBusinessType),
            $"Sim Owner {index}",
            $"{index * 7} Example Road, {city}",
            city,
            null,
            phone,
            contactUrl,
            email,
            website,
            contactUrl,
            IsIndependentBusiness: true,
            IsPermanentlyClosed: false,
            // Comfortably above a default MinimumLeadScore of 60, and varied so ordering is visible.
            LeadScore: 70 + (index * 3 % 25),
            ScoreRationale: "Simulated candidate - no real research was done.");
    }

    /// <summary>A candidate whose phone appears on no fetched page: dropped when PhoneRequired, and saved
    /// without a phone otherwise.</summary>
    private static DiscoveredBusinessCandidate WithoutVerifiablePhone(
        LeadDiscoveryAgentRequest request, string tag, string city, List<string> seenUrls)
    {
        var url = $"https://sim-{tag}-unverified.example/listing";
        seenUrls.Add(url);

        return new DiscoveredBusinessCandidate(
            $"{Marker} Unverified Phone {tag}",
            Capitalise(request.TargetBusinessType),
            null, null, city, null,
            "+91 90000 00000",
            null, null, null,
            url,
            IsIndependentBusiness: true,
            IsPermanentlyClosed: false,
            LeadScore: 75,
            ScoreRationale: "Simulated candidate whose phone number is on no fetched page.");
    }

    private static DiscoveredBusinessCandidate PermanentlyClosed(
        LeadDiscoveryAgentRequest request, string tag, string city, List<FetchedPage> pages, List<string> seenUrls)
    {
        var url = $"https://sim-{tag}-closed.example/contact";
        var phone = "+91 98765 43210";

        pages.Add(new FetchedPage(url, $"This business has permanently closed. Old number: {phone}."));
        seenUrls.Add(url);

        return new DiscoveredBusinessCandidate(
            $"{Marker} Closed Business {tag}",
            Capitalise(request.TargetBusinessType),
            null, null, city, null,
            phone,
            url, null, null,
            url,
            IsIndependentBusiness: true,
            IsPermanentlyClosed: true,
            LeadScore: 80,
            ScoreRationale: "Simulated candidate that is permanently closed.");
    }

    private static string Capitalise(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Business" : char.ToUpperInvariant(value[0]) + value[1..];
}
