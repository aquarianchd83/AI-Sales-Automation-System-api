using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What a discovery round submits: near-misses are asked for, the model explains a short or empty submission, and that
/// explanation reaches the log.</summary>
public sealed class LeadSubmissionTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void The_submit_schema_requires_a_note_and_stays_strict()
    {
        var schema = JsonNode.Parse(LeadDiscoveryPrompts.SubmitToolInputSchema)!;

        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("note", required);
        Assert.Contains("candidates", required);
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.NotNull(schema["properties"]!["note"]);
    }

    [Fact]
    public void Both_agents_are_told_to_report_near_misses_instead_of_dropping_them()
    {
        foreach (var prompt in new[] { LeadDiscoveryPrompts.SystemPrompt, LeadDiscoveryPrompts.OpenAiSystemPrompt })
        {
            Assert.Contains("near-miss", prompt);
            Assert.Contains("do not drop a business only because it misses one", prompt);
            Assert.DoesNotContain("or none, rather than weak", prompt);
            Assert.Contains("note field", prompt);
        }

        Assert.Contains("fetch_page", LeadDiscoveryPrompts.OpenAiSystemPrompt);
        Assert.DoesNotContain("web_fetch", LeadDiscoveryPrompts.OpenAiSystemPrompt);
    }

    [Fact]
    public void An_empty_submission_is_a_warning_carrying_the_models_reason()
    {
        var log = new CapturingLogger();
        var input = JsonNode.Parse("""{"candidates":[],"note":"Every clinic lacked a named contact person."}""");

        LeadSubmission.LogOutcome(log, "OpenAI", submitted: 0, maximum: 2, input);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("0 of at most 2", entry.Message);
        Assert.Contains("Every clinic lacked a named contact person.", entry.Message);
    }

    [Fact]
    public void A_full_submission_is_only_information_and_a_missing_note_is_said_so()
    {
        var log = new CapturingLogger();

        LeadSubmission.LogOutcome(log, "Anthropic", submitted: 2, maximum: 2, JsonNode.Parse("""{"candidates":[]}"""));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("none given", entry.Message);
    }

    [Fact]
    public void A_very_long_note_is_cut_before_it_is_logged()
    {
        var log = new CapturingLogger();
        var input = new JsonObject { ["candidates"] = new JsonArray(), ["note"] = new string('x', 5000) };

        LeadSubmission.LogOutcome(log, "OpenAI", 0, 2, input);

        Assert.True(log.Entries[0].Message.Length < 900);
    }

    [Fact]
    public void Candidates_parse_and_the_note_is_ignored_by_the_parser()
    {
        var input = JsonNode.Parse("""
            {"note":"n","candidates":[{"businessName":"Acme Dental","businessType":"Dental clinic","contactPerson":null,"address":null,
            "city":"Chandigarh","state":null,"phone":null,"phoneSourceUrl":null,"email":null,"website":null,"sourceUrl":"https://acme.test",
            "isIndependentBusiness":false,"isPermanentlyClosed":null,"leadScore":70,"scoreRationale":"near miss"}]}
            """);

        var candidates = LeadSubmission.Parse(input);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Acme Dental", candidate.BusinessName);
        Assert.Null(candidate.Phone);
        Assert.Equal(70, candidate.LeadScore);
    }

    [Fact]
    public void The_note_is_what_the_agent_said_trimmed_and_cut_to_a_length_that_can_be_stored()
    {
        Assert.Equal("Every clinic lacked a named contact.", LeadSubmission.NoteOf(JsonNode.Parse("""{"candidates":[],"note":"  Every clinic lacked a named contact.  "}""")));
        Assert.Equal(600, LeadSubmission.NoteOf(new JsonObject { ["note"] = new string('x', 5000) })!.Length);
    }

    [Theory]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[],"note":""}""")]
    [InlineData("""{"candidates":[],"note":"   "}""")]
    [InlineData("""{"candidates":[],"note":5}""")]
    public void No_note_is_null_so_the_run_does_not_claim_an_explanation_it_does_not_have(string submission) =>
        Assert.Null(LeadSubmission.NoteOf(JsonNode.Parse(submission)));

    [Fact]
    public void A_missing_submission_has_a_note_of_its_own()
    {
        Assert.Contains("without reporting", LeadSubmission.NoSubmissionNote);
        Assert.Null(LeadSubmission.NoteOf(null));
    }
}
