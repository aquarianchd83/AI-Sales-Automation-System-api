namespace WhatsAppSalesAutomation.Infrastructure.Identity;

/// <summary>
/// Decides whether the configured JWT signing secret is fit to sign real tokens. The repository ships a literal placeholder
/// in appsettings.json, and the repository is readable by anyone it is shared with: an app that starts with that value
/// accepts a token forged by any of them, for any tenant. Program.cs refuses to start outside Development when this finds
/// a problem, and only warns in Development, where the placeholder is the convenient default.
/// </summary>
public static class JwtSecretGuard
{
    /// <summary>HS256 wants at least 128 bits; 32 characters of random text comfortably clears that.</summary>
    public const int MinimumLength = 32;

    /// <summary>What is wrong with <paramref name="secret"/>, in a sentence an operator can act on, or null when it is acceptable.</summary>
    public static string? FindProblem(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return "Jwt:Secret is not set.";

        if (secret.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
            return "Jwt:Secret is still the placeholder shipped in appsettings.json, so anyone who can read the repository can forge a valid token for any tenant.";

        if (secret.Trim().Length < MinimumLength)
            return $"Jwt:Secret is only {secret.Trim().Length} characters; at least {MinimumLength} are required.";

        // A secret made of one repeated character passes the length check and is still trivially guessable.
        if (secret.Distinct().Count() < 8)
            return "Jwt:Secret has too little variety to be random; generate a long random value instead.";

        return null;
    }
}
