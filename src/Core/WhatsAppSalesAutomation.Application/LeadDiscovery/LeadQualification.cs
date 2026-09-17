using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>The optional output fields a profile can mark as required, spelled as the output columns are.</summary>
public static class LeadDiscoveryFields
{
    public const string ContactPerson = "ContactPerson";
    public const string Address = "Address";
    public const string City = "City";
    public const string State = "State";
    public const string Phone = "Phone";
    public const string Email = "Email";
    public const string Website = "Website";

    public static readonly IReadOnlyList<string> All = new[] { ContactPerson, Address, City, State, Phone, Email, Website };

    /// <summary>The canonical spelling of a field name given in any case, or null if it is not one.</summary>
    public static string? Canonical(string? name) =>
        All.FirstOrDefault(f => string.Equals(f, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public record QualificationRules(
    bool PhoneRequired,
    bool EmailRequired,
    bool IndependentBusiness,
    int MinimumLeadScore,
    IReadOnlyCollection<string> RequiredFields);

/// <summary>A candidate that passed every rule, carrying only the details that survived verification.</summary>
public record VerifiedLead(
    string BusinessName,
    string BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? State,
    string? Phone,
    string? PhoneSourceUrl,
    string? Email,
    string? Website,
    string SourceUrl,
    int LeadScore,
    string? ScoreRationale)
{
    public string? PhoneE164 => PhoneNumberNormalizer.TryNormalize(Phone, out var e164) ? e164 : null;

    public string? PhoneKey => LeadQualification.PhoneKey(Phone);

    public string? WebsiteKey => LeadQualification.WebsiteKey(Website);

    public string NameKey => LeadQualification.NameKey(BusinessName, City);
}

public record LeadAssessment(VerifiedLead? Lead, string? RejectionReason);

/// <summary>
/// Decides which of the agent's candidates are qualified leads. Pure functions over the candidates and the
/// evidence the agent gathered - no I/O - so every rule here holds regardless of what the model did:
///
/// - A phone number is kept only if the same number appears in the text of a page that was fetched during
///   the run. When the profile requires a phone, a candidate without a verified one is rejected; otherwise
///   the unverified number is dropped. Emails are treated the same way.
/// - The source URL has to be one the run actually searched or fetched, so a made-up page cannot back a
///   lead.
/// - A website is kept only if its host was seen during the run or is mentioned on a fetched page.
/// - Closed businesses, missing required fields, the independent-business rule and the minimum score all
///   reject the candidate.
///
/// Duplicates against existing data need the database, so LeadDiscoveryRunService does that part using the
/// keys defined here.
/// </summary>
public static class LeadQualification
{
    /// <summary>Numbers as commonly written: optional +, then digits broken up by spaces, dots, dashes or
    /// brackets. Two numbers separated only by spaces come out as one run; the digit groups inside it are
    /// what gets compared.</summary>
    private static readonly Regex PhoneLikeRun = new(@"\+?\d[\d\s().\-]{5,40}\d", RegexOptions.Compiled);

    private static readonly Regex DigitGroup = new(@"\d+", RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled);

    private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    /// <summary>Comparing the trailing digits lets "+91 98200 98200", "098200-98200" and "9820098200" match
    /// each other. Ten covers a national number in most numbering plans.</summary>
    private const int PhoneKeyLength = 10;

    private const int MinPhoneDigits = 7;
    private const int MaxPhoneDigits = 15;

    /// <summary>How many digits a number on the page may carry in front of the candidate's - a country code
    /// of up to three digits plus a trunk 0.</summary>
    private const int MaxExtraLeadingDigits = 4;

    /// <summary>Values a model writes instead of null.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "none", "n/a", "na", "unknown", "not available", "not found", "-"
    };

    public static IReadOnlyList<LeadAssessment> AssessAll(
        IEnumerable<DiscoveredBusinessCandidate> candidates,
        LeadDiscoveryEvidence evidence,
        QualificationRules rules)
    {
        var index = new EvidenceIndex(evidence);
        return candidates.Select(candidate => Assess(candidate, index, rules)).ToList();
    }

    private static LeadAssessment Assess(DiscoveredBusinessCandidate candidate, EvidenceIndex evidence, QualificationRules rules)
    {
        var businessName = Clean(candidate.BusinessName);
        if (businessName is null)
            return Rejected("no business name");

        if (candidate.IsPermanentlyClosed == true)
            return Rejected("closed business");

        var sourceUrl = Clean(candidate.SourceUrl);
        if (sourceUrl is null || !evidence.HasSeen(sourceUrl))
            return Rejected("source URL was not visited");

        var (phone, phoneSourceUrl) = evidence.VerifyPhone(Clean(candidate.Phone), Clean(candidate.PhoneSourceUrl));
        if (phone is null && (rules.PhoneRequired || rules.RequiredFields.Contains(LeadDiscoveryFields.Phone)))
            return Rejected("phone not publicly verifiable");

        var email = evidence.VerifyEmail(Clean(candidate.Email));
        if (email is null && (rules.EmailRequired || rules.RequiredFields.Contains(LeadDiscoveryFields.Email)))
            return Rejected("email not publicly verifiable");

        var lead = new VerifiedLead(
            businessName,
            Clean(candidate.BusinessType) ?? string.Empty,
            Clean(candidate.ContactPerson),
            Clean(candidate.Address),
            Clean(candidate.City),
            Clean(candidate.State),
            phone,
            phoneSourceUrl,
            email,
            evidence.VerifyWebsite(Clean(candidate.Website)),
            sourceUrl,
            Math.Clamp(candidate.LeadScore, 0, 100),
            Clean(candidate.ScoreRationale));

        var missingField = rules.RequiredFields.FirstOrDefault(field => ValueOf(lead, field) is null);
        if (missingField is not null)
            return Rejected($"missing required {missingField}");

        if (rules.IndependentBusiness && candidate.IsIndependentBusiness != true)
            return Rejected("not confirmed independent");

        if (lead.LeadScore < rules.MinimumLeadScore)
            return Rejected("below minimum lead score");

        return new LeadAssessment(lead, null);
    }

    public static string? PhoneKey(string? phone)
    {
        var digits = Digits(phone);
        return digits.Length is < MinPhoneDigits or > MaxPhoneDigits
            ? null
            : digits[Math.Max(0, digits.Length - PhoneKeyLength)..];
    }

    /// <summary>Host without "www." plus path, lower-cased, no query or trailing slash. Keeping the path
    /// means two businesses with pages on the same shared platform (a social network, a directory) are not
    /// mistaken for one another.</summary>
    public static string? WebsiteKey(string? url) =>
        TryParseHttpUrl(url, out var uri) ? $"{HostOf(uri)}{uri.AbsolutePath.TrimEnd('/')}".ToLowerInvariant() : null;

    public static string NameKey(string businessName, string? city) =>
        $"{Squash(businessName)}|{Squash(city)}";

    private static string? ValueOf(VerifiedLead lead, string field) => field switch
    {
        LeadDiscoveryFields.ContactPerson => lead.ContactPerson,
        LeadDiscoveryFields.Address => lead.Address,
        LeadDiscoveryFields.City => lead.City,
        LeadDiscoveryFields.State => lead.State,
        LeadDiscoveryFields.Phone => lead.Phone,
        LeadDiscoveryFields.Email => lead.Email,
        LeadDiscoveryFields.Website => lead.Website,
        _ => null
    };

    private static LeadAssessment Rejected(string reason) => new(null, reason);

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || Placeholders.Contains(trimmed) ? null : trimmed;
    }

    private static string Digits(string? value) =>
        value is null ? string.Empty : new string(value.Where(char.IsAsciiDigit).ToArray());

    private static string Squash(string? value) =>
        NonAlphanumeric.Replace((value ?? string.Empty).ToLowerInvariant(), " ").Trim();

    /// <summary>Comparable form of a URL: host without "www.", path without a trailing slash, query kept (a
    /// listing page is often identified by it), fragment dropped.</summary>
    private static string? NormalizeUrl(string? url) =>
        TryParseHttpUrl(url, out var uri) ? $"{HostOf(uri)}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}" : null;

    private static bool TryParseHttpUrl(string? url, out Uri uri) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri!)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string HostOf(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>
    /// Whether a run of digit groups on a page contains the candidate's number. Any consecutive span of
    /// groups counts (so "+91 98200 98200" contains "98200 98200"), when its digits are the candidate's in
    /// full, or its last <see cref="PhoneKeyLength"/> digits, or those preceded by a country code or trunk
    /// prefix that the page itself marks as one - a leading "+" or "0". Extra leading digits without such a
    /// mark are treated as a different, longer number that happens to end the same way (an order or
    /// reference number, say), not as a match.
    /// </summary>
    private static bool ContainsPhone(PhoneRun run, string fullDigits, string key)
    {
        for (var start = 0; start < run.Groups.Count; start++)
        {
            var digits = string.Empty;

            for (var end = start; end < run.Groups.Count && digits.Length < fullDigits.Length + MaxExtraLeadingDigits; end++)
            {
                digits += run.Groups[end];

                if (digits == fullDigits || digits == key)
                    return true;

                var extraDigits = digits.Length - key.Length;
                var prefixMarked = (start == 0 && run.StartsWithPlus) || digits[0] == '0';

                if (extraDigits is > 0 and <= MaxExtraLeadingDigits && prefixMarked && digits.EndsWith(key, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private sealed record PhoneRun(bool StartsWithPlus, IReadOnlyList<string> Groups);

    /// <summary>The evidence arranged for lookups, built once per batch of candidates.</summary>
    private sealed class EvidenceIndex
    {
        private readonly HashSet<string> _seenUrls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenHosts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IndexedPage> _pages = new();

        public EvidenceIndex(LeadDiscoveryEvidence evidence)
        {
            foreach (var url in evidence.SeenUrls.Concat(evidence.FetchedPages.Select(p => p.Url)))
                AddSeen(url);

            foreach (var page in evidence.FetchedPages)
            {
                var phoneRuns = PhoneLikeRun.Matches(page.Text)
                    .Select(m => new PhoneRun(m.Value.StartsWith('+'), DigitGroup.Matches(m.Value).Select(g => g.Value).ToList()))
                    .ToList();

                foreach (Match match in EmailPattern.Matches(page.Text))
                    _emails.Add(match.Value);

                _pages.Add(new IndexedPage(page.Url, NormalizeUrl(page.Url), page.Text, phoneRuns));
            }
        }

        public bool HasSeen(string url) => NormalizeUrl(url) is { } normalized && _seenUrls.Contains(normalized);

        /// <summary>The phone and the fetched page it was found on, checking the page the agent pointed at
        /// first; (null, null) when no fetched page carries the number.</summary>
        public (string? Phone, string? SourceUrl) VerifyPhone(string? phone, string? preferredSourceUrl)
        {
            var key = PhoneKey(phone);
            if (key is null)
                return (null, null);

            var fullDigits = Digits(phone);
            var preferred = NormalizeUrl(preferredSourceUrl);
            var pages = _pages.OrderByDescending(p => preferred is not null && p.NormalizedUrl == preferred);

            foreach (var page in pages)
            {
                if (page.PhoneRuns.Any(run => ContainsPhone(run, fullDigits, key)))
                    return (phone, page.Url);
            }

            return (null, null);
        }

        public string? VerifyEmail(string? email) =>
            email is not null && EmailPattern.Match(email) is { Success: true } match && match.Value.Length == email.Length
            && _emails.Contains(email)
                ? email
                : null;

        public string? VerifyWebsite(string? website)
        {
            if (!TryParseHttpUrl(website, out var uri))
                return null;

            var host = HostOf(uri);
            var mentioned = _seenHosts.Contains(host)
                            || _pages.Any(p => p.Text.Contains(host, StringComparison.OrdinalIgnoreCase));

            return mentioned ? website : null;
        }

        private void AddSeen(string url)
        {
            if (!TryParseHttpUrl(url, out var uri))
                return;

            _seenUrls.Add(NormalizeUrl(url)!);
            _seenHosts.Add(HostOf(uri));
        }

        private sealed record IndexedPage(string Url, string? NormalizedUrl, string Text, IReadOnlyList<PhoneRun> PhoneRuns);
    }
}
