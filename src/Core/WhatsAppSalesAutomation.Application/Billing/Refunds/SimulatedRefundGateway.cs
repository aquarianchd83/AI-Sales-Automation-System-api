using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Application.Billing.Refunds;

/// <summary>Stands in for a real gateway while payments are simulated: every refund succeeds and gets a
/// generated reference. Replaced, not extended, once Razorpay is wired in.</summary>
public class SimulatedRefundGateway : IRefundGateway
{
    public string Name => "Simulated";

    public Task<RefundGatewayResult> RefundAsync(Payment payment, int amountCents, decimal localAmount, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RefundGatewayResult(true, $"sim_refund_{Guid.NewGuid():N}", null));
}
