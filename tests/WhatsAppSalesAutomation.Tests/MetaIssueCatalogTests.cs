using WhatsAppSalesAutomation.Application.MetaOnboarding;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The tenant must be told what is actually wrong. A temporary Meta outage must never read as "verify your business",
/// and nothing technical (Meta's wording, codes, tokens) may leak into what is shown.
/// </summary>
public class MetaIssueCatalogTests
{
    private static MetaIssueDto Map(int? http, int? code, string? message = null, int? sub = null, bool unreachable = false) =>
        MetaIssueCatalog.FromMetaError(MetaSignupSteps.Webhook, new MetaErrorInfo(http, code, sub, message, "trace-1", unreachable));

    [Theory]
    [InlineData(500, null)]
    [InlineData(503, 2)]
    [InlineData(400, 4)]
    [InlineData(400, 80007)]
    [InlineData(429, null)]
    public void Temporary_failures_ask_for_a_retry_and_say_nothing_about_verification(int http, int? code)
    {
        var issue = Map(http, code, "boom");

        Assert.Equal("meta_temporary", issue.Code);
        Assert.Equal(MetaIssueAction.Retry, issue.PrimaryAction);
        Assert.True(issue.Retryable);
        Assert.DoesNotContain("verification", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserved", issue.Message);
    }

    [Fact]
    public void An_unreachable_meta_is_temporary()
    {
        Assert.Equal("meta_temporary", Map(null, null, "timeout", unreachable: true).Code);
    }

    [Theory]
    [InlineData(190)]
    [InlineData(102)]
    public void An_expired_token_asks_to_reconnect(int code)
    {
        var issue = Map(401, code, "Error validating access token");

        Assert.Equal("token_expired", issue.Code);
        Assert.Equal(MetaIssueAction.Reconnect, issue.PrimaryAction);
        Assert.False(issue.Retryable);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(299)]
    public void Missing_permissions_ask_to_reconnect(int code)
    {
        Assert.Equal("permission_missing", Map(403, code, "(#200) Requires whatsapp_business_management permission").Code);
    }

    [Fact]
    public void A_restricted_account_is_explained_as_a_restriction()
    {
        var issue = Map(400, 368, "temporarily blocked");

        Assert.Equal("account_restricted", issue.Code);
        Assert.Contains("restricted", issue.Message);
    }

    [Fact]
    public void A_billing_problem_points_to_platform_support()
    {
        var issue = Map(400, 131042, "payment issue");

        Assert.Equal("meta_billing", issue.Code);
        Assert.Equal(MetaIssueAction.ContactSupport, issue.PrimaryAction);
    }

    [Fact]
    public void An_unregistered_number_asks_to_verify()
    {
        var issue = Map(400, 133010, "Phone number not registered");

        Assert.Equal("number_not_registered", issue.Code);
        Assert.Equal(MetaIssueAction.Verify, issue.PrimaryAction);
    }

    [Fact]
    public void Business_verification_is_recognised_from_the_wording()
    {
        var issue = Map(400, 100, "Business verification is required to perform this action");

        Assert.Equal("business_verification", issue.Code);
        Assert.Equal(MetaIssueAction.Verify, issue.PrimaryAction);
    }

    [Fact]
    public void A_number_registered_elsewhere_is_recognised_from_the_wording()
    {
        Assert.Equal("number_in_use", Map(400, 100, "This phone number is already registered with another account").Code);
    }

    [Fact]
    public void An_unrecognised_error_gets_an_honest_generic_message_with_a_reference()
    {
        var issue = Map(400, 999999, "something nobody has seen before");

        Assert.Equal("meta_unknown", issue.Code);
        Assert.Equal("trace-1", issue.Reference);
        Assert.DoesNotContain("999999", issue.Message);
        Assert.DoesNotContain("nobody has seen", issue.Message);
    }

    [Fact]
    public void Metas_own_words_and_codes_never_reach_the_message()
    {
        var issue = Map(400, 190, "Error validating access token: Session has expired on Monday (fbtrace secret-ish)");

        Assert.DoesNotContain("Session has expired", issue.Message);
        Assert.DoesNotContain("190", issue.Message);
        Assert.DoesNotContain("Session has expired", issue.Title);
    }

    [Fact]
    public void A_cancelled_popup_asks_to_reconnect()
    {
        var issue = MetaIssueCatalog.FromClientEvent("CANCEL", "PHONE_NUMBER_SETUP", "ref");

        Assert.Equal("authorization_cancelled", issue.Code);
        Assert.Equal(MetaIssueAction.Reconnect, issue.PrimaryAction);
        Assert.Contains("authorization was not completed", issue.Message);
    }

    [Fact]
    public void A_popup_error_is_not_reported_as_a_cancel()
    {
        Assert.Equal("authorization_failed", MetaIssueCatalog.FromClientEvent("ERROR", null, "ref").Code);
    }
}
