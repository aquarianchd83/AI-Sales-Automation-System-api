using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Account;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Application.LogViewer;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.MessageTemplates;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Application.Packages;
using WhatsAppSalesAutomation.Application.SocialAds;
using WhatsAppSalesAutomation.Application.Tags;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Application.Users;
using WhatsAppSalesAutomation.Application.Webhooks;

namespace WhatsAppSalesAutomation.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        // typeof(...) rather than the generic overload - a static type cannot be a type argument.
        services.AddValidatorsFromAssemblyContaining(typeof(DependencyInjection));

        services.Configure<CampaignOptions>(configuration.GetSection("Campaigns"));
        services.Configure<MessagingOptions>(configuration.GetSection("Messaging"));
        services.Configure<MediaOptions>(configuration.GetSection("Media"));
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.Configure<AppLinkOptions>(configuration.GetSection("App"));
        services.Configure<RetentionOptions>(configuration.GetSection("Retention"));
        services.Configure<StudioOptions>(configuration.GetSection("ContentStudio"));
        services.AddScoped<IStudioMeteringService, StudioMeteringService>();
        services.AddScoped<IStudioSettingsService, StudioSettingsService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire.IQuestionnaireService, WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire.QuestionnaireService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire.IBusinessFactService, WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire.BusinessFactService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ContentStudio.Knowledge.KnowledgeService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ContentStudio.Knowledge.IKnowledgeService>(sp => sp.GetRequiredService<WhatsAppSalesAutomation.Application.ContentStudio.Knowledge.KnowledgeService>());
        services.AddScoped<WhatsAppSalesAutomation.Application.ContentStudio.Knowledge.IKnowledgeRetrievalService>(sp => sp.GetRequiredService<WhatsAppSalesAutomation.Application.ContentStudio.Knowledge.KnowledgeService>());
        services.AddScoped<WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire.IQuestionnaireTemplateService, WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire.QuestionnaireTemplateService>();
        services.Configure<LeadDiscoveryOptions>(configuration.GetSection("LeadDiscovery"));
        services.Configure<LeadDiscoveryPricingOptions>(configuration.GetSection("LeadDiscovery:Pricing"));
        services.Configure<WhatsAppPricingOptions>(configuration.GetSection("WhatsApp:Pricing"));
        services.Configure<AiPricingOptions>(configuration.GetSection("Ai:Pricing"));
        services.Configure<TaxOptions>(configuration.GetSection("Tax"));
        services.Configure<CostAssumptionsOptions>(configuration.GetSection("Costing"));
        services.Configure<FxOptions>(configuration.GetSection("Fx"));
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Consent.IConsentInvitationService, WhatsAppSalesAutomation.Application.Consent.ConsentInvitationService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Onboarding.IOnboardingService, WhatsAppSalesAutomation.Application.Onboarding.OnboardingService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Tenancy.IKeywordSuggestionService, WhatsAppSalesAutomation.Application.Tenancy.KeywordSuggestionService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.LeadDiscovery.ILeadDiscoveryKeywordSuggestionService, WhatsAppSalesAutomation.Application.LeadDiscovery.LeadDiscoveryKeywordSuggestionService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Tenancy.IDescriptionRefinementService, WhatsAppSalesAutomation.Application.Tenancy.DescriptionRefinementService>();
        services.AddScoped<IAiSpendEstimator, AiSpendEstimator>();

        // Phase 6 ingestion. The chunker and counter are stateless; the batcher and services are
        // scoped because the batcher carries a per-run circuit-breaker counter that must not leak
        // between unrelated jobs.
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.ITokenCounter,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.HeuristicTokenCounter>();
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.StructureAwareChunker>();
        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.EmbeddingBatcher>(_ =>
            new WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.EmbeddingBatcher());
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.IDocumentFormatExtractor,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.TextDocumentExtractor>();
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.IDocumentFormatExtractor,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.HtmlDocumentExtractor>();
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.IDocumentFormatExtractor,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.DocxDocumentExtractor>();
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.DocumentTextExtractor>();
        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.IKnowledgeIngestionService,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.KnowledgeIngestionService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.KnowledgeMetadataSyncService>();

        // Phase 6 retrieval. Options bound here; the stores, cache and reranker are Infrastructure's.
        services.Configure<SupportRagOptions>(configuration.GetSection("SupportRag"));
        services.Configure<RerankerOptions>(configuration.GetSection("Reranker:Cohere"));
        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.IKnowledgeRetrievalService,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.KnowledgeRetrievalService>();

        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.IKnowledgeRetrievalSimulator,
            WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.KnowledgeRetrievalSimulator>();

        services.AddScoped<WhatsAppSalesAutomation.Application.Audit.IAuditLogService, WhatsAppSalesAutomation.Application.Audit.AuditLogService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Reports.IReportService, WhatsAppSalesAutomation.Application.Reports.ReportService>();

        services.AddScoped<IKnowledgeUploadService, KnowledgeUploadService>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountRecoveryService, AccountRecoveryService>();
        services.AddScoped<IAuditTrailWriter, AuditTrailWriter>();
        services.AddScoped<IAccountProfileService, AccountProfileService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantSlugResolver, TenantSlugResolver>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<IPackageService, PackageService>();
        services.AddScoped<ISocialAdsService, SocialAdsService>();
        services.AddScoped<ISocialAdSyncService, SocialAdSyncService>();
        services.AddScoped<IMarketingComparisonService, MarketingComparisonService>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<IMessageTemplateService, MessageTemplateService>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<ICampaignSendService, CampaignSendService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IHandoffService, HandoffService>();
        services.AddScoped<IHandoffSummaryBuilder, HandoffSummaryBuilder>();
        services.AddScoped<IInboundWebhookProcessor, InboundWebhookProcessor>();
        services.AddScoped<ILeadScoringService, LeadScoringService>();
        services.AddScoped<IQualificationPlanner, QualificationPlanner>();
        services.AddScoped<ICrmContextBuilder, CrmContextBuilder>();
        services.AddScoped<ILeadService, LeadService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Leads.FollowUps.ILeadFollowUpService, WhatsAppSalesAutomation.Application.Leads.FollowUps.LeadFollowUpService>();
        services.AddScoped<IQualificationAdminService, QualificationAdminService>();
        services.AddScoped<ILeadScoringAdminService, LeadScoringAdminService>();
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IAiReplyValidator, AiReplyValidator>();
        services.AddScoped<IConversationOrchestrator, ConversationOrchestrator>();
        services.AddScoped<IAgentPerformanceService, AgentPerformanceService>();
        services.AddScoped<ILogService, LogService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IPlanLimitsService, PlanLimitsService>();
        services.Configure<TrialQuotaOptions>(configuration.GetSection("Billing:Trial"));
        // The tenant ledger itself, and in front of it the router that lets the platform pay for a scenario. Everything else asks for
        // IQuotaLedgerService and gets the routing one.
        services.AddScoped<QuotaLedgerService>();
        services.AddScoped<ICreditRouter, CreditRouter>();
        services.AddScoped<IQuotaLedgerService, CreditRoutingLedger>();
        services.AddScoped<ICreditPayerService, CreditPayerService>();
        services.AddScoped<IQuotaGate, QuotaGate>();
        services.AddScoped<IAiUsageService, AiUsageService>();
        services.Configure<RefundPolicyOptions>(configuration.GetSection("Billing:Refunds"));
        services.Configure<BillingAlertOptions>(configuration.GetSection("Billing:Alerts"));
        services.AddScoped<IRefundGateway, SimulatedRefundGateway>();
        services.AddScoped<IRefundService, RefundService>();
        services.AddScoped<ITenantNotifier, TenantNotifier>();
        services.AddScoped<IQuotaAlertService, QuotaAlertService>();
        services.AddScoped<ITenantBillingNoticeService, TenantBillingNoticeService>();
        services.AddScoped<ISubscriptionRenewalService, SubscriptionRenewalService>();
        services.AddScoped<IPlanExpiryNoticeService, PlanExpiryNoticeService>();
        services.AddScoped<ICountryAvailability, CountryAvailability>();
        services.AddScoped<IWhatsAppSpendService, WhatsAppSpendService>();
        services.AddScoped<ICurrentUserPricingService, CurrentUserPricingService>();
        services.AddScoped<ITenantChargesService, TenantChargesService>();
        services.AddScoped<ILeadDiscoveryService, LeadDiscoveryService>();
        services.AddScoped<ILeadDiscoveryRunService, LeadDiscoveryRunService>();
        services.AddScoped<ILeadDiscoveryHistoryService, LeadDiscoveryHistoryService>();
        services.AddSingleton<IApplicationInstance, ApplicationInstance>();

        // Platform Admin Console (PlatformSuperAdmin-only cross-tenant screens).
        services.AddScoped<IPlatformAuditService, PlatformAuditService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Compliance.ITenantQualityService, WhatsAppSalesAutomation.Application.Compliance.TenantQualityService>();
        services.AddScoped<IPlatformTenantService, PlatformTenantService>();
        services.AddScoped<IPlatformBillingService, PlatformBillingService>();
        services.AddScoped<IPlatformConfigurationService, PlatformConfigurationService>();
        services.AddScoped<IPlatformAwsSettingsService, PlatformAwsSettingsService>();
        services.AddScoped<IPlatformDeliverySettingsService, PlatformDeliverySettingsService>();
        services.AddScoped<IPlatformIntegrationSettingsService, PlatformIntegrationSettingsService>();
        services.AddScoped<IPlatformAiFeatureSettingsService, PlatformAiFeatureSettingsService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Ai.Tasks.IAiTaskRunner, WhatsAppSalesAutomation.Application.Ai.Tasks.AiTaskRunner>();

        // Product knowledge, phase 2: the tenant's sources (documents and website) read into page-referenced passages.
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeSourceService, WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeSourceService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeSourceProcessor, WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeSourceProcessor>();
        // The SOURCE index (working layer): embeddings of source passages, used only for drafting answers; chat never reads it.
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.ISourcePassageIndex, WhatsAppSalesAutomation.Application.ProductKnowledge.SourcePassageIndex>();

        // Phase 3: drafting answers from the sources, with verified quotations and an application-computed confidence.
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeDraftingService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeDraftingService>(sp => sp.GetRequiredService<WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeDraftingService>());
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeDraftRunner>(sp => sp.GetRequiredService<WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeDraftingService>());

        // Phase 4: the tenant's decisions about the drafts, each recorded in the audit trail.
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeVerificationService, WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeVerificationService>();

        // Phase 5: publishing the approved answers as an immutable version.
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IProductTestChat, WhatsAppSalesAutomation.Application.ProductKnowledge.ProductTestChat>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeServingIndexer, WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeServingIndexer>();
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgePublishingService, WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgePublishingService>();

        // Phase 6: plain-text Draft and Final knowledge files.
        services.AddScoped<WhatsAppSalesAutomation.Application.ProductKnowledge.IKnowledgeFileService, WhatsAppSalesAutomation.Application.ProductKnowledge.KnowledgeFileService>();
        services.AddScoped<IPlatformAiProvidersService, PlatformAiProvidersService>();
        services.AddScoped<IPlatformWhatsAppSettingsService, PlatformWhatsAppSettingsService>();
        services.AddScoped<IPlatformMessageTemplateService, PlatformMessageTemplateService>();
        services.AddScoped<IPlatformNoticeTemplates, PlatformNoticeTemplates>();
        services.AddScoped<IPlatformMediaService, PlatformMediaService>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Razorpay.IRazorpayService, WhatsAppSalesAutomation.Application.Razorpay.RazorpayService>();
        services.AddScoped<IPlanCostReportService, PlanCostReportService>();
        services.AddScoped<IPlatformUsageService, PlatformUsageService>();
        services.AddScoped<IPlatformPaymentService, PlatformPaymentService>();
        services.AddScoped<IPlatformWhatsAppConnectionService, PlatformWhatsAppConnectionService>();
        services.AddScoped<IPlatformUserSearchService, PlatformUserSearchService>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<IFlowchartService, FlowchartService>();
        services.AddScoped<IFaqService, FaqService>();
        services.AddScoped<IPlatformDashboardService, PlatformDashboardService>();
        services.AddScoped<IPlatformJobService, PlatformJobService>();
        services.AddScoped<IPlatformNotifier, PlatformNotifier>();
        services.AddScoped<IPlatformNotificationService, PlatformNotificationService>();
        services.AddScoped<IPlatformTicketService, PlatformTicketService>();
        services.AddScoped<IPlatformSetupAlerts, PlatformSetupAlerts>();
        services.AddScoped<ITenantJobProvisioner, TenantJobProvisioner>();

        return services;
    }
}
