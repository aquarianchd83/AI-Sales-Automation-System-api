namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>A state or union territory a GST-registered business can be in.</summary>
public record IndianState(string Code, string Name);

/// <summary>The states and union territories of India, by their standard two-letter codes. Used to decide whether GST is
/// CGST + SGST (buyer in the platform's own state) or IGST.</summary>
public static class IndianStates
{
    public static readonly IReadOnlyList<IndianState> All = new List<IndianState>
    {
        new("AN", "Andaman and Nicobar Islands"), new("AP", "Andhra Pradesh"), new("AR", "Arunachal Pradesh"),
        new("AS", "Assam"), new("BR", "Bihar"), new("CH", "Chandigarh"), new("CG", "Chhattisgarh"),
        new("DH", "Dadra and Nagar Haveli and Daman and Diu"), new("DL", "Delhi"), new("GA", "Goa"),
        new("GJ", "Gujarat"), new("HR", "Haryana"), new("HP", "Himachal Pradesh"), new("JK", "Jammu and Kashmir"),
        new("JH", "Jharkhand"), new("KA", "Karnataka"), new("KL", "Kerala"), new("LA", "Ladakh"),
        new("LD", "Lakshadweep"), new("MP", "Madhya Pradesh"), new("MH", "Maharashtra"), new("MN", "Manipur"),
        new("ML", "Meghalaya"), new("MZ", "Mizoram"), new("NL", "Nagaland"), new("OD", "Odisha"),
        new("PY", "Puducherry"), new("PB", "Punjab"), new("RJ", "Rajasthan"), new("SK", "Sikkim"),
        new("TN", "Tamil Nadu"), new("TS", "Telangana"), new("TR", "Tripura"), new("UP", "Uttar Pradesh"),
        new("UK", "Uttarakhand"), new("WB", "West Bengal")
    };

    private static readonly HashSet<string> Codes = All.Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidCode(string? code) => code is not null && Codes.Contains(code.Trim());

    /// <summary>States exist only for countries whose tax splits by state.</summary>
    public static bool AppliesTo(string? countryCode) => string.Equals(countryCode?.Trim(), "IN", StringComparison.OrdinalIgnoreCase);
}
