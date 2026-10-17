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
    }
}
