using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>Gathers what the CRM already knows about a customer so the agent can use it before it asks
/// them anything.</summary>
public interface ICrmContextBuilder
{
    /// <param name="alreadyKnownKeys">Qualification fields already answered on THIS lead. An earlier
    /// answer for one of them is not worth showing - the current one wins.</param>
    /// <param name="minConfidence">The extraction-confidence floor. An earlier value below it was never
    /// treated as known, so it is not resurfaced as a fact now.</param>
    Task<AiCrmContext> BuildAsync(
        Guid customerId, Guid leadId, Guid conversationId,
        IReadOnlySet<string> alreadyKnownKeys, double minConfidence,
        CancellationToken cancellationToken = default);
}

public class CrmContextBuilder : ICrmContextBuilder
{
    /// <summary>A prompt is not the place for a whole template; the opening is what identifies it.</summary>
    private const int MaxCampaignMessageChars = 400;

    private readonly IApplicationDbContext _context;

    public CrmContextBuilder(IApplicationDbContext context) => _context = context;

    public async Task<AiCrmContext> BuildAsync(
        Guid customerId, Guid leadId, Guid conversationId,
        IReadOnlySet<string> alreadyKnownKeys, double minConfidence,
        CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.IgnoreQueryFilters()
            .Where(c => c.Id == customerId)
            .Select(c => new { c.Source, Tags = c.Tags.Select(t => t.Name).ToList() })
            .FirstOrDefaultAsync(cancellationToken);

        var lastCampaignMessage = await _context.Messages
            .Where(m => m.CustomerId == customerId && m.Direction == MessageDirection.Outbound && m.CampaignCustomerId != null)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Text, m.TemplateName, m.CampaignCustomerId })
            .FirstOrDefaultAsync(cancellationToken);

        string? campaignName = null;
        string? campaignMessage = null;
        if (lastCampaignMessage is not null)
        {
            campaignName = await (
                from cc in _context.CampaignCustomers
                join c in _context.Campaigns on cc.CampaignId equals c.Id
                where cc.Id == lastCampaignMessage.CampaignCustomerId
                select c.Name)
                .FirstOrDefaultAsync(cancellationToken);

            var body = lastCampaignMessage.Text;
            if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(lastCampaignMessage.TemplateName))
            {
                body = await _context.MessageTemplates
                    .Where(t => t.WhatsAppTemplateName == lastCampaignMessage.TemplateName)
                    .Select(t => t.BodyText)
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? lastCampaignMessage.TemplateName;
            }

            campaignMessage = Truncate(body);
        }

        var earlier = await (
            from v in _context.LeadQualificationValues
            join l in _context.Leads on v.LeadId equals l.Id
            where l.CustomerId == customerId && v.LeadId != leadId && !v.IsSuperseded && v.ExtractionConfidence >= minConfidence
            orderby v.CreatedAt descending
            select new { v.FieldKey, v.RawValue })
            .ToListAsync(cancellationToken);

        var activeFields = await _context.QualificationFields
            .Where(f => f.IsActive)
            .ToDictionaryAsync(f => f.FieldKey, f => f.DisplayName, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Newest answer per field, and only for a field the business still asks about.
        var earlierAnswers = earlier
            .Where(v => activeFields.ContainsKey(v.FieldKey) && !alreadyKnownKeys.Contains(v.FieldKey))
            .GroupBy(v => v.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiCapturedField(g.Key, activeFields[g.Key], g.First().RawValue))
            .ToList();

        var previousConversations = await _context.Conversations
            .CountAsync(c => c.CustomerId == customerId && c.Id != conversationId, cancellationToken);

        return new AiCrmContext(
            customer?.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
            customer?.Source,
            campaignName,
            campaignMessage,
            earlierAnswers,
            previousConversations);
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxCampaignMessageChars ? collapsed : collapsed[..MaxCampaignMessageChars];
    }
}
