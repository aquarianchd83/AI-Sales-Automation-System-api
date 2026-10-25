using System.Collections.Concurrent;
using WhatsAppSalesAutomation.Application.Consent;

namespace WhatsAppSalesAutomation.Infrastructure.Sms;

/// <inheritdoc />
public sealed class SimulatedSmsOutbox : ISimulatedSmsOutbox
{
    private const int MaxPerTenant = 200;

    private readonly ConcurrentDictionary<Guid, List<SimulatedSms>> _byTenant = new();

    public void Add(Guid tenantId, string toPhoneE164, string body, DateTime sentAtUtc)
    {
        var list = _byTenant.GetOrAdd(tenantId, _ => new List<SimulatedSms>());
        lock (list)
        {
            list.Insert(0, new SimulatedSms(sentAtUtc, toPhoneE164, body));
            if (list.Count > MaxPerTenant)
                list.RemoveRange(MaxPerTenant, list.Count - MaxPerTenant);
        }
    }

    public IReadOnlyList<SimulatedSms> List(Guid tenantId)
    {
        if (!_byTenant.TryGetValue(tenantId, out var list))
            return Array.Empty<SimulatedSms>();
        lock (list)
            return list.ToArray();
    }
}
