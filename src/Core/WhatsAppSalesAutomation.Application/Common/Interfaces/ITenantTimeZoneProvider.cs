namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// The tenant-aware counterpart to <see cref="IDateTimeProvider.IstNow"/> - resolves the current
/// wall-clock time in the ambient tenant's own Tenant.Timezone, falling back to
/// Tenancy.TimeZoneCatalog.DefaultId (India Standard Time, this platform's original assumption) when
/// there's no ambient tenant (a PlatformSuperAdmin request has none by design) or the tenant hasn't
/// set one.
///
/// Used wherever Campaign.ScheduledStartAt's "is it due yet" comparison needs to happen in the
/// tenant's own local time instead of a platform-wide fixed offset - see
/// CampaignService.StartAsync/ValidateSendableAsync and
/// CampaignSendService.ProcessInitialSendsAsync's own doc comments, and both CampaignValidators
/// rules. Same "resolve per call, not once per DI scope" shape as ITenantConfigOverrideProvider.
/// </summary>
public interface ITenantTimeZoneProvider
{
    Task<DateTime> GetLocalNowAsync(CancellationToken cancellationToken = default);
}
