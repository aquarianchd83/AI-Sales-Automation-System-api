namespace WhatsAppSalesAutomation.Application.Razorpay;

/// <summary>Hands a stored Razorpay webhook event to the background job that applies it. Implemented in Infrastructure over Hangfire.
/// Best-effort by design: the event is already saved when this is called, and the retry pass applies anything this never took.</summary>
public interface IRazorpayWebhookQueue
{
    /// <param name="webhookEventRowId">The <c>RazorpayWebhookEvent</c> row's own id - not Razorpay's event id.</param>
    void Enqueue(Guid webhookEventRowId);
}
