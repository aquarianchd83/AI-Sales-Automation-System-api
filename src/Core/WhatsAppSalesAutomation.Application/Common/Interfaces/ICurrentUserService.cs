namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Reads identity claims off the current HTTP request without leaking ASP.NET Core types into Application.</summary>
public interface ICurrentUserService
{
    Guid? UserId { get; }

    string? Email { get; }

    IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// The tenant claim off the current JWT, if any. Null both for an unauthenticated request and for
    /// an authenticated <c>PlatformSuperAdmin</c> (who has no single tenant) - see
    /// <c>ITenantContext</c>, which is what the rest of the app should actually consult; this raw claim
    /// read exists mainly as <c>ITenantContext</c>'s default source.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// Non-null only when the current request is riding an impersonation access token (see
    /// <c>IJwtTokenService.GenerateImpersonationAccessToken</c>) - the PlatformSuperAdmin who started
    /// the support session. Null for a normal login, including a normal PlatformSuperAdmin session.
    /// </summary>
    Guid? ImpersonatorUserId { get; }
}
