using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What is kept about a candidate the agent reported: all of it, cut to fixed lengths, and readable back even when the stored
/// text is missing or damaged.</summary>
public sealed class LeadDiscoveryCandidateSnapshotTests
{
    private static DiscoveredBusinessCandidate Candidate(Func<DiscoveredBusinessCandidate, DiscoveredBusinessCandidate>? change = null)
    {
        var candidate = new DiscoveredBusinessCandidate(
            "Pristine Dental", "Dental clinic", "Dr. Nakul Singla", "SCO 12, Sector 20", "Chandigarh", "Punjab",
            "+91 87280 70880", "https://pristine.example/contact", "hello@pristine.example", "https://pristine.example",
            "https://pristine.example/contact", true, false, 82, "Strong match, phone on the contact page.");
        return change?.Invoke(candidate) ?? candidate;
    }

    [Fact]
    public void Everything_the_agent_reported_survives_a_round_trip()
    {
        var snapshot = LeadDiscoveryCandidateSnapshot.Parse(LeadDiscoveryCandidateSnapshot.Serialize(Candidate()))!;

        Assert.Equal("Dental clinic", snapshot.BusinessType);
        Assert.Equal("Dr. Nakul Singla", snapshot.ContactPerson);
        Assert.Equal("SCO 12, Sector 20", snapshot.Address);
        Assert.Equal("Chandigarh", snapshot.City);
        Assert.Equal("Punjab", snapshot.State);
        Assert.Equal("+91 87280 70880", snapshot.Phone);
        Assert.Equal("https://pristine.example/contact", snapshot.PhoneSourceUrl);
        Assert.Equal("hello@pristine.example", snapshot.Email);
        Assert.Equal("https://pristine.example", snapshot.Website);
        Assert.Equal("https://pristine.example/contact", snapshot.SourceUrl);
        Assert.True(snapshot.IsIndependentBusiness);
        Assert.False(snapshot.IsPermanentlyClosed);
        Assert.Equal(82, snapshot.LeadScore);
        Assert.Equal("Strong match, phone on the contact page.", snapshot.ScoreRationale);
    }

    [Fact]
    public void Missing_values_stay_missing_and_blank_ones_become_missing()
    {
        var snapshot = LeadDiscoveryCandidateSnapshot.From(Candidate(c => c with { ContactPerson = "   ", Email = null, Website = "" }));

        Assert.Null(snapshot.ContactPerson);
        Assert.Null(snapshot.Email);
        Assert.Null(snapshot.Website);
    }

    [Fact]
    public void Model_written_text_is_cut_to_fixed_lengths()
    {
        var long5000 = new string('x', 5000);

        var snapshot = LeadDiscoveryCandidateSnapshot.From(Candidate(c => c with
        {
            BusinessType = long5000, Address = long5000, SourceUrl = long5000, ScoreRationale = long5000, Email = long5000
        }));

        Assert.Equal(200, snapshot.BusinessType.Length);
        Assert.Equal(500, snapshot.Address!.Length);
        Assert.Equal(1000, snapshot.SourceUrl.Length);
        Assert.Equal(1000, snapshot.ScoreRationale!.Length);
        Assert.Equal(254, snapshot.Email!.Length);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 100)]
    public void The_score_is_kept_within_0_to_100(int reported, int kept) =>
        Assert.Equal(kept, LeadDiscoveryCandidateSnapshot.From(Candidate(c => c with { LeadScore = reported })).LeadScore);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Nothing_or_damaged_text_reads_as_no_details_and_never_throws(string? stored) =>
        Assert.Null(LeadDiscoveryCandidateSnapshot.Parse(stored));
}
