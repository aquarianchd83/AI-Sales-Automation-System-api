using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One row of the Payments screen - money that actually moved: a plan subscription, a credit pack, or a
/// refund (negative). Amounts are in that tenant's own currency (snapshotted when it was charged) and in base USD;
/// like everything per-tenant, the local figures are not comparable across rows - total the USD ones.</summary>
public record PlatformPaymentListItemDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string Kind,
    string Description,
    int AmountCents,
    string CurrencyCode,
    string CurrencySymbol,
    decimal LocalAmount,
    string Provider,
    DateTime PaidAtUtc,
    Guid? RefundOfPaymentId,
    string? CountryCode = null,
    string? StateCode = null,
    decimal TaxLocal = 0,
    decimal TotalLocal = 0,
    IReadOnlyList<TaxLineDto>? TaxLines = null,
    decimal AmountInr = 0);

public record PlatformPaymentQuery : PagedRequest
{
    public PaymentKind? Kind { get; init; }

    public Guid? TenantId { get; init; }
}

/// <summary>The Platform Admin Console's Payments screen: every payment across tenants. Read-only - money moves
/// through checkout, credit purchases and refunds, never from this list. Replaces the old usage-invoice screen: with
/// prepaid billing nothing is billed afterwards, so there is nothing to invoice, only payments to look back on.</summary>
public interface IPlatformPaymentService
{
    Task<PagedResult<PlatformPaymentListItemDto>> GetPagedAsync(PlatformPaymentQuery query, CancellationToken cancellationToken = default);
}

public class PlatformPaymentService : IPlatformPaymentService
{
    private readonly IApplicationDbContext _context;

    public PlatformPaymentService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PagedResult<PlatformPaymentListItemDto>> GetPagedAsync(PlatformPaymentQuery query, CancellationToken cancellationToken = default)
    {
        var payments = _context.Payments.IgnoreQueryFilters()
            .Join(_context.Tenants, p => p.TenantId, t => t.Id, (p, t) => new { p, t });

        if (query.Kind is { } kind)
            payments = payments.Where(x => x.p.Kind == kind);

        if (query.TenantId is { } tenantId)
            payments = payments.Where(x => x.p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            payments = payments.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search) || x.p.PlanName.Contains(search));
        }

        var totalCount = await payments.CountAsync(cancellationToken);

        var rows = await payments
            .OrderByDescending(x => x.p.PaidAtUtc).ThenBy(x => x.t.Name)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x => new PlatformPaymentListItemDto(
                x.p.Id, x.p.TenantId, x.t.Name, x.p.Kind.ToString(), x.p.PlanName, x.p.AmountCents,
                x.p.CurrencyCode, x.p.CurrencySymbol, x.p.LocalAmount, x.p.Provider, x.p.PaidAtUtc, x.p.RefundOfPaymentId,
                x.p.CountryCode, x.p.StateCode, x.p.TaxLocal, x.p.TotalPaidLocal, PaymentDto.ParseTaxLines(x.p.TaxLinesJson), x.p.AmountInr))
            .ToList();

        return new PagedResult<PlatformPaymentListItemDto>(items, totalCount, query.Page, query.PageSize);
    }
}
