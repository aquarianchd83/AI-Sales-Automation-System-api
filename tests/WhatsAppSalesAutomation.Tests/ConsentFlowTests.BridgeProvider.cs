using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

// Provider scoping through the real webhook processor: a QR-bridge message id is only unique within the bridge, so it must never be confused with
// (or dropped as a duplicate of) a Meta message that happens to carry the same id.
public sealed partial class ConsentFlowTests
{
    private async Task DeliverInboundAsync(string messageId, string provider, string text = "Hello", int minutesLater = 30)
    {
        _clock.UtcNow = T0.AddMinutes(minutesLater);
        _parser.Next = new WhatsAppWebhookParseResult(
            new[] { new InboundWhatsAppMessage(messageId, "919800000001", "Asha", _clock.UtcNow, "text", text) },
            Array.Empty<WhatsAppStatusUpdate>(), Provider: provider);

        var eventId = await _processor.RecordAsync(_tenant.Id, "bridge", "{}");
        await _processor.ProcessAsync(_tenant.Id, eventId);
    }

    private async Task DeliverStatusAsync(string messageId, string status, string provider, int minutesLater = 40)
    {
        _clock.UtcNow = T0.AddMinutes(minutesLater);
        _parser.Next = new WhatsAppWebhookParseResult(
            Array.Empty<InboundWhatsAppMessage>(), new[] { new WhatsAppStatusUpdate(messageId, status, _clock.UtcNow) }, Provider: provider);

        var eventId = await _processor.RecordAsync(_tenant.Id, "bridge", "{}");
        await _processor.ProcessAsync(_tenant.Id, eventId);
    }

    [Fact]
    public async Task A_bridge_message_with_the_same_id_as_a_meta_message_is_a_different_message_not_a_duplicate()
    {
        await DeliverInboundAsync("SAME-ID", WhatsAppProviders.Meta, "from meta");
        await DeliverInboundAsync("SAME-ID", WhatsAppProviders.QrBridge, "from the bridge", minutesLater: 45);

        var inbound = await _db.Messages.Where(m => m.Direction == MessageDirection.Inbound).OrderBy(m => m.CreatedAt).ToListAsync();
        Assert.Equal(2, inbound.Count);
        Assert.Contains(inbound, m => m.Provider == WhatsAppProviders.Meta && m.Text == "from meta");
        Assert.Contains(inbound, m => m.Provider == WhatsAppProviders.QrBridge && m.Text == "from the bridge");
    }

    [Fact]
    public async Task A_bridge_message_redelivered_is_recorded_once()
    {
        await DeliverInboundAsync("BR-1", WhatsAppProviders.QrBridge);
        await DeliverInboundAsync("BR-1", WhatsAppProviders.QrBridge, minutesLater: 31);

        Assert.Equal(1, await _db.Messages.CountAsync(m => m.Direction == MessageDirection.Inbound));
    }

    [Fact]
    public async Task A_bridge_inbound_message_is_stored_against_the_bridge_with_a_provider_scoped_idempotency_key()
    {
        await DeliverInboundAsync("BR-2", WhatsAppProviders.QrBridge);

        var message = await _db.Messages.SingleAsync(m => m.Direction == MessageDirection.Inbound);
        Assert.Equal(WhatsAppProviders.QrBridge, message.Provider);
        Assert.Equal("inbound:QrBridge:BR-2", message.IdempotencyKey);
    }

    [Fact]
    public async Task A_meta_inbound_message_keeps_its_original_idempotency_key()
    {
        await DeliverInboundAsync("wamid.M1", WhatsAppProviders.Meta);

        Assert.Equal("inbound:wamid.M1", (await _db.Messages.SingleAsync(m => m.Direction == MessageDirection.Inbound)).IdempotencyKey);
    }

    [Fact]
    public async Task A_delivery_status_only_ever_updates_the_message_of_its_own_provider()
    {
        await DeliverInboundAsync("seed", WhatsAppProviders.Meta, "creates the customer");
        var customerId = (await _db.Customers.SingleAsync()).Id;
        _db.Messages.Add(new Message { CustomerId = customerId, Direction = MessageDirection.Outbound, Status = MessageStatus.Sent, WhatsAppMessageId = "X1", Provider = WhatsAppProviders.Meta, IdempotencyKey = "meta-out" });
        _db.Messages.Add(new Message { CustomerId = customerId, Direction = MessageDirection.Outbound, Status = MessageStatus.Sent, WhatsAppMessageId = "X1", Provider = WhatsAppProviders.QrBridge, IdempotencyKey = "bridge-out" });
        await _db.SaveChangesAsync();

        await DeliverStatusAsync("X1", "delivered", WhatsAppProviders.QrBridge);

        Assert.Equal(MessageStatus.Delivered, (await _db.Messages.SingleAsync(m => m.IdempotencyKey == "bridge-out")).Status);
        Assert.Equal(MessageStatus.Sent, (await _db.Messages.SingleAsync(m => m.IdempotencyKey == "meta-out")).Status);
    }

    [Fact]
    public async Task A_status_cannot_move_a_message_backwards_whichever_provider_sent_it()
    {
        await DeliverInboundAsync("seed", WhatsAppProviders.Meta, "creates the customer");
        var customerId = (await _db.Customers.SingleAsync()).Id;
        _db.Messages.Add(new Message { CustomerId = customerId, Direction = MessageDirection.Outbound, Status = MessageStatus.Sent, WhatsAppMessageId = "X2", Provider = WhatsAppProviders.QrBridge, IdempotencyKey = "bridge-out-2" });
        await _db.SaveChangesAsync();

        // Observed from the real bridge: DELIVERY_ACK can arrive before SERVER_ACK, and READ after both.
        await DeliverStatusAsync("X2", "read", WhatsAppProviders.QrBridge);
        await DeliverStatusAsync("X2", "delivered", WhatsAppProviders.QrBridge, minutesLater: 41);
        await DeliverStatusAsync("X2", "sent", WhatsAppProviders.QrBridge, minutesLater: 42);

        Assert.Equal(MessageStatus.Read, (await _db.Messages.SingleAsync(m => m.IdempotencyKey == "bridge-out-2")).Status);
    }
}
