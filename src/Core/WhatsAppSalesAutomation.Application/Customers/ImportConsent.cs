using System.Globalization;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Customers;

/// <summary>Consent resolved from an import row, ready to stamp onto a <c>Customer</c>.</summary>
internal readonly record struct ImportConsent(
    OptInStatus Status,
    DateTime? OptInTimestamp,
    DateTime? OptOutTimestamp,
    string? Source);

/// <summary>
/// Turns the free-text consent columns of an import file into a status plus its evidence.
///
/// Two deliberate rules:
/// <list type="bullet">
/// <item>No consent column at all means <c>PendingOptIn</c> - importing a list never makes anyone
/// messageable by default.</item>
/// <item>Claiming opt-in without a date is rejected rather than accepted, because the timestamp is
/// the evidence. Silently substituting "now" would manufacture a consent record that never happened.</item>
/// </list>
/// </summary>
internal static class ImportConsentResolver
{
    private static readonly HashSet<string> OptedInValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "y", "true", "1", "optedin", "opted-in", "optin", "opt-in", "subscribed", "consented"
    };

    private static readonly HashSet<string> PendingValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "n", "false", "0", "pending", "pendingoptin", "pending-opt-in", "none"
    };

    private static readonly HashSet<string> OptedOutValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "optedout", "opted-out", "optout", "opt-out", "stop", "unsubscribed", "withdrawn"
    };

    /// <summary>
    /// Day-first formats first: the customer base is India, so 03/04/2026 means 3 April, not 4 March.
    /// An ISO date is unambiguous and wins outright.
    /// </summary>
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "dd/MM/yyyy", "dd-MM-yyyy", "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm",
        "dd/MM/yy", "dd-MMM-yyyy", "d MMM yyyy", "MMM d, yyyy"
    };

    public static bool TryResolve(CustomerImportRow row, DateTime utcNow, out ImportConsent consent, out string error)
    {
        consent = new ImportConsent(OptInStatus.PendingOptIn, null, null, null);
        error = string.Empty;

        var source = string.IsNullOrWhiteSpace(row.OptInSource) ? null : row.OptInSource.Trim();

        if (string.IsNullOrWhiteSpace(row.OptInStatus))
            return true;

        var status = row.OptInStatus.Trim();

        if (PendingValues.Contains(status))
            return true;

        if (OptedOutValues.Contains(status))
        {
            // An opt-out date is useful but not load-bearing: the suppression itself is what matters,
            // and a list of numbers that must never be contacted is worth importing either way.
            if (!TryParseDate(row.OptInDate, utcNow, out var optOutAt, out error))
                return false;

            consent = new ImportConsent(OptInStatus.OptedOut, null, optOutAt, source);
            return true;
        }

        if (!OptedInValues.Contains(status))
        {
            error = $"Unrecognised opt-in status '{status}'. Use Yes, No or OptedOut.";
            return false;
        }

        if (!TryParseDate(row.OptInDate, utcNow, out var optInAt, out error))
            return false;

        if (optInAt is null)
        {
            error = "Opt-in requires a date in OptInDate - the timestamp is the evidence of consent.";
            return false;
        }

        if (source is null)
        {
            error = "Opt-in requires OptInSource, e.g. WebForm, WhatsApp, PaperForm.";
            return false;
        }

        consent = new ImportConsent(OptInStatus.OptedIn, optInAt, null, source);
        return true;
    }

    /// <summary>Returns true with a null date when the field is simply absent; false only on a bad value.</summary>
    private static bool TryParseDate(string? value, DateTime utcNow, out DateTime? parsed, out string error)
    {
        parsed = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var text = value.Trim();

        if (!DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            error = $"Could not read consent date '{text}'. Use yyyy-MM-dd or dd/MM/yyyy.";
            return false;
        }

        // Spreadsheet dates carry no time zone; treat them as UTC to match the rest of the system.
        date = DateTime.SpecifyKind(date, DateTimeKind.Utc);

        if (date > utcNow)
        {
            error = $"Consent date '{text}' is in the future.";
            return false;
        }

        parsed = date;
        return true;
    }
}
