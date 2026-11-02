using WhatsAppSalesAutomation.Application.Account;
using WhatsAppSalesAutomation.Application.Tenancy;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The alert address on a profile is optional and kept apart from the sign-in address, but when given it must be a real one.</summary>
public class AccountProfileValidatorTests
{
    private readonly UpdateUserProfileRequestValidator _validator = new();

    private static UpdateUserProfileRequest Request(string? notificationEmail) =>
        new("Asha Verma", "asha@example.com", null, TimeZoneCatalog.DefaultId, null, notificationEmail);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alerts@example.com")]
    public void An_empty_or_valid_notification_email_is_accepted(string? email)
    {
        Assert.True(_validator.Validate(Request(email)).IsValid);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("alerts@")]
    public void A_malformed_notification_email_is_refused_on_that_field(string email)
    {
        var result = _validator.Validate(Request(email));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateUserProfileRequest.NotificationEmail));
    }

    [Fact]
    public void An_over_long_notification_email_is_refused()
    {
        var result = _validator.Validate(Request(new string('a', 250) + "@example.com"));

        Assert.False(result.IsValid);
    }
}
