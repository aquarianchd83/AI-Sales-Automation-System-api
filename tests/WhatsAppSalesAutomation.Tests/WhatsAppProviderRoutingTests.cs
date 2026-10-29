using WhatsAppSalesAutomation.Application.Platform;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class WhatsAppProviderRoutingTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Without_an_override_the_platform_default_applies()
    {
        Assert.Equal("QrBridge", WhatsAppProviderRouting.Resolve("QrBridge", null, null, A));
        Assert.Equal("Meta", WhatsAppProviderRouting.Resolve("Meta", null, null, A));
    }

    [Fact]
    public void A_tenant_override_wins_over_the_platform_default_both_ways()
    {
        Assert.Equal("QrBridge", WhatsAppProviderRouting.Resolve("Meta", new[] { $"{A}=QrBridge" }, null, A));
        Assert.Equal("Meta", WhatsAppProviderRouting.Resolve("QrBridge", new[] { $"{A}=Meta" }, null, A));
        Assert.Equal("Meta", WhatsAppProviderRouting.Resolve("Meta", new[] { $"{A}=QrBridge" }, null, B));
    }

    [Fact]
    public void The_pilot_list_still_limits_the_global_QR_choice_but_not_an_explicit_override()
    {
        Assert.Equal("Meta", WhatsAppProviderRouting.Resolve("QrBridge", null, new[] { B }, A));
        Assert.Equal("QrBridge", WhatsAppProviderRouting.Resolve("Meta", new[] { $"{A}=QrBridge" }, new[] { B }, A));
    }

    [Fact]
    public void With_replaces_or_clears_one_tenants_entry_only()
    {
        var list = WhatsAppProviderRouting.With(new[] { $"{A}=Meta", $"{B}=QrBridge" }, A, "QrBridge");
        Assert.Equal("QrBridge", WhatsAppProviderRouting.OverrideFor(list, A));
        Assert.Equal("QrBridge", WhatsAppProviderRouting.OverrideFor(list, B));

        var cleared = WhatsAppProviderRouting.With(list, A, null);
        Assert.Null(WhatsAppProviderRouting.OverrideFor(cleared, A));
        Assert.Equal("QrBridge", WhatsAppProviderRouting.OverrideFor(cleared, B));
    }
}
