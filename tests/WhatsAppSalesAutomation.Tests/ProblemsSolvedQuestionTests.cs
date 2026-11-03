using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// "What type of problem does your product solve?" - mandatory for every tenant, answered in points. The story studio writes its video ideas from it, so it
/// has to be asked on every questionnaire, rolled out to questionnaires that already exist, validated as a list, and turned into one readable fact.
/// </summary>
public sealed class ProblemsSolvedQuestionTests : IDisposable
{
    private sealed class PlatformContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;

    public ProblemsSolvedQuestionTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static QuestionnaireDefinition Only() => new() { Questions = { StarterTemplates.ProblemsSolved() } };

    private static Dictionary<string, JsonElement> Answer(object value) =>
        new() { [StarterTemplates.ProblemsSolvedKey] = JsonSerializer.SerializeToElement(value) };

    private static IReadOnlyList<string> Problems(params string[] answer) =>
        QuestionnaireEngine.ValidateForSave(Only(), Answer(answer)).Select(i => i.Message).ToList();

    private async Task SeedAsync()
    {
        var services = new ServiceCollection().AddSingleton<ApplicationDbContext>(_db).BuildServiceProvider();
        await QuestionnaireTemplateSeeder.SeedAsync(services);
    }

    // ----- the question itself -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_starter_questionnaire_asks_it_as_a_required_list_that_cannot_be_skipped_and_that_makes_a_Problems_fact()
    {
        foreach (var starter in StarterTemplates.All())
        {
            var q = Assert.Single(starter.Definition.Questions, x => x.Key == StarterTemplates.ProblemsSolvedKey);
            Assert.Equal(QuestionType.PointList, q.Type);
            Assert.True(q.Required, starter.Key);
            Assert.True(q.RequireKnown, starter.Key);
            Assert.Null(q.ShowWhen);
            Assert.Equal("Problems", q.Fact!.Category);
            Assert.Empty(QuestionnaireEngine.Check(starter.Definition));
        }
    }

    [Fact]
    public void A_list_of_short_distinct_points_is_accepted()
    {
        Assert.Empty(Problems("Slow quotes that take days", "No follow-up after the first visit", "Hidden charges"));
        Assert.Empty(Problems("Slow quotes"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void An_empty_or_too_short_point_is_refused(string point)
    {
        Assert.NotEmpty(Problems("A real problem", point));
    }

    [Fact]
    public void Too_many_too_long_duplicate_or_secret_looking_points_are_refused()
    {
        Assert.Contains(Problems(Enumerable.Range(1, QuestionnaireEngine.MaxPoints + 1).Select(i => $"Problem number {i}").ToArray()), m => m.Contains("at most"));
        Assert.Contains(Problems(new string('x', QuestionnaireEngine.MaxPointLength + 1)), m => m.Contains("100 characters"));
        Assert.Contains(Problems("Slow quotes", "slow quotes"), m => m.Contains("twice"));
        Assert.NotEmpty(Problems("My card is 4111 1111 1111 1111"));
    }

    [Fact]
    public void A_plain_string_or_a_list_of_numbers_is_not_a_list_of_points()
    {
        Assert.NotEmpty(QuestionnaireEngine.ValidateForSave(Only(), Answer("Slow quotes")));
        Assert.NotEmpty(QuestionnaireEngine.ValidateForSave(Only(), Answer(new[] { 1, 2 })));
    }

    [Fact]
    public void The_question_must_be_answered_and_i_dont_know_does_not_count()
    {
        Assert.NotEmpty(QuestionnaireEngine.ValidateForComplete(Only(), new Dictionary<string, JsonElement>()));
        Assert.NotEmpty(QuestionnaireEngine.ValidateForComplete(Only(), Answer(new Dictionary<string, string> { [SpecialAnswer.Property] = SpecialAnswer.Unknown })));
        Assert.Empty(QuestionnaireEngine.ValidateForComplete(Only(), Answer(new[] { "Slow quotes", "No follow-up" })));
    }

    [Fact]
    public void The_points_become_one_numbered_fact_in_the_Problems_category()
    {
        var fact = Assert.Single(QuestionnaireEngine.FactsFrom(Only(), Answer(new[] { "Slow quotes", "No follow-up", "Hidden charges" })));

        Assert.Equal("Problems", fact.Category);
        Assert.Equal("Problems our product solves: 1) Slow quotes; 2) No follow-up; 3) Hidden charges", fact.Statement);
        Assert.True(fact.Statement.Length < 1000);   // a full list still fits a fact
    }

    [Fact]
    public void A_full_list_of_the_longest_points_still_fits_in_one_fact()
    {
        var points = Enumerable.Range(1, QuestionnaireEngine.MaxPoints).Select(i => new string((char)('a' + i), QuestionnaireEngine.MaxPointLength)).ToArray();

        Assert.True(QuestionnaireEngine.FactsFrom(Only(), Answer(points)).Single().Statement.Length <= BusinessFactService.MaxStatement);
    }

    // ----- rolling it out to every tenant ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_install_has_the_question_in_every_questionnaire_from_the_first_version()
    {
        await SeedAsync();

        var versions = await _db.QuestionnaireTemplateVersions.ToListAsync();
        Assert.Equal(StarterTemplates.All().Count, versions.Count);
        Assert.All(versions, v => Assert.Contains(StarterTemplates.ProblemsSolvedKey, QuestionnaireEngine.Parse(v.DefinitionJson).Questions.Select(q => q.Key)));
        Assert.All(versions, v => Assert.Equal(1, v.Number));
    }

    /// <summary>A questionnaire as it was seeded before the question existed.</summary>
    private async Task<QuestionnaireTemplate> OldTemplateAsync(string key, bool published = true, bool draftToo = false, bool alreadyAsks = false)
    {
        var starter = StarterTemplates.All().First();
        var definition = QuestionnaireEngine.Parse(QuestionnaireEngine.Serialize(starter.Definition));
        if (!alreadyAsks)
            definition.Questions.RemoveAll(q => q.Key == StarterTemplates.ProblemsSolvedKey);
        var template = new QuestionnaireTemplate { Key = key, Title = key, Description = "d", IsActive = true };
        _db.QuestionnaireTemplates.Add(template);
        _db.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion
        {
            TemplateId = template.Id, Number = 1, Status = published ? QuestionnaireVersionStatus.Published : QuestionnaireVersionStatus.Draft,
            DefinitionJson = QuestionnaireEngine.Serialize(definition), PublishedAtUtc = published ? DateTime.UtcNow : null
        });
        if (draftToo)
            _db.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion { TemplateId = template.Id, Number = 2, Status = QuestionnaireVersionStatus.Draft, DefinitionJson = QuestionnaireEngine.Serialize(definition) });
        await _db.SaveChangesAsync();
        return template;
    }

    [Fact]
    public async Task A_questionnaire_seeded_before_the_question_existed_gets_a_new_published_version_with_it_and_the_old_one_is_left_alone()
    {
        var old = await OldTemplateAsync("legacy");
        var before = (await _db.QuestionnaireTemplateVersions.SingleAsync()).DefinitionJson;

        await QuestionnaireTemplateSeeder.EnsureMandatoryQuestionsAsync(_db, DateTime.UtcNow);

        var versions = await _db.QuestionnaireTemplateVersions.Where(v => v.TemplateId == old.Id).OrderBy(v => v.Number).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, versions.Select(v => v.Number));
        Assert.All(versions, v => Assert.Equal(QuestionnaireVersionStatus.Published, v.Status));
        Assert.Equal(before, versions[0].DefinitionJson);
        var added = QuestionnaireEngine.Parse(versions[1].DefinitionJson).Questions;
        Assert.Equal(StarterTemplates.ProblemsSolvedKey, added[^1].Key);
        Assert.True(added[^1].Required);
        Assert.Empty(QuestionnaireEngine.Check(QuestionnaireEngine.Parse(versions[1].DefinitionJson)));
    }

