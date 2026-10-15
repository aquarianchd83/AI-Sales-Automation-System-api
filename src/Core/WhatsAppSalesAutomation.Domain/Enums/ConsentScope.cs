namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// What a customer's opt-in actually covers. Consent to a booking or an order update is not consent to
/// promotions, so the two are recorded separately and a promotional (Marketing-category) template is
/// only sent under <see cref="Marketing"/>.
/// </summary>
public enum ConsentScope
{
    /// <summary>Service messages only: bookings, orders, reminders, replies. Never promotions.</summary>
    Transactional = 0,

    /// <summary>Promotional messages as well as service messages.</summary>
    Marketing = 1
}
