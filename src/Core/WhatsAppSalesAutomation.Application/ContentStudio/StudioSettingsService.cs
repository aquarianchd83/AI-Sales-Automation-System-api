using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

public class StudioSettingsService : IStudioSettingsService
{
    private readonly IApplicationDbContext _context;

    public StudioSettingsService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<StudioSettingsDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await _context.TenantStudioSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        return new StudioSettingsDto(row?.ApprovalPolicy ?? StudioApprovalPolicy.AdminOnly);
    }

    public async Task<StudioSettingsDto> UpdateAsync(Guid tenantId, UpdateStudioSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.ApprovalPolicy))
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.ApprovalPolicy), "Choose who can approve content.") });

        var row = await _context.TenantStudioSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            row = new TenantStudioSettings { TenantId = tenantId };
            _context.TenantStudioSettings.Add(row);
        }

        row.ApprovalPolicy = request.ApprovalPolicy;
        await _context.SaveChangesAsync(cancellationToken);
        return new StudioSettingsDto(row.ApprovalPolicy);
    }
}
