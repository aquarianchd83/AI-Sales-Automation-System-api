using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <inheritdoc />
public class ConsentPageSettingsService : IConsentPageSettingsService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _dateTime;

    public ConsentPageSettingsService(IApplicationDbContext context, ITenantContext tenantContext, IDateTimeProvider dateTime)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dateTime = dateTime;
    }

    public async Task<ConsentPageSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var page = await FindAsync(tenantId, cancellationToken);
        return ToDto(page, await ProductLabelAsync(tenantId, cancellationToken));
    }

    public async Task<ConsentPageSettingsDto> SaveAsync(SaveConsentPageSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var bad = ConsentPageRules.ValidateFormat(request);
        if (bad.Count > 0)
            throw Invalid(bad);

        var page = await FindAsync(tenantId, cancellationToken);
        if (page is null)
        {
            page = new TenantConsentPage { TenantId = tenantId };
            _context.TenantConsentPages.Add(page);
        }

        page.BusinessName = Clean(request.BusinessName);
        page.ProductDescription = Clean(request.ProductDescription);
        page.AdditionalInfo = Clean(request.AdditionalInfo);
        page.ConsentStatement = Clean(request.ConsentStatement);
        page.PrivacyPolicyUrl = Clean(request.PrivacyPolicyUrl);
        page.TermsUrl = Clean(request.TermsUrl);
        page.HasUnpublishedChanges = true;

        await SaveChangesAsync(cancellationToken);
        return ToDto(page, await ProductLabelAsync(tenantId, cancellationToken));
    }

    public async Task<ConsentPagePreviewDto> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var page = await FindAsync(tenantId, cancellationToken);
        var product = await ProductLabelAsync(tenantId, cancellationToken);
        var name = page?.BusinessName ?? string.Empty;

        var content = new ConsentPageContentDto(
            name, page?.ProductDescription ?? string.Empty, page?.AdditionalInfo,
            ConsentPageRules.ResolveStatement(page?.ConsentStatement, name, product),
            page?.PrivacyPolicyUrl, page?.TermsUrl, Version: 0);

        return new ConsentPagePreviewDto(content, IssuesOf(page));
    }

    public async Task<ConsentPageSettingsDto> PublishAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var page = await FindAsync(tenantId, cancellationToken);

        var issues = IssuesOf(page);
        if (page is null || issues.Count > 0)
            throw Invalid(issues.Count > 0 ? issues : new List<ConsentPageIssueDto> { new("businessName", "Fill in the consent page before publishing.") });

        var product = await ProductLabelAsync(tenantId, cancellationToken);
        var name = page.BusinessName!.Trim();

        var statement = ConsentPageRules.ResolveStatement(page.ConsentStatement, name, product);
        if (statement.Length > ConsentPageRules.MaxResolvedStatement)
            throw Invalid(new[] { new ConsentPageIssueDto("consentStatement", "The consent statement is too long once the business and product names are filled in. Shorten it.") });

        page.PublishedVersion += 1;
        page.PublishedAtUtc = _dateTime.UtcNow;
        page.IsActive = true;
        page.HasUnpublishedChanges = false;

        // An immutable copy, with the statement resolved to the exact words the customer will read. Past decisions point at their own version.
        _context.ConsentPageVersions.Add(new ConsentPageVersion
        {
            TenantId = tenantId,
            Version = page.PublishedVersion,
            BusinessName = name,
            ProductLabel = product,
            ProductDescription = page.ProductDescription!.Trim(),
            AdditionalInfo = page.AdditionalInfo,
            ConsentStatement = statement,
            PrivacyPolicyUrl = page.PrivacyPolicyUrl,
            TermsUrl = page.TermsUrl,
            PublishedAtUtc = page.PublishedAtUtc.Value
        });

        await SaveChangesAsync(cancellationToken);
        return ToDto(page, product);
    }

    public async Task<ConsentPageSettingsDto> SetActiveAsync(bool active, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var page = await FindAsync(tenantId, cancellationToken)
            ?? throw new ConflictException("There is no consent page to switch on or off yet.");

        if (active && page.PublishedVersion == 0)
            throw new ConflictException("Publish the consent page first.");

        page.IsActive = active;
        await SaveChangesAsync(cancellationToken);
        return ToDto(page, await ProductLabelAsync(tenantId, cancellationToken));
    }

    // ---------------------------------------------------------------------------------------------

    private Guid RequireTenant() => _tenantContext.TenantId ?? throw new InvalidOperationException("The consent page belongs to a tenant.");

    private Task<TenantConsentPage?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _context.TenantConsentPages.FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

    private async Task<string?> ProductLabelAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var product = await _context.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.ProductName).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(product))
            return null;

        var label = product.Trim();
        return label.Length <= ConsentPageRules.MaxProductLabel ? label : label[..ConsentPageRules.MaxProductLabel];
    }

    private static List<ConsentPageIssueDto> IssuesOf(TenantConsentPage? p) =>
        ConsentPageRules.ValidateForPublish(p?.BusinessName, p?.ProductDescription, p?.AdditionalInfo, p?.ConsentStatement, p?.PrivacyPolicyUrl, p?.TermsUrl);

    private static ConsentPageSettingsDto ToDto(TenantConsentPage? p, string? product)
    {
        var issues = IssuesOf(p);
        var name = p?.BusinessName?.Trim() ?? string.Empty;

        var status = p is null || (p.PublishedVersion == 0 && issues.Count > 0) ? ConsentPageStatuses.Incomplete
            : p.PublishedVersion == 0 ? ConsentPageStatuses.Complete
            : p.IsActive ? ConsentPageStatuses.Published
            : ConsentPageStatuses.Inactive;

        return new ConsentPageSettingsDto(
            p?.BusinessName, p?.ProductDescription, p?.AdditionalInfo, p?.ConsentStatement,
            ConsentPageRules.ResolveStatement(null, name.Length > 0 ? name : ConsentPageRules.BusinessNameToken, product),
            p?.PrivacyPolicyUrl, p?.TermsUrl, status, p?.IsActive ?? true, p?.PublishedVersion ?? 0, p?.PublishedAtUtc,
            p?.HasUnpublishedChanges ?? false, issues);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique (tenant) / (tenant, version) indexes: two requests raced to create the page or to publish the same number.
            throw new ConflictException("The consent page was changed at the same time. Reload and try again.");
        }
    }

    private static ValidationException Invalid(IEnumerable<ConsentPageIssueDto> issues) =>
        new(issues.Select(i => new ValidationFailure(i.Field, i.Message)));
}
