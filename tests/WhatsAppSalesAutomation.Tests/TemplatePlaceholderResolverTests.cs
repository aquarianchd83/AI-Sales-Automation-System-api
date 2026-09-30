using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What a customer actually receives once the placeholders are filled in.</summary>
public sealed class TemplatePlaceholderResolverTests
{
    private static Customer Person(string? first, string? last) =>
        new() { FirstName = first, LastName = last, PhoneNumberE164 = "+919815733426" };

    [Fact]
    public void Placeholders_are_replaced_with_the_customers_own_values_in_order()
    {
        var (text, values) = TemplatePlaceholderResolver.Resolve(
            "Hi {{FirstName}} {{LastName}}, call {{PhoneNumber}}", Person("Harish", "Bansal"));

        Assert.Equal("Hi Harish Bansal, call +919815733426", text);
        Assert.Equal(new[] { "Harish", "Bansal", "+919815733426" }, values);
    }

    [Fact]
    public void A_missing_first_name_becomes_there_instead_of_an_empty_variable()
    {
        var (text, values) = TemplatePlaceholderResolver.Resolve("Hi {{FirstName}}, welcome!", Person(null, null));

        Assert.Equal("Hi there, welcome!", text);
        Assert.Equal(new[] { "there" }, values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_counts_as_missing(string? blank)
    {
        var (_, values) = TemplatePlaceholderResolver.Resolve("{{FirstName}} {{LastName}}", Person(blank, blank));

        Assert.Equal(new[] { TemplatePlaceholderResolver.FirstNameFallback, TemplatePlaceholderResolver.LastNameFallback }, values);
        Assert.All(values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void A_name_with_stray_spaces_is_trimmed()
    {
        var (text, _) = TemplatePlaceholderResolver.Resolve("Hi {{FirstName}}", Person("  Asha ", null));

        Assert.Equal("Hi Asha", text);
    }

    [Fact]
    public void A_repeated_placeholder_still_sends_one_variable()
    {
        var (text, values) = TemplatePlaceholderResolver.Resolve("{{FirstName}}, yes you, {{FirstName}}", Person(null, null));

        Assert.Equal("there, yes you, there", text);
        Assert.Single(values);
    }
}
