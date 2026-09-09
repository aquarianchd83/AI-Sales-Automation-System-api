using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

public class WhatsAppTokenStore : IWhatsAppTokenStore
{
    /// <summary>Fixed singleton row id - see WhatsAppAccessTokenState's own doc comment.</summary>
    private const int RowId = 1;

    private readonly ApplicationDbContext _context;
    private readonly WhatsAppSettings _settings;
    private readonly IDateTimeProvider _dateTime;

    public WhatsAppTokenStore(ApplicationDbContext context, IOptionsSnapshot<WhatsAppSettings> settings, IDateTimeProvider dateTime)
    {
        _context = context;
        _settings = settings.Value;
        _dateTime = dateTime;
    }

    public async Task<WhatsAppTokenSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var state = await _context.WhatsAppAccessTokenStates.FirstOrDefaultAsync(s => s.Id == RowId, cancellationToken);

        return state is null
            ? new WhatsAppTokenSnapshot(_settings.AccessToken, null)
            : new WhatsAppTokenSnapshot(state.AccessToken, state.ExpiresAt);
    }

    public async Task SaveRefreshedTokenAsync(string accessToken, DateTime? expiresAt, CancellationToken cancellationToken = default)
    {
        var state = await _context.WhatsAppAccessTokenStates.FirstOrDefaultAsync(s => s.Id == RowId, cancellationToken);
        if (state is null)
        {
            state = new WhatsAppAccessTokenState { Id = RowId };
            _context.WhatsAppAccessTokenStates.Add(state);
        }

        state.AccessToken = accessToken;
        state.ExpiresAt = expiresAt;
        state.RefreshedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }
}
