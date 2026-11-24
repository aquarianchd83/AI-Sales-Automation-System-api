using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class WhatsAppSessionAlertJobTests
{
    [Theory]
    [InlineData(BridgeStatuses.Disconnected, true)]
    [InlineData(BridgeStatuses.ConnectionFailed, true)]
    [InlineData(BridgeStatuses.Connected, false)]
    [InlineData(BridgeStatuses.NotConnected, false)]
    [InlineData(BridgeStatuses.QrCodeAvailable, false)]
    [InlineData(BridgeStatuses.Reconnecting, false)]
    public void Only_a_dropped_session_counts_as_disconnected(string status, bool expected)
        => Assert.Equal(expected, WhatsAppSessionAlertJob.IsDisconnected(status));

    [Fact]
    public void The_alert_uses_a_short_text_for_WhatsApp_and_the_full_steps_for_email()
    {
        var request = WhatsAppSessionAlertJob.BuildRequest(Guid.NewGuid(), "+919800000001", "session-20261124100000");

        Assert.Equal(TenantNotificationKind.WhatsAppSessionDisconnected, request.Kind);
        Assert.True(request.AlsoWhatsApp);
        Assert.Contains("+919800000001", request.WhatsAppBody);
        Assert.Contains("Linked Devices", request.Body);
        Assert.DoesNotContain("Linked Devices", request.WhatsAppBody);
        Assert.True(request.WhatsAppBody!.Length < request.Body.Length);
    }

    [Fact]
    public void The_alert_has_a_template_that_fits_Meta_with_the_longest_values()
    {
        var def = PlatformTemplateCatalog.For("WhatsAppSessionDisconnected");
        Assert.NotNull(def);
        Assert.Empty(PlatformTemplateCatalog.UnknownTokens(def!.Body));
        var longest = def.Body.Replace("{{TenantName}}", new string('x', 100)).Replace("{{Message}}", new string('x', PlatformTemplateCatalog.MaxValueLength));
        Assert.True(longest.Length <= PlatformTemplateCatalog.MaxBodyLength);
    }
}
