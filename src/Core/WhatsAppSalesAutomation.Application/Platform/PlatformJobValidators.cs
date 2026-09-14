using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Guards the one field an operator can get wrong in a way the platform cannot recover from on
/// its own: the cron. Validated through <see cref="ITenantJobScheduler.IsValidCron"/> - the same parser
/// Hangfire itself will use - rather than a regex here, so "accepted by this API" and "schedulable by
/// Hangfire" cannot drift apart and leave a tenant with a job that is registered but never fires.</summary>
public class UpdateTenantJobScheduleRequestValidator : AbstractValidator<UpdateTenantJobScheduleRequest>
{
    public UpdateTenantJobScheduleRequestValidator(ITenantJobScheduler scheduler)
    {
        RuleFor(x => x.CronExpression)
            .NotEmpty().WithMessage("A cron expression is required.")
            .MaximumLength(100)
            .Must(cron => scheduler.IsValidCron(cron))
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage("Not a valid cron expression - use 5 fields, e.g. '*/5 * * * *' for every five minutes.");
    }
}
