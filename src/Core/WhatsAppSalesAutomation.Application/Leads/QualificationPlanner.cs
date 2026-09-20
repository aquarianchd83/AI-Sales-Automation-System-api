using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public class QualificationPlanner : IQualificationPlanner
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly ILogger<QualificationPlanner> _logger;

    public QualificationPlanner(
        IApplicationDbContext context,
        ITenantConfigOverrideProvider tenantConfig,
        ILogger<QualificationPlanner> logger)
    {
        _context = context;
        _tenantConfig = tenantConfig;
        _logger = logger;
    }

    public async Task<QualificationPlan> PlanAsync(
        Guid leadId, bool qualificationPaused, CancellationToken cancellationToken = default)
    {
        var options = await _tenantConfig.GetAiOptionsAsync(cancellationToken);

        var fields = await _context.QualificationFields
            .Where(f => f.IsActive)
            .OrderByDescending(f => f.IsRequired)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey)
            .ToListAsync(cancellationToken);

        var values = await _context.LeadQualificationValues
            .Where(v => v.LeadId == leadId && !v.IsSuperseded)
            .ToListAsync(cancellationToken);

        // A value the model was unsure about is on record but does not count as known, so the agent
        // still asks properly rather than treating a half-understood answer as settled.
        var known = values
            .Where(v => v.ExtractionConfidence >= options.MinFieldExtractionConfidence)
            .ToList();

        var knownKeys = known.Select(v => v.FieldKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byKey = fields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);

        var knownForPrompt = known
            // A value whose field was since deactivated is still known - the customer did tell us, and
            // hiding it because the schema changed afterwards would invite the agent to ask again.
            .Select(v => new AiCapturedField(
                v.FieldKey,
                byKey.TryGetValue(v.FieldKey, out var f) ? f.DisplayName : v.FieldKey,
                v.RawValue))
            .ToList();

        // Only the top few, not everything outstanding. Handing the model a list of twelve open fields
        // is an invitation to work through them, and the order was decided here anyway - the rest would
        // only spend tokens.
        var toAsk = qualificationPaused
            ? new List<AiQualificationField>()
            : fields
                .Where(f => !knownKeys.Contains(f.FieldKey))
                .Take(Math.Max(0, options.MaxFieldsToAsk))
                .Select(ToPromptField)
                .ToList();

        return new QualificationPlan(
            fields.Select(ToPromptField).ToList(),
            knownForPrompt,
            toAsk,
            AllRequiredCaptured: fields.Where(f => f.IsRequired).All(f => knownKeys.Contains(f.FieldKey)),
            CapturedCount: fields.Count(f => knownKeys.Contains(f.FieldKey)),
            TotalCount: fields.Count);
    }

    public async Task<IReadOnlyList<AcceptedField>> CaptureAsync(
        Guid leadId,
        Guid? inboundMessageId,
        IReadOnlyList<AiExtractedField> extracted,
        CancellationToken cancellationToken = default)
    {
        if (extracted.Count == 0)
            return Array.Empty<AcceptedField>();

        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken);
        if (lead is null)
            return Array.Empty<AcceptedField>();

        var keys = extracted.Select(e => e.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var fields = await _context.QualificationFields
            .Where(f => f.IsActive && keys.Contains(f.FieldKey))
            .ToListAsync(cancellationToken);

        var current = await _context.LeadQualificationValues
            .Where(v => v.LeadId == leadId && !v.IsSuperseded && keys.Contains(v.FieldKey))
            .ToListAsync(cancellationToken);

        var accepted = new List<AcceptedField>();

        // Last one wins within a single turn: a model that reports the same field twice has changed its
        // mind mid-answer, and the later claim is the one it settled on.
        foreach (var claim in extracted.GroupBy(e => e.FieldKey, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            var field = fields.FirstOrDefault(f =>
                string.Equals(f.FieldKey, claim.FieldKey, StringComparison.OrdinalIgnoreCase));

            if (field is null)
            {
                // Not in this tenant's schema. Dropped rather than stored, and logged rather than
                // ignored - a provider inventing field keys is worth knowing about.
                _logger.LogWarning(
                    "AI returned qualification field '{FieldKey}' that is not in the tenant's active schema - dropped.",
                    claim.FieldKey);
                continue;
            }

            var normalized = QualificationValueNormalizer.Normalize(field, claim.Value);
            if (!normalized.IsAccepted)
            {
                _logger.LogInformation(
                    "AI value for '{FieldKey}' rejected: {Reason}", claim.FieldKey, normalized.RejectionReason);
                continue;
            }

            var existing = current.FirstOrDefault(v =>
                string.Equals(v.FieldKey, field.FieldKey, StringComparison.OrdinalIgnoreCase));

            // An unchanged answer is left alone. Superseding a row with an identical one would churn the
            // capture history and bury the turns where the customer genuinely changed their mind.
            if (existing is not null && string.Equals(existing.RawValue, normalized.RawValue, StringComparison.Ordinal))
                continue;

            if (existing is not null)
                existing.IsSuperseded = true;

            _context.LeadQualificationValues.Add(new LeadQualificationValue
            {
                TenantId = lead.TenantId,
                LeadId = leadId,
                FieldId = field.Id,
                FieldKey = field.FieldKey,
                RawValue = normalized.RawValue,
                NormalizedValue = normalized.NormalizedValue,
                CapturedFromMessageId = inboundMessageId,
                CapturedByUserId = null,
                ExtractionConfidence = Math.Clamp(claim.Confidence, 0, 1)
            });

            MirrorOntoLead(lead, field.FieldKey, normalized.RawValue);

            accepted.Add(new AcceptedField(
                field.FieldKey, normalized.RawValue, normalized.NormalizedValue, existing is not null));
        }

        // New becomes Qualifying the moment we learn anything at all - "we are talking about
        // requirements now". Not Qualified, which stays a human judgement rather than something the
        // agent decides on its own.
        //
        // This moved here from LeadService.ApplyAiExtractedAttributesAsync when the orchestrator
        // stopped going through that method: the transition belongs with capture, because capture is
        // what makes it true.
        if (accepted.Count > 0 && lead.Stage == LeadStage.New)
        {
            _context.LeadActivities.Add(new LeadActivity
            {
                TenantId = lead.TenantId,
                LeadId = lead.Id,
                ActivityType = LeadActivityType.StageChanged,
                OldValue = lead.Stage.ToString(),
                NewValue = LeadStage.Qualifying.ToString(),
                CreatedBy = null
            });

            lead.Stage = LeadStage.Qualifying;
        }

        return accepted;
    }

    /// <summary>Keeps Lead.Budget/Interest/PurchaseTimeline in step with the three mirrored keys, so the
    /// existing lead list, filters and reports keep working while the UI moves to dynamic fields.</summary>
    private static void MirrorOntoLead(Lead lead, string fieldKey, string rawValue)
    {
        switch (fieldKey.ToLowerInvariant())
        {
            case Domain.Constants.QualificationDefaults.BudgetKey:
                lead.Budget = rawValue;
                break;
            case Domain.Constants.QualificationDefaults.InterestKey:
                lead.Interest = rawValue;
                break;
            case Domain.Constants.QualificationDefaults.PurchaseTimelineKey:
                lead.PurchaseTimeline = rawValue;
                break;
        }
    }

    private static AiQualificationField ToPromptField(QualificationField f)
    {
        var allowed = QualificationValueNormalizer.ParseAllowedValues(f.AllowedValuesJson);

        return new AiQualificationField(
            f.FieldKey,
            f.DisplayName,
            f.Description,
            f.Question,
            f.DataType,
            allowed.Count > 0 ? allowed : null);
    }
}
