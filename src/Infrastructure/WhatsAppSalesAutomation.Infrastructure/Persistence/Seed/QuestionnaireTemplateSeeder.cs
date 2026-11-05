using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotently inserts the platform's starter questionnaires (generic, healthcare, software, manufacturing) by template key, each
/// with a published first version. Insert-only, like the other catalog seeders: once a template's key exists this never touches it
/// again, so everything a platform admin has since edited, versioned or switched off survives a restart. Always runs - without a
/// published questionnaire no tenant could use the AI Content Studio at all.
/// The one exception to "never touches again" is a mandatory question (see <see cref="EnsureMandatoryQuestionsAsync"/>): it is added as a NEW published
/// version, never by editing an existing one.
/// </summary>
public static class QuestionnaireTemplateSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var existing = await db.QuestionnaireTemplates.Select(t => t.Key).ToListAsync();
        var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var starter in StarterTemplates.All())
        {
            if (known.Contains(starter.Key))
                continue;

            var json = QuestionnaireEngine.Serialize(starter.Definition);
            if (QuestionnaireEngine.TryParse(json).Issues.Count > 0)
                continue; // a starter that fails its own checks is a bug caught by the tests; never insert something the engine would refuse

            var template = new QuestionnaireTemplate
            {
                Key = starter.Key,
                Title = starter.Title,
                Description = starter.Description,
                Industry = starter.Industry,
                IsActive = true
            };
            db.QuestionnaireTemplates.Add(template);
            db.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion
            {
                TemplateId = template.Id,
                Number = 1,
                Status = QuestionnaireVersionStatus.Published,
                DefinitionJson = json,
                PublishedAtUtc = now
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();

        await EnsureMandatoryQuestionsAsync(db, now);
    }

    /// <summary>
    /// Questions every tenant must answer, whichever questionnaire it is on. A questionnaire that was seeded before such a question existed gets a new
    /// published version with the question added at the end; tenants then see the usual "newer version" step and answer it. The earlier versions are
    /// left exactly as they were. A template that already asks the question (a new install, or an operator who wrote it) is not touched, and neither is
    /// one with an unpublished draft - the operator is editing it, and the next start tries again.
    /// </summary>
    public static async Task EnsureMandatoryQuestionsAsync(ApplicationDbContext db, DateTime now)
    {
        var mandatory = new[] { StarterTemplates.ProblemsSolved() };
        var changed = false;

        foreach (var template in await db.QuestionnaireTemplates.ToListAsync())
        {
            var versions = await db.QuestionnaireTemplateVersions.Where(v => v.TemplateId == template.Id).ToListAsync();
            if (versions.Any(v => v.Status == QuestionnaireVersionStatus.Draft))
                continue;

            var latest = versions.Where(v => v.Status == QuestionnaireVersionStatus.Published).OrderByDescending(v => v.Number).FirstOrDefault();
            if (latest is null)
                continue;

            var (definition, _) = QuestionnaireEngine.TryParse(latest.DefinitionJson);
            if (definition is null)
                continue; // unreadable: never guess at what an operator wrote

            var missing = mandatory.Where(m => definition.Questions.All(q => !string.Equals(q.Key, m.Key, StringComparison.Ordinal))).ToList();
            if (missing.Count == 0)
                continue;

            definition.Questions.AddRange(missing);
            var json = QuestionnaireEngine.Serialize(definition);
            if (QuestionnaireEngine.TryParse(json).Issues.Count > 0)
                continue; // adding it would make the questionnaire invalid, so leave it as the operator has it

            db.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion
            {
                TemplateId = template.Id,
                Number = versions.Max(v => v.Number) + 1,
                Status = QuestionnaireVersionStatus.Published,
                DefinitionJson = json,
                PublishedAtUtc = now
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }
}
