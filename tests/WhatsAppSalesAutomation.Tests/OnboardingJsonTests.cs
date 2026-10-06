using System.Text.Json;
using WhatsAppSalesAutomation.Application.Onboarding;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What the onboarding screen actually receives. The service tests never serialise, which is how a step state
/// that arrived as <c>0</c>, <c>1</c>, <c>2</c> - while the screen compares against "Completed", "Current" and
/// "Pending" - left every step showing as "Waiting".</summary>
public sealed class OnboardingJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Step_states_are_sent_as_words_the_screen_understands()
    {
        var status = new OnboardingStatusDto(
            IsCompleted: false, ProgressPercent: 20, CurrentStepKey: "customer-package", CompletedAt: null,
            Steps: new[]
            {
                new OnboardingStepDto("profile", "Profile Information", "d", 10, "/profile", OnboardingStepState.Completed, DateTime.UtcNow, null),
                new OnboardingStepDto("customer-package", "Create Customer Package", "d", 15, "/packages", OnboardingStepState.Current, null, "Create an active package."),
                new OnboardingStepDto("lead-discovery", "Lead Discovery Profile", "d", 10, "/lead-discovery/profile", OnboardingStepState.Pending, null, "Save your profile."),
            });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status, Web));

        var states = json.RootElement.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("state").GetString()).ToArray();
        Assert.Equal(new[] { "Completed", "Current", "Pending" }, states);
    }

    [Fact]
    public void Every_step_state_round_trips_by_name()
    {
        foreach (var state in Enum.GetValues<OnboardingStepState>())
            Assert.Equal(state, JsonSerializer.Deserialize<OnboardingStepState>($"\"{state}\"", Web));
    }
}
