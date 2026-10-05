using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Setup;

/// <summary>
/// A tenant's plan-driven applications: pick a plan, answer exactly the questions that plan's pinned version asks,
/// review, confirm, run. The questions come from data (see <see cref="ISetupAdminService"/>), never from the UI.
/// </summary>
public interface IApplicationSetupService
{
    /// <summary>The plans a new application can be started on - active, with a published setup version.</summary>
    Task<IReadOnlyList<AvailablePlanDto>> GetAvailablePlansAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationDto>> GetApplicationsAsync(CancellationToken cancellationToken = default);

    Task<ApplicationDto> GetApplicationAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Starts an application on the plan's CURRENT published version and pins it there.</summary>
    Task<ApplicationDto> CreateAsync(CreateApplicationRequest request, CancellationToken cancellationToken = default);

    /// <summary>The wizard payload: the definition for the pinned version, the answers so far and how complete they are.</summary>
    Task<ApplicationSetupDto> GetSetupAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Saves the answers sent (partial), recomputes the setup status and - when <c>Complete</c> is set and
    /// everything is valid - confirms the setup.</summary>
    Task<SaveSetupResultDto> SaveSetupAsync(Guid id, SaveSetupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Moves the application to another plan, keeping every answer that still applies.</summary>
    Task<ApplicationSetupDto> ChangePlanAsync(Guid id, ChangePlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Explicitly moves the application to the plan's newest published version.</summary>
    Task<ApplicationSetupDto> MigrateToLatestVersionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Runs the application with a frozen copy of its setup. Throws <c>SetupIncompleteException</c> - and
    /// runs nothing - unless the setup is complete and valid right now.</summary>
    Task<ApplicationExecutionDto> ExecuteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationExecutionDto>> GetExecutionsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Who changed which answer, when, from what to what, under which plan version. Newest first.</summary>
    Task<PagedResult<SetupAuditEntryDto>> GetAuditAsync(Guid id, PagedRequest request, CancellationToken cancellationToken = default);
}
