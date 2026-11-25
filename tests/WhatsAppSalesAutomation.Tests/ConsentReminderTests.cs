using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.LeadDiscovery;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>How long a customer has to answer the consent SMS, and which reminder days fit inside that.</summary>
public sealed class ConsentReminderTests
{
    [Fact]
    public void Without_a_campaign_the_default_window_applies() =>
        Assert.Equal(14, ConsentWindow.Days(14, null));

    [Fact]
    public void A_campaign_with_no_delays_keeps_the_default_window() =>
        Assert.Equal(14, ConsentWindow.Days(14, new[] { 0 }));

    [Fact]
    public void A_campaign_that_closes_after_six_days_gives_six_days()
    {
        // First step at once, then follow-ups 2, 2 and 2 days apart: its last message falls due on day 6.
        Assert.Equal(6, ConsentWindow.Days(14, new[] { 0, 2, 2, 2 }));
    }

    [Fact]
    public void A_longer_campaign_never_stretches_the_window_past_the_default() =>
        Assert.Equal(14, ConsentWindow.Days(14, new[] { 0, 10, 10 }));

    [Theory]
    [InlineData(3, 6, true)]
    [InlineData(5, 6, true)]
    [InlineData(6, 6, false)] // the day the link closes is too late
    [InlineData(7, 6, false)]
    [InlineData(7, 14, true)]
    [InlineData(13, 14, true)]
    [InlineData(14, 14, false)]
    [InlineData(0, 14, false)]
    public void A_reminder_must_fall_before_the_window_closes(int day, int window, bool fits) =>
        Assert.Equal(fits, ConsentWindow.Fits(day, window));

    [Fact]
    public void The_last_reminder_day_is_the_day_before_the_window_closes() =>
        Assert.Equal(5, ConsentWindow.LastReminderDay(6));

    [Fact]
    public void The_reminder_says_it_is_a_reminder_and_carries_the_link()
    {
        var body = ConsentSmsMessage.BuildReminderBody("Asha", "Acme", "eye care", "https://app.example.com/consent/abc");

        Assert.StartsWith("Reminder: Hi Asha!", body);
        Assert.Contains("https://app.example.com/consent/abc", body);
        Assert.Null(ConsentSmsMessage.Preflight("+919000000000", body, "https://app.example.com/consent/abc"));
    }

    [Theory]
    [InlineData(3, 7, true)]
    [InlineData(3, null, true)]
    [InlineData(3, 3, false)]
    [InlineData(5, 4, false)]
    [InlineData(0, 7, false)]
    [InlineData(3, 14, false)]
    [InlineData(14, null, false)]
    public void The_reminder_days_are_checked_when_the_profile_is_saved(int first, int? second, bool valid)
    {
        var request = new SaveLeadDiscoveryProfileRequest(
            true, "Clinics", new[] { "clinic" }, new[] { "Pune" }, 10, ConsentSmsEnabled: true,
            ConsentReminderEnabled: true, ConsentReminderFirstDays: first, ConsentReminderSecondDays: second);

        Assert.Equal(valid, new SaveLeadDiscoveryProfileRequestValidator().Validate(request).Errors.All(e =>
            !e.PropertyName.StartsWith("ConsentReminder", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reminder_days_are_not_checked_while_reminders_are_off()
    {
        var request = new SaveLeadDiscoveryProfileRequest(
            true, "Clinics", new[] { "clinic" }, new[] { "Pune" }, 10, ConsentSmsEnabled: true,
            ConsentReminderEnabled: false, ConsentReminderFirstDays: 0, ConsentReminderSecondDays: 99);

        Assert.DoesNotContain(new SaveLeadDiscoveryProfileRequestValidator().Validate(request).Errors,
            e => e.PropertyName.StartsWith("ConsentReminder", StringComparison.Ordinal));
    }
}
