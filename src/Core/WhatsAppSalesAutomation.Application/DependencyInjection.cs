using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.MessageTemplates;
using WhatsAppSalesAutomation.Application.Tags;
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

        services.AddScoped<IAuthService, AuthService>();
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

        return services;
    }
}
