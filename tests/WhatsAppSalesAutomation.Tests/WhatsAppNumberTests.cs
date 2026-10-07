using WhatsAppSalesAutomation.Application.Tenancy;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The WhatsApp number a tenant gives during onboarding: a phone number, nothing more.</summary>
public sealed class WhatsAppNumberTests
{
    private static readonly UpdateTenantWhatsAppNumberRequestValidator Validator = new();

    [Theory]
    [InlineData("+91 98765 43210")]
    [InlineData("+919876543210")]
    [InlineData("98765-43210")]
    [InlineData("(0172) 555 0100")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")] // blank clears it
    public void A_phone_number_or_nothing_is_accepted(string? number) =>
        Assert.True(Validator.Validate(new UpdateTenantWhatsAppNumberRequest(number)).IsValid);

    [Theory]
    [InlineData("call me")]
    [InlineData("+91 98765 43210 ext 5")]
    [InlineData("12345")]       // too short to be a real number
    [InlineData("123-45")]
    [InlineData("+++91987654321")]
    public void Anything_else_is_refused(string number) =>
        Assert.False(Validator.Validate(new UpdateTenantWhatsAppNumberRequest(number)).IsValid);

    [Fact]
    public void It_cannot_be_longer_than_the_column()
    {
        var tooLong = "+" + new string('9', 40);

        Assert.False(Validator.Validate(new UpdateTenantWhatsAppNumberRequest(tooLong)).IsValid);
    }
}
