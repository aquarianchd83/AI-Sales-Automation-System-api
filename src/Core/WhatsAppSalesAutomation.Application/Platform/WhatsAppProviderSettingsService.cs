using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One selectable provider. <paramref name="Available"/> is false while its adapter does not exist yet - it is listed so the
/// console can show what is coming, but cannot be activated.</summary>
public record WhatsAppProviderOptionDto(string Id, string Name, bool Available, string? UnavailableReason);

/// <param name="Active">The provider every tenant send/receive goes through.</param>
public record WhatsAppProviderSettingsDto(string Active, IReadOnlyList<WhatsAppProviderOptionDto> Providers);

public record UpdateWhatsAppProviderRequest(string Provider);

public interface IWhatsAppProviderSettingsService
{
    Task<WhatsAppProviderSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The provider in force right now: what is stored, or Meta when nothing is (every platform from before this setting existed).</summary>
    Task<string> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates, then stores the new active provider. A failure leaves the previous one in force. <see cref="WhatsAppProviderChange.Previous"/>
    /// is what the audit log records alongside the new value.</summary>
    Task<WhatsAppProviderChange> UpdateAsync(UpdateWhatsAppProviderRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

public record WhatsAppProviderChange(string Previous, WhatsAppProviderSettingsDto Settings);

/// <summary>
/// Platform-wide choice of WhatsApp provider, stored in the AppSettings table. This only records the choice - nothing reads it on the send
/// path yet, so changing it is a no-op until the bridge adapter is wired to <c>WhatsAppServiceFactory</c>.
/// </summary>
public class WhatsAppProviderSettingsService : IWhatsAppProviderSettingsService
{
    public const string ActiveKey = "WhatsAppProvider:Active";

    private const string BridgeNotReady = "The QR bridge adapter has not been built yet.";

    private readonly IAppSettingsStore _store;
    private readonly IValidator<UpdateWhatsAppProviderRequest> _validator;

    public WhatsAppProviderSettingsService(IAppSettingsStore store, IValidator<UpdateWhatsAppProviderRequest> validator)
    {
        _store = store;
        _validator = validator;
    }

    public async Task<WhatsAppProviderSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await GetActiveAsync(cancellationToken));

    public async Task<string> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return Normalize(stored.TryGetValue(ActiveKey, out var value) ? value : null);
    }

    public async Task<WhatsAppProviderChange> UpdateAsync(UpdateWhatsAppProviderRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var previous = await GetActiveAsync(cancellationToken);
        var next = Normalize(request.Provider);

        if (next != previous)
            await _store.UpsertAsync(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [ActiveKey] = next }, updatedByUserId, cancellationToken);

        return new WhatsAppProviderChange(previous, ToDto(next));
    }

    /// <summary>Anything unknown or empty means Meta - the only provider that existed before this setting.</summary>
    private static string Normalize(string? value) =>
        string.Equals(value?.Trim(), WhatsAppProviders.QrBridge, StringComparison.OrdinalIgnoreCase) ? WhatsAppProviders.QrBridge : WhatsAppProviders.Meta;

    private static WhatsAppProviderSettingsDto ToDto(string active) => new(active, new[]
    {
        new WhatsAppProviderOptionDto(WhatsAppProviders.Meta, "Meta WhatsApp Cloud API", true, null),
        new WhatsAppProviderOptionDto(WhatsAppProviders.QrBridge, "QR-Code WhatsApp Bridge", false, BridgeNotReady),
    });

    /// <summary>Whether <paramref name="provider"/> can be switched to today.</summary>
    internal static bool IsAvailable(string provider) => provider == WhatsAppProviders.Meta;
}

public class UpdateWhatsAppProviderRequestValidator : AbstractValidator<UpdateWhatsAppProviderRequest>
{
    public UpdateWhatsAppProviderRequestValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty()
            .Must(p => p is WhatsAppProviders.Meta or WhatsAppProviders.QrBridge)
            .WithMessage("Choose \"Meta\" or \"QrBridge\".")
            // Reject, never half-activate: a provider with no adapter would leave every send with nothing to deliver it.
            .Must(p => p != WhatsAppProviders.QrBridge || WhatsAppProviderSettingsService.IsAvailable(p))
            .WithMessage("The QR-Code WhatsApp Bridge is not available yet.");
    }
}
