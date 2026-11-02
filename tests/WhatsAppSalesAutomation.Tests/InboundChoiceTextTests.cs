using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A customer who taps a button or picks a list row said something: the words on it are kept as the message, so the conversation reads, the AI answers what
/// was chosen and the credit history can say what a reply was for. Before, both WhatsApp paths saved such a message with no text at all.</summary>
public class InboundChoiceTextTests
{
    private static InboundWhatsAppMessage Bridge(string message, string messageType)
    {
        var evt = EvolutionWebhookNormalizer.Normalize(
            $$"""{"event":"messages.upsert","data":{"key":{"remoteJid":"919800000001@s.whatsapp.net","fromMe":false,"id":"AC10"},"message":{{message}},"messageType":"{{messageType}}","messageTimestamp":1793071801},"apikey":"t"}""");
        Assert.True(EvolutionWebhookNormalizer.TryParseCanonical(evt.Canonical!, out var result));
        return Assert.Single(result.Messages);
    }

    private static InboundWhatsAppMessage Meta(string message)
    {
        var payload = """{"entry":[{"id":"w1","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"p1"},"contacts":[{"wa_id":"919800000001","profile":{"name":"Asha"}}],"messages":[""" + message + """]}}]}]}""";
        return Assert.Single(new WhatsAppWebhookParser(NullLogger<WhatsAppWebhookParser>.Instance).Parse(payload).Messages);
    }

    [Fact]
    public void Bridge_a_chosen_list_row_is_kept_as_the_text_and_is_not_typed_text()
    {
        var m = Bridge("""{"listResponseMessage":{"title":"Book a demo","singleSelectReply":{"selectedRowId":"demo"}}}""", "listResponseMessage");

        Assert.Equal("Book a demo", m.TextBody);
        Assert.Equal("listResponseMessage", m.MessageType);
    }

    [Fact]
    public void Bridge_a_tapped_button_is_kept_as_the_text()
    {
        Assert.Equal("Pricing", Bridge("""{"buttonsResponseMessage":{"selectedButtonId":"b1","selectedDisplayText":"Pricing"}}""", "buttonsResponseMessage").TextBody);
        Assert.Equal("Yes", Bridge("""{"templateButtonReplyMessage":{"selectedDisplayText":"Yes"}}""", "templateButtonReplyMessage").TextBody);
    }

    [Fact]
    public void Bridge_typed_text_is_unchanged_and_a_photo_still_has_no_text()
    {
        var typed = Bridge("""{"conversation":"Hello"}""", "conversation");
        Assert.Equal("Hello", typed.TextBody);
        Assert.Equal("text", typed.MessageType);

        Assert.Null(Bridge("""{"imageMessage":{}}""", "imageMessage").TextBody);
    }

    [Fact]
    public void Meta_a_reply_button_is_kept_as_the_text()
    {
        var m = Meta("""{"id":"wamid.1","from":"919800000001","timestamp":"1793071801","type":"interactive","interactive":{"type":"button_reply","button_reply":{"id":"b1","title":"Talk to sales"}}}""");

        Assert.Equal("Talk to sales", m.TextBody);
        Assert.Equal("interactive", m.MessageType);
    }

    [Fact]
    public void Meta_a_list_row_and_a_template_quick_reply_are_kept_as_the_text()
    {
        Assert.Equal("Pricing", Meta("""{"id":"wamid.2","from":"919800000001","timestamp":"1793071801","type":"interactive","interactive":{"type":"list_reply","list_reply":{"id":"r1","title":"Pricing","description":"Plans"}}}""").TextBody);
        Assert.Equal("Stop", Meta("""{"id":"wamid.3","from":"919800000001","timestamp":"1793071801","type":"button","button":{"payload":"p","text":"Stop"}}""").TextBody);
    }

    [Fact]
    public void Meta_typed_text_is_unchanged_and_a_photo_still_has_no_text()
    {
        Assert.Equal("Hello", Meta("""{"id":"wamid.4","from":"919800000001","timestamp":"1793071801","type":"text","text":{"body":"Hello"}}""").TextBody);
        Assert.Null(Meta("""{"id":"wamid.5","from":"919800000001","timestamp":"1793071801","type":"image","image":{"id":"m1"}}""").TextBody);
    }
}
