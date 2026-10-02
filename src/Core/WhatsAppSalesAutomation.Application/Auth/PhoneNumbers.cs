namespace WhatsAppSalesAutomation.Application.Auth;

/// <summary>Phone numbers as an SMS provider needs them.</summary>
public static class PhoneNumbers
{
    /// <summary>"+91 98765-43210" becomes "+919876543210". Needs the country code (a leading + and 8-15 digits):
    /// a bare local number cannot be routed, and guessing the country would text the wrong person.</summary>
    public static bool TryNormalize(string? raw, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('+'))
            return false;

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length is < 8 or > 15 || digits[0] == '0')
            return false;

        e164 = "+" + digits;
        return true;
    }

    /// <summary>True when two stored numbers are the same number, ignoring spaces, dashes and brackets.</summary>
    public static bool Same(string? a, string? b)
    {
        static string Digits(string? s) => new((s ?? string.Empty).Where(char.IsDigit).ToArray());
        return Digits(a) == Digits(b);
    }
}
