using System.Text.Json.Serialization;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.MetaOnboarding;

/// <summary>The onboarding steps, in order. Names are the wire values the screen keys off.</summary>
public static class MetaSignupSteps
{
    public const string Authorization = "authorization";
    public const string Assets = "assets";
    public const string Credentials = "credentials";
    public const string Registration = "registration";
    public const string Webhook = "webhook";
    public const string Templates = "templates";
    public const string Verification = "verification";

    public static readonly IReadOnlyList<(string Key, string Title)> All = new[]
    {
        (Authorization, "Meta authorization"),
        (Assets, "WhatsApp business account and number"),
        (Credentials, "Secure connection"),
        (Registration, "Number registration"),
        (Webhook, "Message notifications"),
        (Templates, "Message templates"),
        (Verification, "Final check"),
    };
}

/// <summary>Where one step stands. Matches the statuses the tenant sees: Not Started, In Progress, Action Required, Failed, Completed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetaSignupStepStatus
{
    NotStarted,
    InProgress,
    ActionRequired,
    Failed,
    Completed,
}

/// <summary>What the tenant can do next about a blocked step. The screen shows one button per action.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetaIssueAction
{
    /// <summary>Nothing to do (or nothing the tenant can do) - the message explains.</summary>
    None,
    /// <summary>Try the same step again - for temporary problems.</summary>
    Retry,
    /// <summary>Run the Meta sign-in again to grant access or renew it.</summary>
    Reconnect,
    /// <summary>Finish a verification in the tenant's own Meta Business account, then retry.</summary>
    Verify,
    /// <summary>Only the platform can fix this.</summary>
    ContactSupport,
    /// <summary>Top up the prepaid wallet.</summary>
    AddCredits,
}

/// <summary>
/// A blocked or failed step, explained in plain words. Never carries Meta's raw error, a token or any other secret:
/// the technical detail goes to the log, and <see cref="Reference"/> (Meta's trace id, or ours) ties the two together
/// so support can find it when the tenant quotes it.
/// </summary>
public record MetaIssueDto(
    string Code,
    string Step,
    string Title,
    string Message,
    MetaIssueAction PrimaryAction,
    MetaIssueAction? SecondaryAction,
    bool Retryable,
    string? Reference);

public record MetaSignupStepDto(string Key, string Title, MetaSignupStepStatus Status, string? Detail);

public record MetaTemplateSummaryDto(int Total, int Approved, int Pending, int Rejected);

/// <summary>Everything the screen needs to launch Meta's Embedded Signup. Nothing here is a secret (the App ID and the
/// configuration ID appear in the browser by design).</summary>
public record MetaSignupClientConfigDto(
    bool Enabled,
    string? AppId,
    string? ConfigurationId,
    string ApiVersion,
    MetaIssueDto? Issue);

/// <summary>
/// What the browser reports after Meta's popup closes. <see cref="Code"/> is the one-time authorization code from
/// FB.login; <see cref="WabaId"/>/<see cref="PhoneNumberId"/> come from Meta's WA_EMBEDDED_SIGNUP message (the backend
/// looks them up itself when they are missing). <see cref="ClientEvent"/> is "FINISH", "CANCEL" or "ERROR".
/// None of this is trusted on its own - the code is exchanged server-side and the assets are re-read with the token it yields.
/// </summary>
public record CompleteMetaSignupRequest(
    string? Code,
    string? WabaId,
    string? PhoneNumberId,
    string? ClientEvent,
    string? ClientStep,
    string? ClientErrorMessage);

public record MetaSignupResultDto(
    string Status,
    IReadOnlyList<MetaSignupStepDto> Steps,
    MetaIssueDto? Issue,
    TenantWhatsAppConfigDto? Config,
    MetaTemplateSummaryDto? Templates)
{
    public const string Completed = "Completed";
    public const string ActionRequired = "ActionRequired";
    public const string Failed = "Failed";
}

/// <summary>
/// "Connect with Meta": WhatsApp Embedded Signup. The tenant signs in to Meta in Meta's own popup; this service then
/// does the rest - exchanges the code, finds the WhatsApp Business Account and number, saves the credentials encrypted
/// against the tenant, registers the number, subscribes this app to the account's webhooks, reads the templates and
/// verifies the result. Every step that cannot continue comes back as a <see cref="MetaIssueDto"/>; steps already done
/// stay done, and <see cref="ResumeAsync"/> carries on from the first one that is not.
/// </summary>
public interface IMetaEmbeddedSignupService
{
    Task<MetaSignupClientConfigDto> GetClientConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>The steps as the saved connection shows them now. Reads only what is stored - no call to Meta.</summary>
    Task<MetaSignupResultDto> GetStatusAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Finishes a sign-in the browser just completed. Never throws for a Meta-side problem: that comes back as an issue.</summary>
    Task<MetaSignupResultDto> CompleteAsync(Guid tenantId, Guid? userId, CompleteMetaSignupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Re-runs the steps after authorization with the stored token (registration, notifications, templates, final check).</summary>
    Task<MetaSignupResultDto> ResumeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
