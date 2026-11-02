using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Conversations;

/// <summary>A customer agreed the business's next step (a demo, a visit, an appointment...) with the AI and gave what it needs. One row per booking, so the tenant has a
/// list to work from: the AI has no calendar, so this list is what makes the booking real. <see cref="CreatedAt"/> is when it was agreed.</summary>
public class NextStepBooking : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The conversation it was agreed in. Null for a booking made in Test chat, where there is no conversation.</summary>
    public Guid? ConversationId { get; set; }

    public Guid? CustomerId { get; set; }

    /// <summary>What it is, in a word: "Demo", "Site visit", "Appointment"... as the business's goal was when it was agreed.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What was agreed, in a sentence, as the AI reported it: what, when, in which time zone, and what to cover.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Made by the tenant trying its own AI in Test chat, not by a customer. Kept so the notice can be tested end to end; never counted as a real booking.</summary>
    public bool IsTest { get; set; }

    /// <summary>Pending until the tenant marks it Done, or Reschedule when the time has to change.</summary>
    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    /// <summary>The new time the tenant picked when it marked the booking Reschedule (UTC), so the customer can be offered it. Only set while the status is Reschedule;
    /// cleared when the booking moves on, and when the customer confirms a time of their own.</summary>
    public DateTime? NewSlotUtc { get; set; }

    /// <summary>Why the tenant moved the booking, as it typed it when marking it Reschedule. Only set while the status is Reschedule; cleared when the booking moves on.</summary>
    public string? RescheduleReason { get; set; }

    /// <summary>The email address the customer gave in the chat, so the confirmation (and anything the tenant sends later, such as a meeting link) can reach them.
    /// Null when they gave none.</summary>
    public string? CustomerEmail { get; set; }

    /// <summary>How the confirmation email went: Sent, Failed, or Skipped (no email set up on the platform). Null when there was no address to send to.</summary>
    public string? ConfirmationStatus { get; set; }

    /// <summary>Why the confirmation failed or was skipped, so the tenant knows to contact the customer themselves.</summary>
    public string? ConfirmationNote { get; set; }
}
