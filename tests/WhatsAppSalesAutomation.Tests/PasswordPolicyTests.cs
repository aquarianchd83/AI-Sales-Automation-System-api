using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The request validators must refuse what ASP.NET Identity would refuse, so a weak password never gets as far as creating a tenant.</summary>
public sealed class PasswordPolicyTests
{
    private sealed class Probe : AbstractValidator<string>
    {
        public Probe() => RuleFor(x => x).MeetsPasswordPolicy();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password1")]
    [InlineData("password1!")]
    [InlineData("PASSWORD1!")]
    [InlineData("Pass1!")]
    [InlineData("")]
    public void Weak_passwords_are_refused(string password) => Assert.False(new Probe().Validate(password).IsValid);

    [Fact]
    public void A_password_with_every_class_is_accepted() => Assert.True(new Probe().Validate("Passw0rd!").IsValid);
}
