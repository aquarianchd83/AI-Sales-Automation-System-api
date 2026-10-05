using Microsoft.AspNetCore.DataProtection;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.SocialAds;

/// <summary>The same protector (and key ring) the stored platform and tenant secrets use.</summary>
public class SecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        _protector = AppSettingsSecretProtection.CreateProtector(provider);
    }

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string? TryUnprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
