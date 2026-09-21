using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

/// <param name="TenantId">Retrieve AS this tenant, seeing what it would see. Null retrieves as the
/// platform, which sees GLOBAL knowledge only.</param>
public sealed record SimulateRetrievalRequest(
    string Query,
    IReadOnlyList<string>? ConversationContext,
    Guid? TenantId,
    SupportIntent? Intent,
    ProductModule? Module,
    string? CountryCode,
    string? Language,
    string? PlatformVersion);

public interface IKnowledgeRetrievalSimulator
{
    Task<KnowledgeRetrievalResult> SimulateAsync(
        SimulateRetrievalRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}

/// <summary>
/// "What would retrieval return for this question, for this tenant?" - the tool content authors use to
/// write and check articles (§V.3), and the first place anyone looks when an answer was wrong.
///
/// It runs the REAL pipeline, not a copy of it, and returns the full diagnostics. A simulator with its
/// own logic would agree with production only until the day one of them changed.
///
/// Acting as a tenant is a cross-tenant read, so it is audited. The audit entry records who and for
/// which tenant but deliberately NOT the query text: an author testing with a pasted real ticket would
/// otherwise copy a customer's message into a log that far more people can read than the ticket.
/// </summary>
public sealed class KnowledgeRetrievalSimulator : IKnowledgeRetrievalSimulator
{
    public const int MaxQueryLength = 4000;

    private readonly IKnowledgeRetrievalService _retrieval;
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IPlatformAuditService _audit;

    public KnowledgeRetrievalSimulator(
        IKnowledgeRetrievalService retrieval, IApplicationDbContext context, ITenantContext tenantContext, IPlatformAuditService audit)
    {
        _retrieval = retrieval;
        _context = context;
        _tenantContext = tenantContext;
        _audit = audit;
    }

    public async Task<KnowledgeRetrievalResult> SimulateAsync(
        SimulateRetrievalRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw Invalid("query", "A query is required.");

        if (request.Query.Length > MaxQueryLength)
            throw Invalid("query", $"A query may be at most {MaxQueryLength} characters.");

        string? country = request.CountryCode;

        if (request.TenantId is { } tenantId)
        {
            var tenant = await _context.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
                ?? throw new NotFoundException(nameof(Tenant), tenantId);

            // Retrieval reads the tenant from ambient context, so the simulation has to BE that tenant
            // for the rest of this request - both for the ambient query filters and for the tenant's
            // own embedding configuration, which decides which vectors are comparable.
            _tenantContext.SetTenant(tenantId);

            // What the real agent would use, unless the author is deliberately asking a what-if.
            country ??= tenant.CountryCode;
        }

        var result = await _retrieval.RetrieveAsync(
            new KnowledgeRetrievalRequest(
                request.Query,
                request.ConversationContext ?? Array.Empty<string>(),
                request.Intent,
                request.Module,
                country,
                string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language,
                request.PlatformVersion,
                BypassCache: true),
            cancellationToken);

        await _audit.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.KnowledgeRetrievalSimulated,
            targetTenantId: request.TenantId,
            details: $"Query of {request.Query.Length} characters; returned {result.Evidence.Count} chunks; gate " +
                     (result.Diagnostics.EvidenceGatePassed ? "passed" : "failed: " + result.Diagnostics.GateFailureReason),
            cancellationToken: cancellationToken);

        return result;
    }

    private static ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
