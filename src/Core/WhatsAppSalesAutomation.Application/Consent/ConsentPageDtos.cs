namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>One thing wrong or missing on the consent page configuration, tied to the field to fix.</summary>
public record ConsentPageIssueDto(string Field, string Message);

/// <param name="Status">Incomplete, Complete, Published or Inactive - see <see cref="ConsentPageStatuses"/>.</param>
/// <param name="DefaultConsentStatement">The wording used when the tenant leaves the statement blank, with [Business Name] and [Product/Service] filled in.</param>
public record ConsentPageSettingsDto(
    string? BusinessName,
    string? ProductDescription,
    string? AdditionalInfo,
    string? ConsentStatement,
    string DefaultConsentStatement,
    string? PrivacyPolicyUrl,
    string? TermsUrl,
    string Status,
    bool IsActive,
    int PublishedVersion,
    DateTime? PublishedAtUtc,
    bool HasUnpublishedChanges,
    IReadOnlyList<ConsentPageIssueDto> Issues);

public record SaveConsentPageSettingsRequest(
    string? BusinessName,
    string? ProductDescription,
    string? AdditionalInfo,
    string? ConsentStatement,
    string? PrivacyPolicyUrl,
    string? TermsUrl);

/// <summary>What the customer sees. The statement is already resolved to the exact sentence shown beside the checkbox.</summary>
public record ConsentPageContentDto(
    string BusinessName,
    string ProductDescription,
    string? AdditionalInfo,
    string ConsentStatement,
    string? PrivacyPolicyUrl,
    string? TermsUrl,
    int Version);

/// <summary>The draft as the customer would see it. Nothing is stored and no link exists - previewing never reaches a customer.</summary>
public record ConsentPagePreviewDto(ConsentPageContentDto Page, IReadOnlyList<ConsentPageIssueDto> Issues);

public static class ConsentPageStatuses
{
    /// <summary>Required content is missing or invalid, and nothing is live.</summary>
    public const string Incomplete = "Incomplete";

    /// <summary>The draft is valid but has never been published.</summary>
    public const string Complete = "Complete";

    /// <summary>Live: customers get the published version.</summary>
    public const string Published = "Published";

    /// <summary>Was published, now switched off.</summary>
    public const string Inactive = "Inactive";
}

/// <summary>What a customer's link shows. <see cref="State"/> is one of <see cref="PublicConsentStates"/>.</summary>
public record PublicConsentPageDto(string State, ConsentPageContentDto? Page, string? Decision, string? Message);

public static class PublicConsentStates
{
    /// <summary>Show the page and let the customer choose.</summary>
    public const string Ready = "Ready";

    /// <summary>The customer already chose; <see cref="PublicConsentPageDto.Decision"/> says what.</summary>
    public const string Answered = "Answered";

    /// <summary>The business has taken the page down for now. A decline is still accepted.</summary>
    public const string Unavailable = "Unavailable";

    public const string Expired = "Expired";

    /// <summary>Unknown, malformed or revoked link. Deliberately says nothing about why.</summary>
    public const string Invalid = "Invalid";
}

/// <param name="Decision">"agree" or "decline".</param>
/// <param name="ConsentChecked">The checkbox. Required for "agree"; ignored for "decline".</param>
/// <param name="PageVersion">The version the customer was looking at, from the page they loaded. An agreement to a page that has since changed is refused.</param>
public record ConsentDecisionRequest(string Decision, bool ConsentChecked, int? PageVersion);

public record ConsentClientInfo(string? IpAddress, string? UserAgent);

/// <param name="Outcome">Recorded, AlreadyRecorded, Conflict, Expired, Invalid, Unavailable, PageChanged or NotAllowed - see <see cref="ConsentDecisionOutcomes"/>.</param>
public record ConsentDecisionResultDto(string Outcome, string? Decision, string Message, PublicConsentPageDto? Page = null);

public static class ConsentDecisionOutcomes
{
    public const string Recorded = "Recorded";
    public const string AlreadyRecorded = "AlreadyRecorded";
    public const string Conflict = "Conflict";
    public const string Expired = "Expired";
    public const string Invalid = "Invalid";
    public const string Unavailable = "Unavailable";
    public const string PageChanged = "PageChanged";
    public const string NotAllowed = "NotAllowed";
    public const string BadRequest = "BadRequest";
}
