using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The admin simulator: it must run the real pipeline as the right tenant, bypass the cache,
/// and leave an audit trail that does not itself leak the customer text it was given.</summary>
public sealed class KnowledgeRetrievalSimulatorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly RecordingRetrieval _retrieval = new();
    private readonly RecordingAudit _audit = new();
    private readonly SwitchableTenant _tenantContext = new();
    private readonly KnowledgeRetrievalSimulator _simulator;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme", CountryCode = "IN", Status = TenantStatus.Active };

    public KnowledgeRetrievalSimulatorTests()
    {
        _connection.Open();
        _db = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        // What retrieval would see through ITenantContext AT THE MOMENT it runs - the whole point of
        // asserting on it is ordering: the scope must be set before retrieval reads it.
        _retrieval.ReadTenant = () => _tenantContext.TenantId;

        _simulator = new KnowledgeRetrievalSimulator(_retrieval, _db, _tenantContext, _audit);
    }

    private static SimulateRetrievalRequest Request(string query = "refund", Guid? tenantId = null, string? country = null) =>
        new(query, null, tenantId, null, null, country, null, null);

    [Fact]
    public async Task Acting_as_a_tenant_puts_the_scope_in_place_before_retrieval_runs()
    {
        await _simulator.SimulateAsync(Request(tenantId: _tenant.Id), Guid.NewGuid(), "admin@x.com");

        // Retrieval reads the tenant from ambient context, so the scope has to be set BEFORE it runs -
        // both for the query filters and for which embedding provider the tenant uses.
        Assert.Equal(_tenant.Id, _retrieval.TenantSeenByRetrieval);
    }

    [Fact]
    public async Task With_no_tenant_the_simulation_runs_as_the_platform()
    {
        await _simulator.SimulateAsync(Request(), Guid.NewGuid(), "admin@x.com");

        Assert.Null(_retrieval.TenantSeenByRetrieval);
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found_rather_than_silently_returning_nothing()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _simulator.SimulateAsync(Request(tenantId: Guid.NewGuid()), Guid.NewGuid(), "admin@x.com"));

        Assert.Empty(_retrieval.Requests);
    }

    [Fact]
    public async Task The_tenants_own_country_is_used_unless_a_what_if_overrides_it()
    {
        await _simulator.SimulateAsync(Request(tenantId: _tenant.Id), Guid.NewGuid(), "a@x.com");
        Assert.Equal("IN", _retrieval.Requests[0].TenantCountry);

        await _simulator.SimulateAsync(Request(tenantId: _tenant.Id, country: "AE"), Guid.NewGuid(), "a@x.com");
        Assert.Equal("AE", _retrieval.Requests[1].TenantCountry);
    }

    [Fact]
    public async Task The_result_cache_is_bypassed()
    {
        // Right after publishing an article, an answer up to a minute old is the wrong thing to show
        // someone checking whether the publish worked.
        await _simulator.SimulateAsync(Request(), Guid.NewGuid(), "a@x.com");

        Assert.True(_retrieval.Requests.Single().BypassCache);
    }

    [Fact]
    public async Task The_audit_entry_names_the_actor_and_tenant_but_not_the_query_text()
    {
        var actor = Guid.NewGuid();

        await _simulator.SimulateAsync(
            Request("My customer Rahul at rahul@private.example says his card was charged twice", _tenant.Id), actor, "admin@x.com");

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(PlatformAuditActions.KnowledgeRetrievalSimulated, entry.Action);
        Assert.Equal(actor, entry.Actor);
        Assert.Equal(_tenant.Id, entry.TargetTenantId);
        // An author testing with a pasted real ticket would otherwise copy the customer's message into
        // a log far more people can read than the ticket.
        Assert.DoesNotContain("Rahul", entry.Details);
        Assert.DoesNotContain("private.example", entry.Details);
        Assert.Contains("characters", entry.Details);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_query_is_rejected(string query)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _simulator.SimulateAsync(Request(query), Guid.NewGuid(), "a@x.com"));
    }

    [Fact]
    public async Task An_oversized_query_is_rejected()
    {
        var huge = new string('x', KnowledgeRetrievalSimulator.MaxQueryLength + 1);

        await Assert.ThrowsAsync<ValidationException>(() => _simulator.SimulateAsync(Request(huge), Guid.NewGuid(), "a@x.com"));
    }

    [Fact]
    public async Task A_rejected_request_leaves_no_audit_entry_and_never_reaches_retrieval()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _simulator.SimulateAsync(Request(""), Guid.NewGuid(), "a@x.com"));

        Assert.Empty(_audit.Entries);
        Assert.Empty(_retrieval.Requests);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class RecordingRetrieval : IKnowledgeRetrievalService
    {
        public List<KnowledgeRetrievalRequest> Requests { get; } = new();
        public Guid? TenantSeenByRetrieval { get; private set; }
        public Func<Guid?> ReadTenant { get; set; } = () => null;

        public Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            TenantSeenByRetrieval = ReadTenant();

            return Task.FromResult(new KnowledgeRetrievalResult(
                Array.Empty<RetrievedEvidence>(),
                new RetrievalDiagnostics("", Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, false, "NoEvidence", 0, false, false,
                    RetrievalMode.FusionOnly, "v", "k", "r", 0, 0)));
        }
    }

    private sealed class RecordingAudit : IPlatformAuditService
    {
        public List<(Guid Actor, string Action, Guid? TargetTenantId, string? Details)> Entries { get; } = new();

        public Task LogAsync(Guid actorUserId, string actorEmail, string action, Guid? targetTenantId = null,
            Guid? targetUserId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((actorUserId, action, targetTenantId, details));
            return Task.CompletedTask;
        }

        public Task<PagedResult<PlatformAuditLogEntryDto>> GetPagedAsync(PlatformAuditLogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SwitchableTenant : ITenantContext
    {
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
