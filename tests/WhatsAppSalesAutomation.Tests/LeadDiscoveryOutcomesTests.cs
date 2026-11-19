using WhatsAppSalesAutomation.Application.LeadDiscovery;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The outcome label on each candidate row, which the "candidates considered" popup groups and the run's own figures
/// (saved, duplicate, rejected) have to agree with.</summary>
public sealed class LeadDiscoveryOutcomesTests
{
    private const string NotNeeded = "Qualified, but the batch was already full - not saved and not charged.";

    [Theory]
    [InlineData("Created", false, null, false, "Saved")]
    [InlineData("Duplicate", false, null, false, "Duplicate")]
    [InlineData("Failed", false, null, false, "Failed")]
    [InlineData("Pending", false, null, false, "Pending")]
    [InlineData("Processing", false, null, false, "Pending")]
    [InlineData("Skipped", true, "Invalid: closed business (not charged)", false, "Rejected")]          // failed a rule: never kept
    [InlineData("Skipped", true, "Rejected by the DND check (DND)", true, "Lead only")]                   // kept as a lead, no customer
    [InlineData("Skipped", true, "Invalid: no WhatsApp-capable phone number - kept as a discovered lead only.", true, "Lead only")]
    [InlineData("Skipped", false, NotNeeded, false, "Not needed")]
    [InlineData("Skipped", false, "Already created by an earlier execution - not processed again.", false, "Skipped")]
    public void Each_row_gets_the_outcome_the_popup_and_the_run_figures_agree_on(string status, bool isInvalid, string? note, bool hasLead, string expected) =>
        Assert.Equal(expected, LeadDiscoveryExecutionOutcomes.Of(status, isInvalid, note, hasLead));

    [Fact]
    public void A_lead_kept_without_a_customer_counts_with_the_saved_ones_and_not_with_the_rejected()
    {
        // The run's "saved" figure is customers created plus leads kept without one; "rejected" is only what failed a rule.
        var kept = LeadDiscoveryExecutionOutcomes.Of("Skipped", true, "Rejected by the DND check", hasDiscoveredLead: true);
        var failedRule = LeadDiscoveryExecutionOutcomes.Of("Skipped", true, "Invalid: closed business", hasDiscoveredLead: false);

        Assert.Contains(kept, new[] { LeadDiscoveryExecutionOutcomes.Saved, LeadDiscoveryExecutionOutcomes.LeadOnly });
        Assert.Equal(LeadDiscoveryExecutionOutcomes.Rejected, failedRule);
    }
}
