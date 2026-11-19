using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The simulator hands back what was asked for, a realistic share of it failing the rules - so a simulated run behaves like a real one
/// instead of returning only candidates that qualify.</summary>
public sealed class SimulatedLeadDiscoveryAgentTests
{
    private static readonly QualificationRules PhoneRequired = new(PhoneRequired: true, EmailRequired: false, IndependentBusiness: false, MinimumLeadScore: 60, RequiredFields: Array.Empty<string>());

    private static async Task<(int Returned, int Qualified, int Rejected)> Run(int asked)
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var request = new LeadDiscoveryAgentRequest(
            "dental clinic", new[] { "dentist" }, new[] { "Chandigarh" }, asked, Array.Empty<string>(), true, false, false, 60, Array.Empty<string>(), Array.Empty<string>());

        var result = await agent.DiscoverAsync(request);
        var assessments = LeadQualification.AssessAll(result.Candidates, result.Evidence, PhoneRequired);

        return (result.Candidates.Count, assessments.Count(a => a.Lead is not null), assessments.Count(a => a.Lead is null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    public async Task It_returns_exactly_as_many_candidates_as_were_asked_for(int asked) =>
        Assert.Equal(asked, (await Run(asked)).Returned);

    [Theory]
    [InlineData(15, 10, 5)]    // 30% of 15 is 4.5: five fail
    [InlineData(20, 14, 6)]
    [InlineData(10, 7, 3)]
    [InlineData(3, 2, 1)]      // a small ask still shows the rejection paths
    public async Task About_30_percent_fail_the_rules_and_the_rest_qualify(int asked, int qualified, int rejected)
    {
        var (_, actuallyQualified, actuallyRejected) = await Run(asked);

        Assert.Equal(qualified, actuallyQualified);
        Assert.Equal(rejected, actuallyRejected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_request_for_one_or_two_has_nothing_flawed_so_a_tiny_batch_is_not_starved(int asked)
    {
        var (_, qualified, rejected) = await Run(asked);

        Assert.Equal(asked, qualified);
        Assert.Equal(0, rejected);
    }

    [Fact]
    public async Task Both_ways_of_failing_show_up_in_turn_a_phone_on_no_page_and_a_closed_business()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(new LeadDiscoveryAgentRequest(
            "dental clinic", new[] { "dentist" }, new[] { "Chandigarh" }, 20, Array.Empty<string>(), true, false, false, 60, Array.Empty<string>(), Array.Empty<string>()));

        var reasons = LeadQualification.AssessAll(result.Candidates, result.Evidence, PhoneRequired)
            .Where(a => a.Lead is null).Select(a => a.RejectionReason).ToList();

        Assert.Contains("phone not publicly verifiable", reasons);
        Assert.Contains("closed business", reasons);
    }

    [Fact]
    public async Task What_it_returns_is_marked_as_invented_and_costs_nothing()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(new LeadDiscoveryAgentRequest(
            "dental clinic", new[] { "dentist" }, new[] { "Chandigarh" }, 10, Array.Empty<string>(), true, false, false, 60, Array.Empty<string>(), Array.Empty<string>()));

        Assert.Equal(SimulatedLeadDiscoveryAgent.ModelName, result.Model);
        Assert.Equal(LeadDiscoveryUsage.None, result.Usage);
        Assert.All(result.Candidates, c => Assert.StartsWith("[Simulated]", c.BusinessName));
    }
}
