namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Reads identity claims off the current HTTP request without leaking ASP.NET Core types into Application.</summary>
public interface ICurrentUserService
{
    Guid? UserId { get; }

    string? Email { get; }

    IReadOnlyList<string> Roles { get; }
}
