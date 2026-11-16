using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Infrastructure.Payments;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The HTTP side of plans and subscriptions: the paths, Razorpay's snake_case bodies, and its answers read back - including the
/// nulls it sends for a mandate nobody has authorised yet.</summary>
public class RazorpaySubscriptionGatewayTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }

    private static readonly RazorpayCredentials Keys = new("rzp_test_1DP5mmOlF5G5ag", "secret");
    private static readonly IReadOnlyDictionary<string, string> Notes = new Dictionary<string, string> { ["source"] = "autopay" };

    private const string CreatedSubscription =
        "{\"id\":\"sub_1\",\"entity\":\"subscription\",\"plan_id\":\"plan_1\",\"status\":\"created\",\"current_start\":null,\"current_end\":null," +
        "\"ended_at\":null,\"quantity\":1,\"notes\":{\"source\":\"autopay\"},\"charge_at\":null,\"start_at\":null,\"end_at\":null,\"auth_attempts\":0," +
        "\"total_count\":120,\"paid_count\":0,\"customer_notify\":false,\"created_at\":1757505600,\"expire_by\":1757678400," +
        "\"short_url\":\"https://rzp.io/i/abc\",\"has_scheduled_changes\":false,\"remaining_count\":120,\"customer_id\":null,\"payment_method\":null}";

    private const string ActiveSubscription =
        "{\"id\":\"sub_1\",\"plan_id\":\"plan_1\",\"status\":\"active\",\"current_start\":1757505600,\"current_end\":1760097600,\"ended_at\":null," +
        "\"charge_at\":1760097600,\"start_at\":1757505600,\"end_at\":2073124800,\"total_count\":120,\"paid_count\":1,\"remaining_count\":119," +
        "\"short_url\":\"https://rzp.io/i/abc\",\"customer_id\":\"cust_1\",\"payment_method\":\"upi\"}";

    private static RazorpayGateway Gateway(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.razorpay.test/v1/") }, NullLogger<RazorpayGateway>.Instance);

    [Fact]
    public async Task Creating_a_plan_posts_the_period_and_the_amount_in_paise()
    {
        var handler = new StubHandler
        {
            Body = "{\"id\":\"plan_1\",\"entity\":\"plan\",\"interval\":1,\"period\":\"monthly\",\"item\":{\"id\":\"item_1\",\"name\":\"Premium (monthly)\",\"amount\":284026,\"currency\":\"INR\"}}"
        };

        var result = await Gateway(handler).CreatePlanAsync(Keys, "monthly", 1, "Premium (monthly)", 284026, "INR", Notes);

        Assert.True(result.Success);
        Assert.Equal(new RazorpayPlanInfo("plan_1", "monthly", 1, 284026, "INR"), result.Value);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.razorpay.test/v1/plans", handler.Request.RequestUri!.ToString());
        Assert.Equal("Basic", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("\"period\":\"monthly\"", handler.RequestBody);
        Assert.Contains("\"interval\":1", handler.RequestBody);
        Assert.Contains("\"item\":{\"name\":\"Premium (monthly)\",\"amount\":284026,\"currency\":\"INR\"}", handler.RequestBody);
    }

    [Fact]
    public async Task Creating_a_subscription_posts_the_plan_the_count_and_the_expiry()
    {
        var handler = new StubHandler { Body = CreatedSubscription };

        var result = await Gateway(handler).CreateSubscriptionAsync(Keys, "plan_1", 120, false, 1757678400, Notes);

        Assert.True(result.Success);
        Assert.Equal("https://api.razorpay.test/v1/subscriptions", handler.Request!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Contains("\"plan_id\":\"plan_1\"", handler.RequestBody);
        Assert.Contains("\"total_count\":120", handler.RequestBody);
        Assert.Contains("\"quantity\":1", handler.RequestBody);
        Assert.Contains("\"customer_notify\":false", handler.RequestBody);
        Assert.Contains("\"expire_by\":1757678400", handler.RequestBody);
        Assert.Contains("\"notes\":{\"source\":\"autopay\"}", handler.RequestBody);
    }

    [Fact]
    public async Task With_no_expiry_the_field_is_left_out_altogether()
    {
        var handler = new StubHandler { Body = CreatedSubscription };

        await Gateway(handler).CreateSubscriptionAsync(Keys, "plan_1", 120, false, null, Notes);

        Assert.DoesNotContain("expire_by", handler.RequestBody);
    }

    [Fact]
    public async Task A_subscription_nobody_has_authorised_reads_back_with_its_nulls()
    {
        var handler = new StubHandler { Body = CreatedSubscription };

        var result = await Gateway(handler).CreateSubscriptionAsync(Keys, "plan_1", 120, false, null, Notes);

        Assert.Equal(
            new RazorpaySubscriptionInfo("sub_1", "plan_1", "created", "https://rzp.io/i/abc", 120, 0, 120, null, null, null, null, null, null, null, null),
            result.Value);
    }

    [Fact]
    public async Task A_running_subscription_is_read_from_razorpays_snake_case()
    {
        var handler = new StubHandler { Body = ActiveSubscription };

        var result = await Gateway(handler).FetchSubscriptionAsync(Keys, "sub_1");

        Assert.Equal(
            new RazorpaySubscriptionInfo("sub_1", "plan_1", "active", "https://rzp.io/i/abc", 120, 1, 119, 1757505600, 1760097600, 1760097600, 1757505600, 2073124800, null, "cust_1", "upi"),
            result.Value);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://api.razorpay.test/v1/subscriptions/sub_1", handler.Request.RequestUri!.ToString());
        Assert.Null(handler.RequestBody);
    }

    [Theory]
    [InlineData(true, "{\"cancel_at_cycle_end\":true}")]
    [InlineData(false, "{\"cancel_at_cycle_end\":false}")]
    public async Task Cancelling_says_whether_to_wait_for_the_cycle_to_end(bool atCycleEnd, string expectedBody)
    {
        var handler = new StubHandler { Body = ActiveSubscription.Replace("\"status\":\"active\"", "\"status\":\"cancelled\"").Replace("\"ended_at\":null", "\"ended_at\":1757600000") };

        var result = await Gateway(handler).CancelSubscriptionAsync(Keys, "sub_1", atCycleEnd);

        Assert.Equal("https://api.razorpay.test/v1/subscriptions/sub_1/cancel", handler.Request!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal(expectedBody, handler.RequestBody);
        Assert.Equal("cancelled", result.Value!.Status);
        Assert.Equal(1757600000, result.Value.EndedAt);
    }

    [Fact]
    public async Task Subscriptions_not_enabled_on_the_account_comes_back_as_razorpays_own_message()
    {
        var handler = new StubHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = "{\"error\":{\"code\":\"BAD_REQUEST_ERROR\",\"description\":\"The requested URL was not found on the server.\"}}"
        };

        var result = await Gateway(handler).CreateSubscriptionAsync(Keys, "plan_1", 120, false, null, Notes);

        Assert.False(result.Success);
        Assert.Equal("The requested URL was not found on the server.", result.Error);
    }

    [Fact]
    public async Task A_reply_with_no_id_is_a_failure_not_an_empty_subscription()
    {
        var handler = new StubHandler { Body = "{}" };

        var result = await Gateway(handler).FetchSubscriptionAsync(Keys, "sub_1");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task An_id_is_escaped_into_the_path()
    {
        var handler = new StubHandler { Body = ActiveSubscription };

        await Gateway(handler).FetchSubscriptionAsync(Keys, "sub_1/../../payments");

        Assert.Equal("https://api.razorpay.test/v1/subscriptions/sub_1%2F..%2F..%2Fpayments", handler.Request!.RequestUri!.AbsoluteUri);
    }
}
