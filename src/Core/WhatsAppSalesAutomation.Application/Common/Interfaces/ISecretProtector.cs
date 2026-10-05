namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Encrypts a secret for storage (a tenant's Meta access token) with the same key ring that protects
/// the other stored secrets. Also used to sign short-lived values such as the Facebook login "state".</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>Null when the value was not produced by <see cref="Protect"/> or cannot be decrypted any more.</summary>
    string? TryUnprotect(string protectedValue);
}
