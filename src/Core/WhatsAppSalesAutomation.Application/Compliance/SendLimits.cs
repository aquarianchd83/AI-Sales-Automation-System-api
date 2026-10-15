using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Compliance;

/// <summary>
/// The volume rules that sit beside <see cref="TemplateSendGate"/>: whether the tenant may send at all, how many
/// templates it may send in a day, and how many promotions one contact may receive in a week. All the numbers are
/// starting values to tune from real data (see <see cref="MessagingOptions"/>), and each can be overridden per tenant.
///
/// A queued message counts as sent: the send pipeline reserves a row before it calls WhatsApp, so counting
/// only delivered ones would let a burst overshoot the cap. A failed message does not count - it never
/// reached the customer and did not move the quality rating.
/// </summary>
public static class SendLimits
{
    public static async Task<SendDecision> CheckAsync(
        IApplicationDbContext context,
        Guid tenantId,
        Guid customerId,
        TemplateCategory category,
        MessagingOptions options,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is { SendingPausedAt: not null })
            return SendDecision.Block($"Sending is paused: {tenant.SendingPauseReason ?? "the account is under review"}.");

        var cap = EffectiveDailyCap(options, tenant, now);

        var dayAgo = now.AddHours(-24);
        var sentToday = await context.Messages.CountAsync(
            m => m.Direction == MessageDirection.Outbound
                 && m.MessageType == MessageType.Template
                 && m.Status != MessageStatus.Failed
                 && m.CreatedAt > dayAgo,
            cancellationToken);

        if (sentToday >= cap)
            return SendDecision.Block($"The daily limit of {cap} template messages has been reached. It frees up as earlier messages pass the 24-hour mark.");

        if (category != TemplateCategory.Marketing)
            return SendDecision.Allow;

        var since = now.AddDays(-options.MarketingWindowDays);
        var marketingNames = context.MessageTemplates
            .Where(t => t.Category == TemplateCategory.Marketing)
            .Select(t => t.WhatsAppTemplateName);

        var promotions = await context.Messages.CountAsync(
            m => m.CustomerId == customerId
                 && m.Direction == MessageDirection.Outbound
                 && m.MessageType == MessageType.Template
                 && m.Status != MessageStatus.Failed
                 && m.CreatedAt > since
                 && m.TemplateName != null
                 && marketingNames.Contains(m.TemplateName),
            cancellationToken);

        if (promotions >= options.MaxMarketingPerContactPerWindow)
            return SendDecision.Block(
                $"This contact has already received {promotions} promotional messages in the last {options.MarketingWindowDays} days (limit {options.MaxMarketingPerContactPerWindow}).");

        return SendDecision.Allow;
    }

    /// <summary>
    /// The daily cap in force for a tenant right now. Green quality earns the next step of
    /// <see cref="MessagingOptions.CapStepUps"/> for every <see cref="MessagingOptions.CapStepUpAfterGreenDays"/> it
    /// lasts; Yellow halves whatever cap the tenant had earned (never below 1). A tenant we know nothing about gets the
    /// base cap.
    /// </summary>
    public static int EffectiveDailyCap(MessagingOptions options, Tenant? tenant, DateTime now)
    {
        var cap = options.DailyTemplateCap;
        if (tenant is null)
            return cap;

        var steps = options.CapStepUps ?? Array.Empty<int>();
        if (tenant.QualityRating == WhatsAppQualityRating.Green && steps.Length > 0 && options.CapStepUpAfterGreenDays > 0)
        {
            var greenSince = tenant.QualityRatingSince ?? tenant.CreatedAt;
            var earned = (int)Math.Floor((now - greenSince).TotalDays / options.CapStepUpAfterGreenDays);
            if (earned > 0)
                cap = Math.Max(cap, steps[Math.Min(earned, steps.Length) - 1]);
        }

        // Yellow halves the cap the tenant is on; the earned step is not lost, it is simply not available until the
        // rating recovers, because recovery restarts the Green clock.
        if (tenant.QualityRating == WhatsAppQualityRating.Yellow)
            cap = Math.Max(1, cap / 2);

        return cap;
    }
}
