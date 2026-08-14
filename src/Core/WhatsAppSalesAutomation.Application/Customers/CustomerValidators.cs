using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;

namespace WhatsAppSalesAutomation.Application.Customers;

/// <summary>
/// Accepts anything <see cref="PhoneNumberNormalizer"/> can resolve rather than demanding E.164 up
/// front - the service normalizes before saving, so requiring the caller to pre-format would reject
/// numbers the system is perfectly able to understand.
/// </summary>
internal static class CustomerRuleBuilders
{
    private const string PhoneMessage =
        "Phone number must be a valid Indian mobile number, e.g. 9820098200, 09820098200 or +919820098200.";

    public static IRuleBuilderOptions<T, string> ValidPhoneNumber<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(phone => PhoneNumberNormalizer.TryNormalize(phone, out _))
            .WithMessage(PhoneMessage);
}

public class CreateCustomerRequestValidator : AbstractValidator<CreateCustomerRequest>
{
    public CreateCustomerRequestValidator()
    {
        RuleFor(x => x.PhoneNumberE164).ValidPhoneNumber();

        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class UpdateCustomerRequestValidator : AbstractValidator<UpdateCustomerRequest>
{
    public UpdateCustomerRequestValidator()
    {
        RuleFor(x => x.PhoneNumberE164).ValidPhoneNumber();

        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class OptInCustomerRequestValidator : AbstractValidator<OptInCustomerRequest>
{
    public OptInCustomerRequestValidator()
    {
        // Consent with no recorded provenance is not evidence, so Source is required.
        RuleFor(x => x.Source)
            .NotEmpty().WithMessage("Record how consent was captured, e.g. WebForm, WhatsApp, PaperForm.")
            .MaximumLength(100);

        RuleFor(x => x.CapturedAt)
            .LessThanOrEqualTo(_ => DateTime.UtcNow)
            .When(x => x.CapturedAt.HasValue)
            .WithMessage("Consent cannot be dated in the future.");
    }
}

public class BulkDeleteCustomersRequestValidator : AbstractValidator<BulkDeleteCustomersRequest>
{
    /// <summary>Bounds the IN clause and the size of a single accidental mass-delete.</summary>
    public const int MaxIds = 500;

    public BulkDeleteCustomersRequestValidator()
    {
        RuleFor(x => x.Ids)
            .NotEmpty().WithMessage("At least one customer id is required.")
            .Must(ids => ids is null || ids.Count <= MaxIds)
            .WithMessage($"A maximum of {MaxIds} customers can be deleted per request.");

        RuleForEach(x => x.Ids)
            .NotEqual(Guid.Empty).WithMessage("Customer ids must be non-empty GUIDs.");
    }
}
