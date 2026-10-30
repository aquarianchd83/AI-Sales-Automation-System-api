namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>Why a tenant cannot afford any more of something it has credits for. "No credits" and "the monthly limit it set is used up" both end
/// in zero affordable units, but they call for different action, so a screen or a run history must be able to say which.</summary>
public enum QuotaShortfallKind
{
    /// <summary>The wallet holds fewer credits than one unit costs.</summary>
    InsufficientCredits = 0,

    /// <summary>The feature's monthly cap (set by the tenant or an operator) is used up, though the wallet still has credits.</summary>
    MonthlyLimitReached = 1,

    /// <summary>The tenant's plan does not include the feature.</summary>
    NotInPlan = 2
}

/// <param name="Message">A sentence for the tenant, with the figures in it, ready to show as it is.</param>
public record QuotaShortfall(QuotaShortfallKind Kind, string Message);
