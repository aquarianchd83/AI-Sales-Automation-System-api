using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Signing in: a guesser is stopped per account, the failure text gives nothing away, and changing a password signs
/// everyone else out.</summary>
public sealed class LoginSecurityTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private readonly RecordingNotifier _notices = new();
    private ITenantNotifier Notifier => _notices;

    public void Dispose() => _h.Dispose();

    private AuthService Auth()
    {
        var jwt = Fake.Of<IJwtTokenService>((m, a) => m.Name switch
        {
            nameof(IJwtTokenService.GenerateAccessToken) => new JwtTokenResult("access", DateTime.UtcNow.AddMinutes(15)),
            nameof(IJwtTokenService.GenerateRefreshToken) => new JwtTokenResult("refresh-" + Guid.NewGuid(), DateTime.UtcNow.AddDays(7)),
            nameof(IJwtTokenService.HashToken) => "hash:" + (string)a![0]!,
            _ => throw new NotImplementedException(m.Name)
        });
        var recovery = new AccountRecoveryService(
            _h.Users, _h.Db, new NoEmail(), new NoSms(), _h.Clock, new MemoryCache(new MemoryCacheOptions()),
            new FixedOptions<AppLinkOptions>(new AppLinkOptions()),
            new ForgotPasswordRequestValidator(), new ResetPasswordRequestValidator(), new VerifyEmailRequestValidator(), new VerifyPhoneRequestValidator(),
            NullLogger<AccountRecoveryService>.Instance);

        return new AuthService(
            _h.Users, _h.Db, jwt, _h.Clock, null!, null!, null!, null!, null!, null!,
            new LoginRequestValidator(), null!, new ChangePasswordRequestValidator(), null!, null!, recovery, Notifier);
    }

    private Task<TokenPairDto> Login(string password, string email = "asha@example.com") =>
        Auth().LoginAsync(new LoginRequest(email, password), "127.0.0.1");

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_and_even_the_right_one_is_then_refused()
    {
        await _h.AddUserAsync();

        for (var i = 0; i < 4; i++)
        {
            var wrong = await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));
            Assert.Equal("Invalid credentials.", wrong.Message);
        }

        var fifth = await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));
        var correct = await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login(IdentityHarness.Password));

        Assert.Equal(AuthService.LockedOutMessage, fifth.Message);
        Assert.Equal(AuthService.LockedOutMessage, correct.Message);
    }

    [Fact]
    public async Task The_workspace_admins_are_told_once_when_someone_is_locked_out()
    {
        var tenantId = Guid.NewGuid();
        await _h.AddUserAsync(tenantId: tenantId);

        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));
        Assert.Empty(_notices.Sent);

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal(TenantNotificationKind.AccountLocked, notice.Kind);
        Assert.Equal(tenantId, notice.TenantId);
        Assert.Contains("asha@example.com", notice.Body);
    }

    [Fact]
    public async Task A_user_who_belongs_to_no_workspace_has_nobody_to_tell()
    {
        await _h.AddUserAsync();

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));

        Assert.Empty(_notices.Sent);
    }

    [Fact]
    public async Task A_good_sign_in_clears_the_count_of_failures()
    {
        var user = await _h.AddUserAsync();
        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));

        await Login(IdentityHarness.Password);

        user = (await _h.Users.FindByIdAsync(user.Id.ToString()))!;
        Assert.Equal(0, user.AccessFailedCount);
        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Wrong-Passw0rd!"));
        Assert.False(await _h.Users.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task An_unknown_email_fails_exactly_like_a_wrong_password()
    {
        await _h.AddUserAsync();

        var unknown = await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Whatever-1!", "nobody@example.com"));
        var wrong = await Assert.ThrowsAsync<AuthenticationFailedException>(() => Login("Whatever-1!"));

        Assert.Equal(wrong.Message, unknown.Message);
    }

    [Fact]
    public async Task Changing_the_password_signs_every_other_session_out()
    {
        var mine = await _h.AddUserAsync();
        var other = await _h.AddUserAsync("other@example.com");
        _h.Db.RefreshTokens.Add(new RefreshToken { UserId = mine.Id, TokenHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(7) });
        _h.Db.RefreshTokens.Add(new RefreshToken { UserId = mine.Id, TokenHash = "b", ExpiresAt = DateTime.UtcNow.AddDays(7) });
        _h.Db.RefreshTokens.Add(new RefreshToken { UserId = other.Id, TokenHash = "c", ExpiresAt = DateTime.UtcNow.AddDays(7) });
        await _h.Db.SaveChangesAsync();

        await Auth().ChangePasswordAsync(mine.Id, new ChangePasswordRequest(IdentityHarness.Password, "New-Passw0rd!"));

        var tokens = await _h.Db.RefreshTokens.ToListAsync();
        Assert.All(tokens.Where(t => t.UserId == mine.Id), t => Assert.NotNull(t.RevokedAt));
        Assert.Null(tokens.Single(t => t.UserId == other.Id).RevokedAt);
    }

    private sealed class NoEmail : IEmailSender
    {
        public Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeliveryResult(true));
    }

    private sealed class NoSms : ISmsOtpSender
    {
        public Task<DeliveryResult> SendOtpAsync(string toPhoneE164, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeliveryResult(true));
    }
}
