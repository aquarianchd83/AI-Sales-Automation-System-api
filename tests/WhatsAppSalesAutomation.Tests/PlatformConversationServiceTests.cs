using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Platform Admin Console's Tenant conversations page: the admin chats with a tenant from the platform's own number, free text only
/// inside the 24-hour window, and the tenant's WhatsApp replies land in the same chat.</summary>
public sealed class PlatformConversationServiceTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private readonly FakePlatformWhatsApp _sender = new();
    private readonly Guid _tenant = Guid.NewGuid();

    public PlatformConversationServiceTests()
    {
        _h.Db.Tenants.Add(new Tenant { Id = _tenant, Name = "Acme Traders", Slug = "acme", Status = TenantStatus.Active, BillingAlertPhoneE164 = "+919820098200" });
        _h.Db.SaveChanges();
    }

    public void Dispose() => _h.Dispose();

    private PlatformConversationService Service() =>
        new(_h.Db, _h.Users, _sender, _h.Clock, NullLogger<PlatformConversationService>.Instance);

    private static WhatsAppWebhookParseResult Reply(string from, string text, DateTime at, string wamid = "wamid.in1") =>
        new(new[] { new InboundWhatsAppMessage(wamid, from, "Ravi", at, "text", text) }, Array.Empty<WhatsAppStatusUpdate>(), "111");

    [Fact]
    public async Task Starting_a_chat_with_a_tenant_uses_its_number_and_free_text_is_refused_until_the_tenant_writes()
    {
        var chat = await Service().StartAsync(new StartPlatformConversationRequest(_tenant, null));

        Assert.Equal("+919820098200", chat.PhoneE164);
        Assert.Equal("Acme Traders", chat.TenantName);
        Assert.False(chat.WindowOpen);

        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().SendAsync(chat.Id, new SendPlatformConversationMessageRequest("Hello", null, null, null), null, "admin@x.com"));
        Assert.Empty(_sender.Texts);

        // Starting again returns the same chat, not a second one.
        Assert.Equal(chat.Id, (await Service().StartAsync(new StartPlatformConversationRequest(_tenant, null))).Id);
    }

    [Fact]
    public async Task A_reply_lands_in_the_tenants_chat_opens_the_window_and_then_free_text_goes()
    {
        var chat = await Service().StartAsync(new StartPlatformConversationRequest(_tenant, null));

        await Service().RecordWebhookAsync(Reply("919820098200", "Haan, bataiye", _h.Clock.UtcNow.AddMinutes(-5)));
        // Meta redelivers: recorded once.
        await Service().RecordWebhookAsync(Reply("919820098200", "Haan, bataiye", _h.Clock.UtcNow.AddMinutes(-5)));

        var list = await Service().ListAsync(null);
        var row = Assert.Single(list);
        Assert.Equal(chat.Id, row.Id);
        Assert.Equal(1, row.UnreadCount);
        Assert.True(row.WindowOpen);
        Assert.Equal("Ravi", row.ContactName);

        var sent = await Service().SendAsync(chat.Id, new SendPlatformConversationMessageRequest("Aapka campaign ready hai", null, null, null), null, "admin@x.com");
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Single(_sender.Texts);

        var detail = await Service().GetAsync(chat.Id, null, 50);
        Assert.Equal(new[] { MessageDirection.Inbound, MessageDirection.Outbound }, detail.Messages.Select(m => m.Direction));
        Assert.Equal(0, (await Service().ListAsync(null)).Single().UnreadCount); // opening it marked it read

        // A day later the window has closed again.
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddHours(25);
        Assert.False((await Service().ListAsync(null)).Single().WindowOpen);
    }

    [Fact]
    public async Task An_unknown_number_gets_its_own_chat_and_a_tenant_number_is_matched_even_when_typed_differently()
    {
        await Service().RecordWebhookAsync(Reply("15551234567", "hi", _h.Clock.UtcNow, "wamid.a"));
        _h.Db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Zed", Slug = "zed", Status = TenantStatus.Active, SupportPhone = "098765 43210" });
        await _h.Db.SaveChangesAsync();
        await Service().RecordWebhookAsync(Reply("919876543210", "hello", _h.Clock.UtcNow, "wamid.b"));

        var list = await Service().ListAsync(null);
        Assert.Null(list.Single(c => c.PhoneE164 == "+15551234567").TenantId);
        Assert.Equal("Zed", list.Single(c => c.PhoneE164 == "+919876543210").TenantName);
    }

    [Fact]
    public async Task A_template_goes_when_the_window_is_closed_and_status_webhooks_move_it_forward_only()
    {
        _h.Db.PlatformMessageTemplates.Add(new PlatformMessageTemplate
        {
            EventKey = TenantNotificationKind.AdminConversation.ToString(), Name = "Admin message", WhatsAppTemplateName = "platform_admin_message",
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved, IsActive = true,
            BodyText = "Hi {{TenantName}}, the AiSaarthi team has a message about your account. {{Message}} You can reply to this message."
        });
        await _h.Db.SaveChangesAsync();
        var template = (await Service().GetTemplatesAsync()).Single();
        Assert.True(template.IsDefault);

        var chat = await Service().StartAsync(new StartPlatformConversationRequest(_tenant, null));
        var sent = await Service().SendAsync(chat.Id, new SendPlatformConversationMessageRequest(null, template.Id, null, "Campaign paused hai."), null, "admin@x.com");

        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal("platform_admin_message", sent.TemplateName);
        Assert.Contains("Hi Acme Traders,", sent.Body);
        Assert.Contains("Campaign paused hai.", sent.Body);
        var (_, parameters) = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "Acme Traders", "Campaign paused hai." }, parameters);

        // The fake gives no wamid, so give the stored row one to match statuses against.
        var row = await _h.Db.PlatformConversationMessages.SingleAsync();
        row.WhatsAppMessageId = "wamid.out";
        await _h.Db.SaveChangesAsync();

        WhatsAppWebhookParseResult Status(string s) =>
            new(Array.Empty<InboundWhatsAppMessage>(), new[] { new WhatsAppStatusUpdate("wamid.out", s, _h.Clock.UtcNow) }, "111");

        await Service().RecordWebhookAsync(Status("read"));
        await Service().RecordWebhookAsync(Status("delivered")); // late, out of order
        _h.Db.ChangeTracker.Clear();
        Assert.Equal(MessageStatus.Read, (await _h.Db.PlatformConversationMessages.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_send_whatsapp_refuses_is_kept_as_failed_with_the_reason()
    {
        var chat = await Service().StartAsync(new StartPlatformConversationRequest(_tenant, null));
        await Service().RecordWebhookAsync(Reply("919820098200", "hi", _h.Clock.UtcNow));
        _sender.TextFails = true;

        var sent = await Service().SendAsync(chat.Id, new SendPlatformConversationMessageRequest("Hello", null, null, null), null, null);

        Assert.Equal(MessageStatus.Failed, sent.Status);
        Assert.Contains("24-hour window", sent.ErrorMessage);
    }

    [Fact]
    public async Task Paging_never_splits_messages_that_share_a_timestamp()
    {
        var chat = await Service().StartAsync(new StartPlatformConversationRequest(_tenant, null));
        var at = _h.Clock.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 3; i++)
            await Service().RecordWebhookAsync(Reply("919820098200", "same second " + i, at, "wamid.s" + i));
        await Service().RecordWebhookAsync(Reply("919820098200", "older", at.AddMinutes(-1), "wamid.o"));
        await Service().RecordWebhookAsync(Reply("919820098200", "newest", at.AddMinutes(1), "wamid.n"));

        var first = await Service().GetAsync(chat.Id, null, 2);
        Assert.Equal(4, first.Messages.Count); // "newest" + the whole same-second group
        Assert.True(first.HasOlder);

        var older = await Service().GetAsync(chat.Id, first.Messages[0].SentAtUtc, 2);
        Assert.Equal("older", Assert.Single(older.Messages).Body);
        Assert.False(older.HasOlder);
    }

    [Fact]
    public async Task The_invite_gives_a_wa_me_link_to_the_platform_number_and_emails_it_to_the_tenant()
    {
        var owner = await _h.AddUserAsync("owner@acme.in", tenantId: _tenant);
        var tenant = await _h.Db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.OwnerUserId = owner.Id;
        await _h.Db.SaveChangesAsync();

        var mails = new List<(string To, string Body)>();
        var email = Fake.Of<IEmailSender>((_, a) => { mails.Add(((string)a![0]!, (string)a[2]!)); return Task.FromResult(new DeliveryResult(true)); });
        var store = Fake.Of<IAppSettingsStore>((m, _) => m.Name == nameof(IAppSettingsStore.GetAllAsync)
            ? Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [PlatformWhatsAppSettingsService.DisplayPhoneNumberKey] = "+91 98000 11111" })
            : throw new NotImplementedException(m.Name));
        var service = new PlatformConversationService(_h.Db, _h.Users, _sender, _h.Clock, NullLogger<PlatformConversationService>.Instance, email: email, settings: store);

        var chat = await service.StartAsync(new StartPlatformConversationRequest(_tenant, null));
        var invite = await service.GetInviteAsync(chat.Id);

        Assert.Equal("+919800011111", invite.PlatformNumber);
        Assert.Equal("https://wa.me/919800011111?text=Hi%20AiSaarthi%20team", invite.WhatsAppLink);
        Assert.Equal("owner@acme.in", invite.Email);

        var sent = await service.SendInviteEmailAsync(chat.Id, new SendPlatformConversationInviteRequest("Aapka campaign paused hai."), null, "admin@x.com");
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal("email", sent.MessageType);
        var (to, body) = Assert.Single(mails);
        Assert.Equal("owner@acme.in", to);
        Assert.Contains(invite.WhatsAppLink, body);
        Assert.Contains("Aapka campaign paused hai.", body);
        Assert.Empty(_sender.Texts); // nothing went on WhatsApp
    }
}
