using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;

namespace WhatsAppSalesAutomation.Api.Extensions;

public static class SwaggerServiceExtensions
{
    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "WhatsApp Sales Automation API",
                Version = "v1",
                Description = "WhatsApp Marketing + AI Sales Automation + CRM + Human Handoff Platform API"
            });

            // Two DTOs may share a simple name in different namespaces (e.g. AnswerRevisionDto in ProductKnowledge and in
            // ContentStudio.Questionnaire). Swashbuckle keys schemas by simple name and throws on /swagger/v1/swagger.json when
            // two types collide, so those - and only those - get their namespace folded into the schema id.
            options.CustomSchemaIds(SchemaId);

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Enter a valid JWT access token (no 'Bearer ' prefix needed).",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            };

            options.AddSecurityDefinition("Bearer", securityScheme);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                { securityScheme, Array.Empty<string>() }
            });
        });

        return services;
    }

    private static readonly Lazy<HashSet<string>> CollidingNames = new(() =>
        typeof(SwaggerServiceExtensions).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name?.StartsWith("WhatsAppSalesAutomation", StringComparison.Ordinal) == true)
            .Select(Assembly.Load)
            .Append(typeof(SwaggerServiceExtensions).Assembly)
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => !t.IsGenericTypeDefinition && !t.IsNested)
            .GroupBy(t => t.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet());

    private static string SchemaId(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            return name + string.Concat(type.GetGenericArguments().Select(SchemaId));
        }

        if (!CollidingNames.Value.Contains(type.Name) || string.IsNullOrEmpty(type.Namespace))
            return type.Name;

        return type.Namespace.Split('.')[^1] + type.Name;
    }
}
