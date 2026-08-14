using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>
/// Normalizes the phone number formats people actually type into spreadsheets into the E.164 form
/// WhatsApp requires. The customer base is India, so a number with no country code is assumed to be
/// Indian and gets +91 applied; a number that already carries an explicit country code is respected
/// as-is rather than being forced to India.
/// </summary>
public static class PhoneNumberNormalizer
{
    public const string IndiaCallingCode = "91";

    /// <summary>Spaces, dashes, dots, brackets - anything a human might type as a separator.</summary>
    private static readonly Regex Separators = new(@"[\s\-\.\(\) ]", RegexOptions.Compiled);

    /// <summary>Indian mobile numbers are 10 digits and start 6-9. Landlines are out of scope for WhatsApp.</summary>
    private static readonly Regex IndianSubscriber = new(@"^[6-9]\d{9}$", RegexOptions.Compiled);

    private static readonly Regex GenericE164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);

    /// <summary>
    /// Accepts, for an Indian number: <c>9820098200</c>, <c>09820098200</c>, <c>919820098200</c>,
    /// <c>+91 98200-98200</c>, <c>0091 9820098200</c> - all normalize to <c>+919820098200</c>.
    /// A fully qualified non-Indian E.164 number (e.g. <c>+15551234567</c>) is passed through unchanged.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var value = Separators.Replace(input.Trim(), string.Empty);

        // 00 is the international dialling prefix used across India and most of the world.
        if (value.StartsWith("00", StringComparison.Ordinal))
            value = string.Concat("+", value.AsSpan(2));

        if (value.StartsWith('+'))
        {
            var digits = value[1..];
            if (!digits.All(char.IsAsciiDigit))
                return false;

            if (digits.StartsWith(IndiaCallingCode, StringComparison.Ordinal))
                return TrySetIndian(digits[IndiaCallingCode.Length..], out normalized);

            if (!GenericE164.IsMatch(value))
                return false;

            normalized = value;
            return true;
        }

        if (!value.All(char.IsAsciiDigit))
            return false;

        // No country code: interpret against the Indian numbering plan.
        var subscriber = value switch
        {
            // 919820098200 - country code typed without the +.
            { Length: 12 } when value.StartsWith(IndiaCallingCode, StringComparison.Ordinal) => value[2..],
            // 09820098200 - the domestic trunk prefix, which is not part of the number itself.
            { Length: 11 } when value.StartsWith('0') => value[1..],
            { Length: 10 } => value,
            _ => null
        };

        return TrySetIndian(subscriber, out normalized);
    }

    private static bool TrySetIndian(string? subscriber, out string normalized)
    {
        normalized = string.Empty;

        if (subscriber is null || !IndianSubscriber.IsMatch(subscriber))
            return false;

        normalized = $"+{IndiaCallingCode}{subscriber}";
        return true;
    }
}
