using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public record PlatformTicketDto(
    Guid Id,
    Guid? TenantId,
    string? TenantName,
    PlatformTicketKind Kind,
    string Subject,
    string Description,
    PlatformTicketPriority Priority,
    PlatformTicketStatus Status,
    string? DeliveryNote,
    DateTime CreatedAt,
    DateTime? ResolvedAtUtc);

public record UpdatePlatformTicketStatusRequest(PlatformTicketStatus Status);

/// <summary>The product owner's ticket list in the Platform Admin Console.</summary>
public interface IPlatformTicketService
{
    /// <summary>Open and in-progress first, most urgent then newest; <paramref name="status"/> narrows to one status.</summary>
    Task<IReadOnlyList<PlatformTicketDto>> GetAsync(PlatformTicketStatus? status, CancellationToken cancellationToken = default);

    Task<PlatformTicketDto> SetStatusAsync(Guid id, PlatformTicketStatus status, CancellationToken cancellationToken = default);
}

public class PlatformTicketService : IPlatformTicketService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public PlatformTicketService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<IReadOnlyList<PlatformTicketDto>> GetAsync(PlatformTicketStatus? status, CancellationToken cancellationToken = default)
    {
        var query = _context.PlatformTickets.AsNoTracking();
        if (status is { } s)
            query = query.Where(t => t.Status == s);

        var rows = await query.ToListAsync(cancellationToken);
        var ordered = rows
            .OrderBy(t => t.Status == PlatformTicketStatus.Resolved)
            .ThenByDescending(t => t.Priority)
            .ThenByDescending(t => t.CreatedAt)
            .Take(200)
            .ToList();

        return await ToDtosAsync(ordered, cancellationToken);
    }

    public async Task<PlatformTicketDto> SetStatusAsync(Guid id, PlatformTicketStatus status, CancellationToken cancellationToken = default)
    {
        var ticket = await _context.PlatformTickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlatformTicket), id);

        ticket.Status = status;
        ticket.ResolvedAtUtc = status == PlatformTicketStatus.Resolved ? _dateTime.UtcNow : null;
        await _context.SaveChangesAsync(cancellationToken);

        return (await ToDtosAsync(new[] { ticket }, cancellationToken))[0];
    }

    private async Task<IReadOnlyList<PlatformTicketDto>> ToDtosAsync(IReadOnlyCollection<PlatformTicket> tickets, CancellationToken cancellationToken)
    {
        var tenantIds = tickets.Where(t => t.TenantId != null).Select(t => t.TenantId!.Value).Distinct().ToList();
        var names = tenantIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return tickets.Select(t => new PlatformTicketDto(
            t.Id, t.TenantId, t.TenantId is { } id ? names.GetValueOrDefault(id) : null, t.Kind, t.Subject, t.Description, t.Priority, t.Status,
            t.DeliveryNote, t.CreatedAt, t.ResolvedAtUtc)).ToList();
    }
}

/// <summary>
/// What happens when a tenant runs into a platform setting only the product owner can change. Raises one Urgent ticket and tells the product
/// owner three ways: the bell (an in-app alert), email and WhatsApp to the number on their profile. Never throws - a tenant trying
/// their knowledge must not be failed because an alert could not be sent.
/// </summary>
public interface IPlatformSetupAlerts
{
    /// <summary>The platform's AI (chat or embedding) provider is Simulated. A ticket is already open for it? Then nothing more is sent, so a tenant
    /// who keeps retrying does not flood the owner.</summary>
    Task ReportSimulatedAiAsync(Guid tenantId, string area, CancellationToken cancellationToken = default);
}

