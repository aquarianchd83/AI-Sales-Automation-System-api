using System.Text.Json;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class PlatformTicketJsonTests
{
    [Fact]
    public void A_ticket_goes_over_the_wire_with_named_priority_and_status_the_console_reads()
    {
        var dto = new PlatformTicketDto(Guid.NewGuid(), null, null, PlatformTicketKind.SimulatedAiConfiguration, "s", "d", PlatformTicketPriority.Urgent, PlatformTicketStatus.InProgress, null, DateTime.UtcNow, null);

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"priority\":\"Urgent\"", json);
        Assert.Contains("\"status\":\"InProgress\"", json);
        Assert.Contains("\"kind\":\"SimulatedAiConfiguration\"", json);
    }

    [Fact]
    public void A_status_update_can_be_sent_by_name()
    {
        var request = JsonSerializer.Deserialize<UpdatePlatformTicketStatusRequest>("{\"status\":\"Resolved\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(PlatformTicketStatus.Resolved, request!.Status);
    }

    [Fact]
    public void An_operator_alert_names_its_kind_and_severity()
    {
        var json = JsonSerializer.Serialize(new { kind = PlatformNotificationKind.ConfigurationNeeded, severity = PlatformNotificationSeverity.Critical });

        Assert.Equal("{\"kind\":\"ConfigurationNeeded\",\"severity\":\"Critical\"}", json);
    }
}
