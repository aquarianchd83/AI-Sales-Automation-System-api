using WhatsAppSalesAutomation.Application.Compliance;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class TemplateSendGateTests
{
    private static Customer Consented(ConsentScope scope = ConsentScope.Marketing) => new()
    {
        PhoneNumberE164 = "+919000000000",
        OptInStatus = OptInStatus.OptedIn,
        OptInTimestamp = DateTime.UtcNow.AddDays(-1),
        OptInSource = "WebsiteForm",
        OptInProof = "form-entry-1042",
        OptInScope = scope
    };

    [Theory]
    [InlineData(TemplateCategory.Marketing)]
    [InlineData(TemplateCategory.Utility)]
    [InlineData(TemplateCategory.Authentication)]
    public void A_customer_with_a_complete_marketing_consent_can_be_sent_any_category(TemplateCategory category)
    {
        Assert.True(TemplateSendGate.Evaluate(Consented(), category).Allowed);
    }

    [Fact]
    public void A_customer_who_never_opted_in_is_blocked()
    {
        var customer = new Customer { PhoneNumberE164 = "+919000000000" };

        var decision = TemplateSendGate.Evaluate(customer, TemplateCategory.Utility);

        Assert.False(decision.Allowed);
        Assert.Contains("not opted in", decision.Reason);
    }

    [Fact]
    public void An_opted_out_customer_is_blocked_even_with_a_complete_record()
    {
        var customer = Consented();
        customer.OptInStatus = OptInStatus.OptedOut;
        customer.OptOutTimestamp = DateTime.UtcNow;

        var decision = TemplateSendGate.Evaluate(customer, TemplateCategory.Utility);

        Assert.False(decision.Allowed);
        Assert.Contains("opted out", decision.Reason);
    }

    [Fact]
    public void Transactional_consent_covers_service_templates_but_not_promotions()
    {
        var customer = Consented(ConsentScope.Transactional);

        Assert.True(TemplateSendGate.Evaluate(customer, TemplateCategory.Utility).Allowed);
        Assert.True(TemplateSendGate.Evaluate(customer, TemplateCategory.Authentication).Allowed);

        var promo = TemplateSendGate.Evaluate(customer, TemplateCategory.Marketing);
        Assert.False(promo.Allowed);
        Assert.Contains("promotional", promo.Reason);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("source")]
    [InlineData("proof")]
    [InlineData("scope")]
    public void Opted_in_with_any_part_of_the_record_missing_is_blocked(string missing)
    {
        var customer = Consented();
        switch (missing)
        {
            case "timestamp": customer.OptInTimestamp = null; break;
            case "source": customer.OptInSource = " "; break;
            case "proof": customer.OptInProof = null; break;
            case "scope": customer.OptInScope = null; break;
        }

        var decision = TemplateSendGate.Evaluate(customer, TemplateCategory.Utility);

        Assert.False(decision.Allowed);
        Assert.Contains("incomplete", decision.Reason);
    }

    [Theory]
    [InlineData("Marketing", true, ConsentScope.Marketing)]
    [InlineData("promotional", true, ConsentScope.Marketing)]
    [InlineData("Transactional", true, ConsentScope.Transactional)]
    [InlineData("booking", true, ConsentScope.Transactional)]
    [InlineData("", false, default(ConsentScope))]
    [InlineData(null, false, default(ConsentScope))]
    [InlineData("everything", false, default(ConsentScope))]
    public void Scope_is_read_strictly_and_never_defaulted(string? value, bool ok, ConsentScope expected)
    {
        var parsed = ImportConsentResolver.TryParseScope(value, out var scope);

        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal(expected, scope);
    }

    [Fact]
    public async Task Opt_in_request_needs_proof_and_a_scope()
    {
        var validator = new OptInCustomerRequestValidator();

        Assert.False((await validator.ValidateAsync(new OptInCustomerRequest("WebsiteForm"))).IsValid);
        Assert.False((await validator.ValidateAsync(new OptInCustomerRequest("WebsiteForm", Proof: "x"))).IsValid);
        Assert.False((await validator.ValidateAsync(new OptInCustomerRequest("WebsiteForm", Scope: "Marketing"))).IsValid);
        Assert.True((await validator.ValidateAsync(new OptInCustomerRequest("WebsiteForm", Proof: "x", Scope: "Marketing"))).IsValid);
    }
}