public class PlatformSetupAlerts : IPlatformSetupAlerts
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPlatformNotifier _notifier;
    private readonly IPlatformWhatsAppSender _whatsApp;
    private readonly IPlatformNoticeTemplates _templates;
    private readonly ILogger<PlatformSetupAlerts> _logger;

    public PlatformSetupAlerts(
        IApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IPlatformNotifier notifier,
        IPlatformWhatsAppSender whatsApp,
        IPlatformNoticeTemplates templates,
        ILogger<PlatformSetupAlerts> logger)
    {
        _context = context;
        _userManager = userManager;
        _notifier = notifier;
        _whatsApp = whatsApp;
        _templates = templates;
        _logger = logger;
    }

    public async Task ReportSimulatedAiAsync(Guid tenantId, string area, CancellationToken cancellationToken = default)
    {
        try
        {
            const PlatformTicketKind kind = PlatformTicketKind.SimulatedAiConfiguration;
            var key = kind.ToString();

            if (await _context.PlatformTickets.AnyAsync(t => t.DedupeKey == key && t.Status != PlatformTicketStatus.Resolved, cancellationToken))
                return;

            var tenantName = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? "A tenant";

            var detail = $"{tenantName} used {area}, but the platform's AI provider is set to Simulated, so answers are not real and published knowledge cannot be matched properly. "
                + "Switch the AI provider and embedding provider to a real one (Platform Admin > Integrations), then ask the tenant to press \"Re-index for chat\" on Product knowledge > Published knowledge.";

            var ticket = new PlatformTicket
            {
                TenantId = tenantId,
                Kind = kind,
                DedupeKey = key,
                Subject = "URGENT: AI provider is set to Simulated - tenants cannot get real answers",
                Description = detail,
                Priority = PlatformTicketPriority.Urgent,
                Status = PlatformTicketStatus.Open
            };
            _context.PlatformTickets.Add(ticket);
            await _context.SaveChangesAsync(cancellationToken);

            var notes = new List<string>();

            var raised = await _notifier.NotifyAsync(new PlatformNotificationRequest(
                PlatformNotificationKind.ConfigurationNeeded, PlatformNotificationSeverity.Critical, ticket.Id.ToString(),
                ticket.Subject, detail, tenantId), cancellationToken);
            notes.Add(raised ? "in-app: sent" : "in-app: not raised");

            notes.Add(await SendWhatsAppAsync(tenantName, ticket.Subject, $"{tenantName} used {area} while the AI provider is set to Simulated.", cancellationToken));

            ticket.DeliveryNote = string.Join("; ", notes);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not raise the Simulated-AI ticket for tenant {TenantId}", tenantId);
        }
    }

    private async Task<string> SendWhatsAppAsync(string tenantName, string title, string message, CancellationToken cancellationToken)
    {
        var phones = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == null && u.IsActive && u.PhoneNumber != null && u.PhoneNumber != string.Empty)
            .Select(u => u.PhoneNumber!)
            .ToListAsync(cancellationToken);

        if (phones.Count == 0)
            return "whatsapp: no phone number on the product owner's profile";

        var resolution = await _templates.ResolveAsync(TenantNotificationKind.PlatformSetupNeeded, tenantName, title, message, cancellationToken);
        if (resolution.Message is not { } m)
            return resolution.SkipNote ?? "whatsapp: no template";

        int sent = 0, failed = 0;
        string? note = null;
        foreach (var phone in phones.Select(ToDigits).Where(p => p.Length > 0).Distinct())
        {
            var result = await _whatsApp.SendTemplateAsync(phone, m.TemplateName, m.Language, m.Parameters, cancellationToken, m.MediaUrl);
            if (result.Skipped) note ??= result.Note;
            else if (result.Success) sent++;
            else { failed++; note ??= result.Note; }
        }

        return sent > 0 ? $"whatsapp: sent to {sent}" : failed > 0 ? $"whatsapp: failed ({note})" : $"whatsapp: skipped ({note})";
    }

    /// <summary>The Cloud API wants the number as digits with the country code and no +.</summary>
    internal static string ToDigits(string phone) => new(phone.Where(char.IsDigit).ToArray());
}
