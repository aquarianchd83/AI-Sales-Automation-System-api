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
        // typeof(...) rather than the generic overload - a static type cannot be a type argument.
        services.AddValidatorsFromAssemblyContaining(typeof(DependencyInjection));

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ICustomerService, CustomerService>();

        return services;
    }
}
