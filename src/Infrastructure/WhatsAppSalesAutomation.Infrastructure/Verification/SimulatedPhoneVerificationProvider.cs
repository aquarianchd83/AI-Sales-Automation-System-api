using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Verification;

/// <summary>
/// The no-cost <see cref="IPhoneVerificationProvider"/> until a real provider is wired in. Deterministic, so a number gets the
/// same answer every time: one whose digits add up to a multiple of four is rejected as being on a DND register (about one in
/// four), everything else passes. Nothing is sent anywhere.
/// </summary>
public class SimulatedPhoneVerificationProvider : IPhoneVerificationProvider
{
    public const string DndReasonCode = "DND";

    public Task<PhoneVerificationResponse> VerifyAsync(
        Guid tenantId, IReadOnlyList<PhoneVerificationItem> items, CancellationToken cancellationToken = default)
    {
        var results = items.Select(item => IsOnDndRegister(item.PhoneE164)
                ? new PhoneVerificationItemResult(item.ReferenceId, LeadDiscoveryVerificationStatus.Fail, DndReasonCode,
                    "Number is registered on the Do Not Disturb list (simulated).")
                : new PhoneVerificationItemResult(item.ReferenceId, LeadDiscoveryVerificationStatus.Pass, null, null))
            .ToList();

        return Task.FromResult(new PhoneVerificationResponse($"sim-{Guid.NewGuid():N}", results));
    }

    internal static bool IsOnDndRegister(string phone) => phone.Where(char.IsDigit).Sum(c => c - '0') % 4 == 0;
}
