namespace WhatsAppSalesAutomation.Application.MetaOnboarding;

/// <summary>What Meta said, reduced to the fields the mapping needs. Built from Meta's error envelope; never holds a token.</summary>
public record MetaErrorInfo(int? HttpStatus, int? Code, int? Subcode, string? Message, string? TraceId, bool Unreachable = false);

/// <summary>
/// Turns a failure into the message the tenant sees. The choice is made from the real error - Meta's code, subcode and
/// wording, plus the step it happened in - so a temporary outage never reads as "verify your business", and a
/// permission problem never reads as "try again later". Anything not recognised gets an honest generic message with a
/// reference to quote, not a guess.
///
/// Codes follow Meta's documented Graph API / WhatsApp Cloud API error codes
/// (https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes). Where Meta documents no stable code
/// (business verification, number already registered) the wording is matched instead, narrowly.
/// </summary>
public static class MetaIssueCatalog
{
    public static MetaIssueDto FromMetaError(string step, MetaErrorInfo error)
    {
        var reference = error.TraceId;
        var text = (error.Message ?? string.Empty).ToLowerInvariant();

        if (error.Unreachable || error.HttpStatus is >= 500)
            return Temporary(step, reference);

        switch (error.Code)
        {
            case 1 or 2 or 4 or 17 or 32 or 341 or 613 or 80007 or 130429 or 131056:
                return Temporary(step, reference);

            case 190: // access token expired / revoked / invalidated
            case 102: // session expired
                return Issue("token_expired", step, "Your Meta connection has expired",
                    "Meta no longer accepts the saved access for this account. Reconnect your account to renew it - your other settings are kept.",
                    MetaIssueAction.Reconnect, null, false, reference);

            case 10 or 200 or 299 or 3:
                return Issue("permission_missing", step, "Meta permissions are missing",
                    "The connected account has not given this app the permissions it needs. Reconnect and approve every permission Meta asks for.",
                    MetaIssueAction.Reconnect, null, false, reference);

            case 368 or 131031: // temporarily blocked / account locked
                return Issue("account_restricted", step, "Meta has restricted this account",
                    "Meta has restricted this account from performing the requested operation. Review the account status in your Meta Business account and resolve the restriction before retrying.",
                    MetaIssueAction.Verify, MetaIssueAction.Retry, false, reference);

            case 131042: // business eligibility - payment issue
                return Issue("meta_billing", step, "Meta billing is not ready",
                    "WhatsApp messaging cannot be activated because the required billing arrangement has not been confirmed. Please contact platform support.",
                    MetaIssueAction.ContactSupport, null, false, reference);

            case 133010: // phone number not registered
                return Issue("number_not_registered", step, "This number is not registered yet",
                    "Your number is not registered for WhatsApp messaging yet. Verify the number with Meta, then retry.",
                    MetaIssueAction.Verify, MetaIssueAction.Retry, true, reference);

            case 133000 or 133004 or 133005 or 133006 or 133008 or 133009 or 133015 or 133016:
                return Issue("number_verification", step, "The number could not be verified",
                    "Your mobile number has not been verified by Meta. Complete the verification step to continue connecting WhatsApp.",
                    MetaIssueAction.Verify, MetaIssueAction.Retry, true, reference);
        }

        // Meta documents no stable code for these; the wording is what identifies them.
        if (text.Contains("business verification") || text.Contains("verify your business") || text.Contains("business is not verified"))
            return Issue("business_verification", step, "Business verification is needed",
                "Meta requires additional business verification before you can continue. Complete the verification steps in your Meta Business account, then retry.",
                MetaIssueAction.Verify, MetaIssueAction.Retry, false, reference);

        if (text.Contains("already registered") || text.Contains("already in use") || text.Contains("already connected")
            || (text.Contains("phone number") && text.Contains("already") && text.Contains("account")))
            return Issue("number_in_use", step, "This number is already connected elsewhere",
                "This number is already registered to another WhatsApp account and cannot be connected as it is. Disconnect it from the other account in Meta, or choose a different number.",
                MetaIssueAction.Reconnect, MetaIssueAction.ContactSupport, false, reference);

        if (text.Contains("permission") || text.Contains("unsupported get request") || text.Contains("unsupported post request")
            || error.HttpStatus is 401 or 403)
            return Issue("permission_missing", step, "Meta permissions are missing",
                "The connected account has not given this app the permissions it needs. Reconnect and approve every permission Meta asks for.",
                MetaIssueAction.Reconnect, null, false, reference);

        if (error.HttpStatus == 429)
            return Temporary(step, reference);

        return Issue("meta_unknown", step, "Meta could not complete this step",
            "Meta did not accept this step and gave no reason we can act on. Your progress is saved - retry, and if it keeps happening contact support with the reference below.",
            MetaIssueAction.Retry, MetaIssueAction.ContactSupport, true, reference);
    }

