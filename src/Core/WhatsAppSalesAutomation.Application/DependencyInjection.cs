using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Application.Users;

namespace WhatsAppSalesAutomation.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<DependencyInjection>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ICustomerService, CustomerService>();

        return services;
    }
}
