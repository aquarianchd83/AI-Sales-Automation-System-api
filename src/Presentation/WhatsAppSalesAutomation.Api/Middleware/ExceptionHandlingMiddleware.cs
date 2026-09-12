using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;

namespace WhatsAppSalesAutomation.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(context, exception);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, title, errors) = exception switch
        {
            NotFoundException notFound => (StatusCodes.Status404NotFound, notFound.Message, (IReadOnlyList<string>?)null),
            ConflictException conflict => (StatusCodes.Status409Conflict, conflict.Message, null),
            AuthenticationFailedException auth => (StatusCodes.Status401Unauthorized, auth.Message, null),
            PlanLimitExceededException planLimit => (StatusCodes.Status402PaymentRequired, planLimit.Message, null),
            // Almost always Stripe:SecretKey not configured yet (see Infrastructure's
            // DependencyInjection.AddBilling doc comment on the "sk_not_configured" placeholder) or a
            // real Stripe outage - neither is the tenant's fault, and Stripe's own exception message
            // isn't something a tenant clicking "Choose plan" should have to interpret. The real
            // message still reaches the log below (unredacted) for whoever's diagnosing it.
            Stripe.StripeException => (StatusCodes.Status503ServiceUnavailable, "Billing isn't available right now — contact your platform administrator.", null),
            FluentValidation.ValidationException validation =>
                (StatusCodes.Status400BadRequest, "One or more validation errors occurred.", (IReadOnlyList<string>?)validation.Errors.Select(e => e.ErrorMessage).ToList()),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", null)
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
        else
            _logger.LogWarning(exception, "Handled exception ({StatusCode}) processing {Method} {Path}", statusCode, context.Request.Method, context.Request.Path);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Instance = context.Request.Path
        };

        if (errors is { Count: > 0 })
            problemDetails.Extensions["errors"] = errors;

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(problemDetails);
    }
}
