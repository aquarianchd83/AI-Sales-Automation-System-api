using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Application.Simulator;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The simulator hands back the stored dummy businesses exactly as they are - never invented or altered - with a realistic
/// share of the flawed ones, and leaves out the businesses the tenant already has.</summary>
public sealed class SimulatedLeadDiscoveryAgentTests
{
    private static readonly QualificationRules PhoneRequired = new(PhoneRequired: true, EmailRequired: false, IndependentBusiness: false, MinimumLeadScore: 60, RequiredFields: Array.Empty<string>());

    private static readonly int Stored = SimulatorDataCatalog.DefaultBusinesses.Count;

    private static LeadDiscoveryAgentRequest Ask(int count, params string[] known) => new(
        "dental clinic", new[] { "dentist" }, new[] { "Chandigarh" }, count, Array.Empty<string>(), true, false, false, 60, Array.Empty<string>(), known);

    private static async Task<(int Returned, int Qualified, int Rejected)> Run(int asked)
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(Ask(asked));
        var assessments = LeadQualification.AssessAll(result.Candidates, result.Evidence, PhoneRequired);

        return (result.Candidates.Count, assessments.Count(a => a.Lead is not null), assessments.Count(a => a.Lead is null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task It_returns_as_many_as_were_asked_for_while_stored_rows_last(int asked) =>
        Assert.Equal(asked, (await Run(asked)).Returned);

    [Fact]
    public async Task It_never_returns_more_than_the_stored_rows_and_never_repeats_one()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(Ask(50));

        Assert.Equal(Stored, result.Candidates.Count);
        Assert.Equal(Stored, result.Candidates.Select(c => c.BusinessName).Distinct().Count());
    }

    [Theory]
    [InlineData(10, 7, 3)]
    [InlineData(3, 2, 1)]      // a small ask still shows the rejection paths
    public async Task About_30_percent_are_the_flawed_rows(int asked, int qualified, int rejected)
    {
        var (_, actuallyQualified, actuallyRejected) = await Run(asked);

        Assert.Equal(qualified, actuallyQualified);
        Assert.Equal(rejected, actuallyRejected);
    }

    [Fact]
    public async Task Both_ways_of_failing_show_up_a_phone_on_no_page_and_a_closed_business()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(Ask(10));

        var reasons = LeadQualification.AssessAll(result.Candidates, result.Evidence, PhoneRequired)
            .Where(a => a.Lead is null).Select(a => a.RejectionReason).ToList();

        Assert.Contains("phone not publicly verifiable", reasons);
        Assert.Contains("closed business", reasons);
    }

    [Fact]
    public async Task Every_business_is_a_stored_row_exactly_as_it_is_stored()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(Ask(Stored));

        Assert.Equal(SimulatedLeadDiscoveryAgent.ModelName, result.Model);
        Assert.Equal(LeadDiscoveryUsage.None, result.Usage);
        foreach (var c in result.Candidates)
        {
            var row = SimulatorDataCatalog.DefaultBusinesses.Single(b => b.Name == c.BusinessName);
            Assert.Equal(row.Phone, c.Phone);
            Assert.Equal(row.Email, c.Email);
            Assert.Equal(row.Website, c.Website);
            Assert.Equal(row.Address, c.Address);
            Assert.Equal(row.ContactPerson, c.ContactPerson);
            Assert.Equal(row.LeadScore, c.LeadScore);
        }
    }

    [Fact]
    public async Task Businesses_the_tenant_already_has_are_left_out_instead_of_coming_back_as_duplicates()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);

        var result = await agent.DiscoverAsync(Ask(50, "Sharma Dental Care, Chandigarh", "Smile Studio Dental"));

        Assert.Equal(Stored - 2, result.Candidates.Count);
        Assert.DoesNotContain(result.Candidates, c => c.BusinessName is "Sharma Dental Care" or "Smile Studio Dental");
    }

    [Fact]
    public async Task The_kind_of_business_asked_for_comes_first()
    {
        var agent = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);
        var result = await agent.DiscoverAsync(Ask(1));

        Assert.Equal("Dental clinic", result.Candidates.Single().BusinessType);
    }
}
