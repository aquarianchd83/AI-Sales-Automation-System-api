using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

// A QR-bridge tenant has no Meta template review, so its templates must be sendable while Pending; a Meta tenant still needs Approved.
public class TemplateSendPolicyTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private sealed class Snapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public Snapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    private sealed class FakeTenant : ITenantContext
    {
        public Guid? TenantId => Tenant;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private static TemplateSendPolicy PolicyFor(string provider) => new(
        new FakeTenant(),
        new Snapshot<WhatsAppProviderOptions>(new WhatsAppProviderOptions { Active = provider }),
        new Snapshot<WhatsAppBridgeOptions>(new WhatsAppBridgeOptions()));

    private static MessageTemplate Template(WhatsAppTemplateStatus status, bool active = true) =>
        new() { WhatsAppTemplateStatus = status, IsActive = active };

    [Fact]
    public void A_QR_tenant_can_send_a_pending_template()
        => Assert.True(PolicyFor("QrBridge").IsSendable(Template(WhatsAppTemplateStatus.Pending)));

    [Fact]
    public void A_QR_tenant_still_cannot_send_an_inactive_template()
        => Assert.False(PolicyFor("QrBridge").IsSendable(Template(WhatsAppTemplateStatus.Approved, active: false)));

    [Fact]
    public void A_Meta_tenant_needs_an_approved_template()
    {
        Assert.False(PolicyFor("Meta").IsSendable(Template(WhatsAppTemplateStatus.Pending)));
        Assert.True(PolicyFor("Meta").IsSendable(Template(WhatsAppTemplateStatus.Approved)));
    }

    [Fact]
    public void Without_a_policy_the_Meta_rule_applies()
    {
        ITemplateSendPolicy? none = null;
        Assert.False(none.CanSend(Template(WhatsAppTemplateStatus.Pending)));
        Assert.True(none.CanSend(Template(WhatsAppTemplateStatus.Approved)));
    }
}
