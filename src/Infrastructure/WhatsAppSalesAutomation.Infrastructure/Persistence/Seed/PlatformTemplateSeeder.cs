using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotently inserts the platform's WhatsApp notice templates - one per kind of notice in <see cref="PlatformTemplateCatalog"/> - by event
/// key. Always runs (not a Seed:* gated dev convenience): every environment needs them for tenant notices to reach WhatsApp at all. Insert-only,
/// like <see cref="FaqSeeder"/>: once a row exists the platform admin owns its wording, image and on/off switch, and a restart never touches it
/// again. A template starts Pending - it still has to be pushed to Meta and approved before anything is sent with it.
/// </summary>
public static class PlatformTemplateSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var existing = new HashSet<string>(await db.PlatformMessageTemplates.Select(t => t.EventKey).ToListAsync(), StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var definition in PlatformTemplateCatalog.All)
        {
            if (existing.Contains(definition.Kind.ToString()))
                continue;

            db.PlatformMessageTemplates.Add(new PlatformMessageTemplate
            {
                EventKey = definition.Kind.ToString(),
                Name = definition.Name,
                Language = "en",
                Category = TemplateCategory.Utility,
                WhatsAppTemplateName = definition.WhatsAppTemplateName,
                WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending,
                BodyText = definition.Body,
                IsActive = true
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }
}
