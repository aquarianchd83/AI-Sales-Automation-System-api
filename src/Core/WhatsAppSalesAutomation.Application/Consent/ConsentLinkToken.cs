using System.Security.Cryptography;
using System.Text;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// The secret in a consent link. 256 random bits, so it cannot be guessed or enumerated, and it carries no tenant, lead or phone data.
/// Only its SHA-256 is stored: a database leak does not yield working links.
/// </summary>
public static class ConsentLinkToken
{
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>A token that cannot be ours is rejected without touching the database.</summary>
    public static bool LooksValid(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>For audit: identifies a client without keeping its address.</summary>
    public static string? HashClient(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Hash("client:" + value.Trim());

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