    [Fact]
    public async Task Running_it_again_adds_nothing()
    {
        await OldTemplateAsync("legacy");

        await QuestionnaireTemplateSeeder.EnsureMandatoryQuestionsAsync(_db, DateTime.UtcNow);
        await QuestionnaireTemplateSeeder.EnsureMandatoryQuestionsAsync(_db, DateTime.UtcNow);
        await SeedAsync();   // a restart runs the whole seeder again

        var legacyId = (await _db.QuestionnaireTemplates.SingleAsync(t => t.Key == "legacy")).Id;
        Assert.Equal(2, await _db.QuestionnaireTemplateVersions.CountAsync(v => v.TemplateId == legacyId));
    }

    [Fact]
    public async Task A_questionnaire_that_already_asks_it_or_is_being_edited_is_not_touched()
    {
        await OldTemplateAsync("has-it", alreadyAsks: true);
        var editing = await OldTemplateAsync("being-edited", draftToo: true);

        await QuestionnaireTemplateSeeder.EnsureMandatoryQuestionsAsync(_db, DateTime.UtcNow);

        var hasItId = (await _db.QuestionnaireTemplates.SingleAsync(t => t.Key == "has-it")).Id;
        Assert.Equal(1, await _db.QuestionnaireTemplateVersions.CountAsync(v => v.TemplateId == hasItId));
        Assert.Equal(new[] { 1, 2 }, (await _db.QuestionnaireTemplateVersions.Where(v => v.TemplateId == editing.Id).OrderBy(v => v.Number).ToListAsync()).Select(v => v.Number));
    }

    [Fact]
    public async Task A_questionnaire_that_cannot_be_read_is_never_guessed_at()
    {
        var template = new QuestionnaireTemplate { Key = "broken", Title = "b", Description = "d", IsActive = true };
        _db.QuestionnaireTemplates.Add(template);
        _db.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion { TemplateId = template.Id, Number = 1, Status = QuestionnaireVersionStatus.Published, DefinitionJson = "{ not json" });
        await _db.SaveChangesAsync();

        await QuestionnaireTemplateSeeder.EnsureMandatoryQuestionsAsync(_db, DateTime.UtcNow);

        Assert.Equal(1, await _db.QuestionnaireTemplateVersions.CountAsync());
    }
}
