using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Account;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Options;
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
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Settings;
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
        services.Configure<LeadDiscoveryOptions>(configuration.GetSection("LeadDiscovery"));
        services.Configure<LeadDiscoveryPricingOptions>(configuration.GetSection("LeadDiscovery:Pricing"));
        services.Configure<WhatsAppPricingOptions>(configuration.GetSection("WhatsApp:Pricing"));

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountProfileService, AccountProfileService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantSlugResolver, TenantSlugResolver>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<IMessageTemplateService, MessageTemplateService>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<ICampaignSendService, CampaignSendService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IHandoffService, HandoffService>();
        services.AddScoped<IInboundWebhookProcessor, InboundWebhookProcessor>();
        services.AddScoped<ILeadService, LeadService>();
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IConversationOrchestrator, ConversationOrchestrator>();
        services.AddScoped<ILogService, LogService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IPlanLimitsService, PlanLimitsService>();
        services.Configure<WhatsAppQuotaWeightOptions>(configuration.GetSection("WhatsApp:QuotaWeights"));
        services.Configure<TrialQuotaOptions>(configuration.GetSection("Billing:Trial"));
        services.AddScoped<IQuotaLedgerService, QuotaLedgerService>();
        services.AddScoped<IQuotaGate, QuotaGate>();
        services.Configure<RefundPolicyOptions>(configuration.GetSection("Billing:Refunds"));
        services.Configure<BillingAlertOptions>(configuration.GetSection("Billing:Alerts"));
        services.AddScoped<IRefundGateway, SimulatedRefundGateway>();
        services.AddScoped<IRefundService, RefundService>();
        services.AddScoped<ITenantNotifier, TenantNotifier>();
        services.AddScoped<IQuotaAlertService, QuotaAlertService>();
        services.AddScoped<ITenantBillingNoticeService, TenantBillingNoticeService>();
        services.AddScoped<ISubscriptionRenewalService, SubscriptionRenewalService>();
        services.AddScoped<IWhatsAppSpendService, WhatsAppSpendService>();
        services.AddScoped<ICurrentUserPricingService, CurrentUserPricingService>();
        services.AddScoped<ITenantChargesService, TenantChargesService>();
        services.AddScoped<ILeadDiscoveryService, LeadDiscoveryService>();
        services.AddScoped<ILeadDiscoveryRunService, LeadDiscoveryRunService>();

        // Platform Admin Console (PlatformSuperAdmin-only cross-tenant screens).
        services.AddScoped<IPlatformAuditService, PlatformAuditService>();
        services.AddScoped<IPlatformTenantService, PlatformTenantService>();
        services.AddScoped<IPlatformBillingService, PlatformBillingService>();
        services.AddScoped<IPlatformUsageService, PlatformUsageService>();
        services.AddScoped<IPlatformPaymentService, PlatformPaymentService>();
        services.AddScoped<IPlatformWhatsAppConnectionService, PlatformWhatsAppConnectionService>();
        services.AddScoped<IPlatformUserSearchService, PlatformUserSearchService>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<IPlatformDashboardService, PlatformDashboardService>();
        services.AddScoped<IPlatformJobService, PlatformJobService>();
        services.AddScoped<ITenantJobProvisioner, TenantJobProvisioner>();

        return services;
    }
}
