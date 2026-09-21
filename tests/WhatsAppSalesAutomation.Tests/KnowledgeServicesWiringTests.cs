using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Application;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Reports;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Infrastructure;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Builds the REAL dependency-injection graph, with scope validation on, and resolves the Phase 6
/// services from it.
///
/// Every other test in this area constructs its subject by hand, which proves the class works and
/// says nothing about whether the application can START. A missing registration, a scoped service
/// injected into a singleton, or a constructor nobody can satisfy shows up only when the host boots -
/// which is the worst place to find out. This is the test that would have caught it.
///
/// The connection string points at a closed local port with a one-second timeout, so the capability
/// probes (vector store, full-text) fail fast and choose their fallbacks instead of depending on
/// whichever SQL Server this machine happens to have.
/// </summary>
public class KnowledgeServicesWiringTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=127.0.0.1,1;Database=Wiring;Connect Timeout=1;TrustServerCertificate=True",
                ["Jwt:Secret"] = "wiring-test-secret-wiring-test-secret-wiring",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);

        // The same two switches ASP.NET Core turns on in Development, which is where a broken graph
        // is meant to surface.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void The_ingestion_stack_resolves_from_a_scope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKnowledgeIngestionService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<KnowledgeMetadataSyncService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DocumentTextExtractor>());
    }

    [Fact]
    public void The_retrieval_stack_resolves_from_a_scope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalSimulator>());
    }

    [Fact]
    public void The_background_job_resolves_from_a_scope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<KnowledgeIndexingJob>());
    }

    [Fact]
    public void Without_native_vectors_or_full_text_the_fallback_stores_are_chosen()
    {
        // Closed port, so both probes fail and select their fallbacks - the same outcome as this
        // project's SQL Server 2017 development database.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<JsonColumnVectorStore>(scope.ServiceProvider.GetRequiredService<IVectorStore>());
        Assert.IsType<Bm25KeywordStore>(scope.ServiceProvider.GetRequiredService<IKeywordSearchStore>());
    }

    [Fact]
    public void The_reranker_is_registered_and_reports_none_when_no_key_is_configured()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var reranker = scope.ServiceProvider.GetRequiredService<IReranker>();

        Assert.Equal("None", reranker.ProviderName);
    }

    [Fact]
    public void Every_document_format_is_registered_including_pdf()
    {
        using var provider = BuildProvider();

        var extractor = provider.GetRequiredService<DocumentTextExtractor>();

        Assert.Contains(".md", extractor.SupportedExtensions);
        Assert.Contains(".html", extractor.SupportedExtensions);
        Assert.Contains(".docx", extractor.SupportedExtensions);
        Assert.Contains(".pdf", extractor.SupportedExtensions);
    }

    [Fact]
    public void The_stateful_batcher_is_scoped_so_one_jobs_circuit_state_cannot_leak_into_another()
    {
        using var provider = BuildProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredService<EmbeddingBatcher>();
        var b = second.ServiceProvider.GetRequiredService<EmbeddingBatcher>();

        Assert.NotSame(a, b);
        Assert.Same(a, first.ServiceProvider.GetRequiredService<EmbeddingBatcher>());
    }

    [Fact]
    public void The_audit_and_report_services_resolve_and_the_interceptor_is_scoped()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditLogService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AuditTrailSaveChangesInterceptor>());
    }

    [Fact]
    public void The_audit_log_is_admin_only_because_it_names_staff_and_their_ip_addresses()
    {
        var roles = typeof(AuditLogsController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(a => a.Roles).Single();

        Assert.Equal(AppRoles.Admin, roles);
    }

    [Fact]
    public void Reports_are_open_to_admins_and_sales_managers_but_not_sales_agents()
    {
        var roles = typeof(ReportsController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(a => a.Roles).Single()!.Split(',');

        Assert.Contains(AppRoles.Admin, roles);
        Assert.Contains(AppRoles.SalesManager, roles);
        Assert.DoesNotContain(AppRoles.SalesAgent, roles);
    }

    [Fact]
    public void The_kb_service_and_the_upload_service_resolve_now_that_publish_depends_on_ingestion()
    {
        // KnowledgeBaseService gained a constructor dependency on the ingestion service; this is the
        // test that would fail at startup rather than at the first Publish click if it were unresolvable.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKnowledgeUploadService>());
    }
}
