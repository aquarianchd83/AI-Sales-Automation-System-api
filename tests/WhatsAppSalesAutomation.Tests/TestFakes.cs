using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Tests;

public sealed class TestClock : IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
    public DateTime IstNow => UtcNow.AddHours(5.5);
}

public sealed class PlatformContext : ITenantContext
{
    public Guid? TenantId => null;
    public bool IsPlatformSuperAdmin => true;
    public void SetTenant(Guid tenantId) { }
}

public sealed class AnonymousUser : ICurrentUserService
{
    public Guid? UserId => null;
    public string? Email => null;
    public IReadOnlyList<string> Roles => Array.Empty<string>();
    public Guid? TenantId => null;
    public Guid? ImpersonatorUserId => null;
}

public sealed class FakeRefundGateway : IRefundGateway
{
    public string Name => "FakeGateway";
    public bool Succeed { get; set; } = true;
    public int Calls { get; private set; }
    public int LastAmountCents { get; private set; }

    public Task<RefundGatewayResult> RefundAsync(Payment payment, int amountCents, decimal localAmount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastAmountCents = amountCents;
        return Task.FromResult(Succeed ? new RefundGatewayResult(true, $"ref_{Calls}", null) : new RefundGatewayResult(false, null, "gateway down"));
    }
}

/// <summary>Records what would have been sent and refuses a repeat of the same (tenant, kind, quota, episode),
/// like the real notifier's unique index.</summary>
public sealed class RecordingNotifier : ITenantNotifier
{
    private readonly HashSet<string> _seen = new();

    public List<TenantNotificationRequest> Sent { get; } = new();

    public Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default)
    {
        var key = $"{request.TenantId}|{request.Kind}|{request.QuotaType}|{request.EpisodeKey}";
        if (!_seen.Add(key))
            return Task.FromResult(false);

        Sent.Add(request);
        return Task.FromResult(true);
    }
}

public sealed class FakeEmail : IEmailSender
{
    public bool Succeed { get; set; } = true;
    public List<(string To, string Subject)> Sent { get; } = new();

    public Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        Sent.Add((toEmail, subject));
        return Task.FromResult(Succeed ? new DeliveryResult(true) : new DeliveryResult(false, "smtp refused"));
    }
}

public sealed class FakePlatformWhatsApp : IPlatformWhatsAppSender
{
    public bool Configured { get; set; } = true;
    public List<(string To, IReadOnlyList<string> Parameters)> Sent { get; } = new();

    public Task<DeliveryResult> SendTemplateAsync(string toPhoneE164, string templateName, string languageCode, IReadOnlyList<string> parameters, CancellationToken cancellationToken = default)
    {
        if (!Configured)
            return Task.FromResult(new DeliveryResult(false, "not configured", Skipped: true));

        Sent.Add((toPhoneE164, parameters));
        return Task.FromResult(new DeliveryResult(true));
    }
}

/// <summary>An interface stub whose every call goes to one handler - enough to stand in for a collaborator the
/// code under test only touches once or twice, without hand-writing all of its members.</summary>
public class ProxyStub : System.Reflection.DispatchProxy
{
    public Func<System.Reflection.MethodInfo, object?[]?, object?> Handler { get; set; } = (m, _) => throw new NotImplementedException(m.Name);

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}

public static class Fake
{
    public static T Of<T>(Func<System.Reflection.MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, ProxyStub>();
        ((ProxyStub)(object)proxy).Handler = handler;
        return proxy;
    }
}

/// <summary>An IOptionsSnapshot over fixed values - what a test hands a service that reads live configuration.</summary>
public sealed class FixedOptions<T> : Microsoft.Extensions.Options.IOptionsSnapshot<T> where T : class
{
    public FixedOptions(T value) => Value = value;

    public T Value { get; }

    public T Get(string? name) => Value;
}
