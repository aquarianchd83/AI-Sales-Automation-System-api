using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Simulator;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Verification;

/// <summary>
/// The no-cost <see cref="IPhoneVerificationProvider"/> until a real provider is wired in. Nothing is sent anywhere. A number is rejected as
/// being on a DND register when it is in the DND numbers list on the Simulator dummy data page, or - while the DndDigitSumRule setting is on
/// (the default) - when its digits add up to a multiple of four (about one in four). Deterministic, so a number gets the same answer every time.
/// </summary>
public class SimulatedPhoneVerificationProvider : IPhoneVerificationProvider
{
    public const string DndReasonCode = "DND";

    private readonly ISimulatorDataProvider? _data;

    public SimulatedPhoneVerificationProvider(ISimulatorDataProvider? data = null) => _data = data;

    public async Task<PhoneVerificationResponse> VerifyAsync(
        Guid tenantId, IReadOnlyList<PhoneVerificationItem> items, CancellationToken cancellationToken = default)
    {
        var data = _data is null ? SimulatorDataSnapshot.Defaults : await _data.GetAsync(cancellationToken);
        var digitRule = data.Flag(SimulatorDataCatalog.Settings.DndDigitSumRule);

        var results = items.Select(item => data.IsDndNumber(item.PhoneE164) || (digitRule && IsOnDndRegister(item.PhoneE164))
                ? new PhoneVerificationItemResult(item.ReferenceId, LeadDiscoveryVerificationStatus.Fail, DndReasonCode,
                    "Number is registered on the Do Not Disturb list (simulated).")
                : new PhoneVerificationItemResult(item.ReferenceId, LeadDiscoveryVerificationStatus.Pass, null, null))
            .ToList();

        return new PhoneVerificationResponse($"sim-{Guid.NewGuid():N}", results);
    }

    internal static bool IsOnDndRegister(string phone) => phone.Where(char.IsDigit).Sum(c => c - '0') % 4 == 0;
}
