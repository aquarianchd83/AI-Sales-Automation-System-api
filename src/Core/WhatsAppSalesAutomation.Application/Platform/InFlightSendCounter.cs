using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>How many outbound sends are mid-flight across every tenant: reserved (Queued) but with no provider message id yet.</summary>
public interface IInFlightSendCounter
{
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A send is recorded as Queued BEFORE the provider is called and only gets its provider id once the provider answers (see CampaignSendService), so a Queued
/// row with no id is either being sent right now or was cut short and its outcome is unknown. Switching provider under such a row could send the same
/// message twice (the old provider may already have accepted it), so a switch waits for them. Rows older than <see cref="Window"/> are not counted:
/// a send that old is stuck, not in flight, and must not block a switch forever.
/// </summary>
public class InFlightSendCounter : IInFlightSendCounter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _clock;

    public InFlightSendCounter(IApplicationDbContext context, IDateTimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var since = _clock.UtcNow - Window;
        return _context.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.Direction == MessageDirection.Outbound
                             && m.Status == MessageStatus.Queued
                             && m.WhatsAppMessageId == null
                             && m.CreatedAt >= since, cancellationToken);
    }
}