    /// <summary>The tenant closed Meta's popup, or Meta reported the sign-in failed, before a code was issued.</summary>
    public static MetaIssueDto FromClientEvent(string? clientEvent, string? clientStep, string? reference)
    {
        if (string.Equals(clientEvent, "CANCEL", StringComparison.OrdinalIgnoreCase))
        {
            return Issue("authorization_cancelled", MetaSignupSteps.Authorization, "Meta sign-in was not completed",
                "Your WhatsApp account could not be connected because Meta authorization was not completed. Please reconnect your account and grant the required permissions.",
                MetaIssueAction.Reconnect, null, true, reference);
        }

        return Issue("authorization_failed", MetaSignupSteps.Authorization, "Meta could not complete the sign-in",
            "Meta could not finish connecting your account. Please try again; if it keeps happening, contact support.",
            MetaIssueAction.Retry, MetaIssueAction.ContactSupport, true, reference);
    }

    /// <summary>No authorization code came back although the popup did not report a cancel - the tenant most likely declined access.</summary>
    public static MetaIssueDto AuthorizationDenied(string? reference) =>
        Issue("authorization_denied", MetaSignupSteps.Authorization, "Access was not granted",
            "Your WhatsApp account could not be connected because Meta authorization was not completed. Please reconnect your account and grant the required permissions.",
            MetaIssueAction.Reconnect, null, true, reference);

    public static MetaIssueDto NotConfigured() =>
        Issue("platform_not_configured", MetaSignupSteps.Authorization, "Connect with Meta is not available yet",
            "The platform has not finished setting up its Meta app, so this account cannot be connected here yet. Please contact platform support.",
            MetaIssueAction.ContactSupport, null, false, null);

    public static MetaIssueDto NoAssetsFound(string? reference) =>
        Issue("no_whatsapp_account", MetaSignupSteps.Assets, "No WhatsApp number was shared",
            "Meta did not share a WhatsApp Business account and number with us. Reconnect, and choose or create a WhatsApp Business account and number when Meta asks.",
            MetaIssueAction.Reconnect, null, false, reference);

    public static MetaIssueDto NotConnected() =>
        Issue("not_connected", MetaSignupSteps.Credentials, "WhatsApp is not connected yet",
            "There is nothing to continue yet. Connect with Meta first.",
            MetaIssueAction.Reconnect, null, false, null);

    public static MetaIssueDto TokenUnreadable(string? reference) =>
        Issue("token_unreadable", MetaSignupSteps.Credentials, "The saved Meta access could not be read",
            "The saved access for this account could not be used. Reconnect your account to renew it.",
            MetaIssueAction.Reconnect, null, false, reference);

    private static MetaIssueDto Temporary(string step, string? reference) =>
        Issue("meta_temporary", step, "Meta is temporarily unavailable",
            "Meta is temporarily unable to complete this operation. Your configuration has been preserved. Please retry later.",
            MetaIssueAction.Retry, null, true, reference);

    private static MetaIssueDto Issue(
        string code, string step, string title, string message,
        MetaIssueAction primary, MetaIssueAction? secondary, bool retryable, string? reference) =>
        new(code, step, title, message, primary, secondary, retryable, reference);
}
