using WhatsAppSalesAutomation.Infrastructure.Identity;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A deployed instance must not start with a signing secret that anyone reading the repository already knows.</summary>
public class JwtSecretGuardTests
{
    [Fact]
    public void The_placeholder_shipped_in_appsettings_is_rejected()
    {
        var problem = JwtSecretGuard.FindProblem("REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARACTERS");

        Assert.NotNull(problem);
        Assert.Contains("placeholder", problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_secret_is_rejected(string? secret) => Assert.Contains("not set", JwtSecretGuard.FindProblem(secret));

    [Fact]
    public void A_short_secret_is_rejected_and_the_message_says_how_long_it_must_be()
    {
        var problem = JwtSecretGuard.FindProblem("only-twenty-chars-xx");

        Assert.NotNull(problem);
        Assert.Contains("32", problem);
    }

    [Fact]
    public void A_long_but_repetitive_secret_is_rejected()
    {
        Assert.NotNull(JwtSecretGuard.FindProblem(new string('a', 64)));
        Assert.NotNull(JwtSecretGuard.FindProblem("abababababababababababababababababab"));
    }

    [Theory]
    [InlineData("k9Qz3vLx0pTn7YbR2mWc8HdJ5sGf1UaE")]
    [InlineData("wiring-test-secret-wiring-test-secret-wiring")]
    public void A_long_varied_secret_is_accepted(string secret) => Assert.Null(JwtSecretGuard.FindProblem(secret));

    [Fact]
    public void The_placeholder_is_still_caught_when_padded_past_the_minimum_length()
    {
        Assert.NotNull(JwtSecretGuard.FindProblem("replace_with_something_" + new string('x', 40)));
    }
}
