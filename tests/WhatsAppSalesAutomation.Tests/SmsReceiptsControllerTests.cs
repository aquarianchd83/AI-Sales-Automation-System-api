using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The SMS provider's delivery-receipt endpoint: closed without a configured secret, and idempotent behind it.</summary>
public sealed class SmsReceiptsControllerTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);

    private readonly RecordingConsent _consent = new();

    private SmsReceiptsController Controller(string? secret)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(secret is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Sms:ReceiptSecret"] = secret })
            .Build();
        return new SmsReceiptsController(_consent, new FixedClock(), config, NullLogger<SmsReceiptsController>.Instance);
    }

    private static SmsReceiptsController.SmsReceiptRequest Receipt(string status = "delivered", string id = "prov-1") => new(id, status, null, "why");

    [Fact]
    public async Task Without_a_configured_secret_every_receipt_is_refused()
    {
        var result = await Controller(null).Receive(Receipt(), "anything", default);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(_consent.Applied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task A_missing_or_wrong_secret_is_unauthorized(string? header)
    {
        var result = await Controller("s3cret").Receive(Receipt(), header, default);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(_consent.Applied);
    }

    [Theory]
    [InlineData("delivered", ConsentInvitationStatus.Delivered)]
    [InlineData("Delivered", ConsentInvitationStatus.Delivered)]
    [InlineData("failed", ConsentInvitationStatus.Failed)]
    [InlineData("undelivered", ConsentInvitationStatus.Failed)]
    public async Task A_valid_receipt_is_applied_with_the_provider_id_status_and_reason(string status, ConsentInvitationStatus expected)
    {
        var result = await Controller("s3cret").Receive(Receipt(status), "s3cret", default);

        Assert.IsType<AcceptedResult>(result);
        var applied = Assert.Single(_consent.Applied);
        Assert.Equal(("prov-1", expected, "why"), (applied.ProviderMessageId, applied.Status, applied.Reason));
    }

    [Fact]
    public async Task An_unknown_status_is_a_bad_request()
    {
        var result = await Controller("s3cret").Receive(Receipt("maybe"), "s3cret", default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_consent.Applied);
    }

    [Fact]
    public async Task A_duplicate_or_unknown_receipt_is_still_accepted_so_the_provider_does_not_keep_retrying()
    {
        _consent.Result = false;

        var result = await Controller("s3cret").Receive(Receipt(), "s3cret", default);

        Assert.IsType<AcceptedResult>(result);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    private sealed class RecordingConsent : IConsentInvitationService
    {
        public bool Result { get; set; } = true;
        public List<(string ProviderMessageId, ConsentInvitationStatus Status, DateTime At, string? Reason)> Applied { get; } = new();

        public Task<bool> ApplyReceiptAsync(string providerMessageId, ConsentInvitationStatus status, DateTime atUtc, string? reason, CancellationToken cancellationToken = default)
        {
            Applied.Add((providerMessageId, status, atUtc, reason));
            return Task.FromResult(Result);
        }

        public Task<ConsentInvitationBatchResult> SendAsync(Guid campaignId, Guid? executionId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> RetryDueAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<ConsentReplyOutcome> HandleInboundMessageAsync(Customer customer, InboundConsentMessage message, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
