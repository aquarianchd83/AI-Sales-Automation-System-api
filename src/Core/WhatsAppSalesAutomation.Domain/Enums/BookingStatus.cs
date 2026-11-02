namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where a demo, visit or appointment the AI booked has got to. Set by the tenant's own people on the Demos scheduled page.</summary>
public enum BookingStatus
{
    /// <summary>Agreed with the customer and waiting to happen. Every booking starts here.</summary>
    Pending = 0,

    /// <summary>It took place.</summary>
    Done = 1,

    /// <summary>The time no longer works and a new one has to be agreed. When the customer then confirms a new time in the same conversation, the booking is updated in place and goes back to Pending.</summary>
    Reschedule = 2
}
