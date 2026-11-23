using System.Net;

namespace Paygate.Api.Tests;

public sealed class RefundApiTests(PaygateApiFactory factory) : IClassFixture<PaygateApiFactory>
{
    private readonly HttpClient _merchant = factory.CreateClient(PaygateApiFactory.MerchantKey);

    private async Task<string> CapturedDepositAsync(long amount = 10_000)
    {
        var payment = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount))).ReadJsonAsync();
        Assert.Equal("captured", payment.Str("status"));
        return payment.Str("id");
    }

    [Fact]
    public async Task Partial_then_full_refund()
    {
        var id = await CapturedDepositAsync(10_000);

        var partial = await (await _merchant.PostIdempotentAsync($"/v1/payments/{id}/refunds", new { amount = 2_500, reason = "goodwill" }))
            .ReadSuccessJsonAsync(HttpStatusCode.Created);
        var full = await (await _merchant.PostIdempotentAsync($"/v1/payments/{id}/refunds", new { amount = 7_500 }))
            .ReadSuccessJsonAsync(HttpStatusCode.Created);

        Assert.Equal("partially_refunded", partial.Str("payment.status"));
        Assert.Equal("2500", partial.Str("refund.amount"));
        Assert.StartsWith("hrn_rf_", partial.Str("refund.providerReference"), StringComparison.Ordinal);
        Assert.Equal(("refunded", "10000"), (full.Str("payment.status"), full.Str("payment.refundedAmount")));
    }

    [Fact]
    public async Task Refunding_more_than_was_captured_is_rejected()
    {
        var id = await CapturedDepositAsync(5_000);

        var response = await _merchant.PostIdempotentAsync($"/v1/payments/{id}/refunds", new { amount = 5_001 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("refund_exceeds_captured_amount", problem.Str("code"));
        Assert.Contains("50.00 EUR", problem.Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payment_that_is_not_captured_cannot_be_refunded()
    {
        var pending = await (await _merchant.PostIdempotentAsync("/v1/payments", new
        {
            amount = 50_000,
            currency = "SEK",
            method = "bank_transfer",
            country = "SE",
            customerId = "player-7",
        })).ReadJsonAsync();

        var response = await _merchant.PostIdempotentAsync($"/v1/payments/{pending.Str("id")}/refunds", new { amount = 100 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("payment_not_refundable", (await response.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Refund_of_an_unknown_payment_is_404()
    {
        var response = await _merchant.PostIdempotentAsync($"/v1/payments/{Guid.NewGuid()}/refunds", new { amount = 100 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Refund_while_the_acquirer_is_down_is_503_and_can_be_retried_with_the_same_key()
    {
        using var isolated = new PaygateApiFactory();
        var merchant = isolated.CreateClient(PaygateApiFactory.MerchantKey);
        var admin = isolated.CreateClient(PaygateApiFactory.AdminKey);
        var payment = await (await merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(10_000))).ReadJsonAsync();
        var key = Guid.NewGuid().ToString();
        var url = $"/v1/payments/{payment.Str("id")}/refunds";

        await admin.PutAsync("/v1/admin/providers/heron/faults", Json(new { outage = "errors" }), TestContext.Current.CancellationToken);
        var failed = await merchant.PostIdempotentAsync(url, new { amount = 1_000 }, key);
        await admin.PutAsync("/v1/admin/providers/heron/faults", Json(new { outage = "none" }), TestContext.Current.CancellationToken);
        await admin.PostAsync("/v1/admin/providers/heron/circuit/close", null, TestContext.Current.CancellationToken);
        var retried = await merchant.PostIdempotentAsync(url, new { amount = 1_000 }, key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.NotNull(failed.Headers.RetryAfter);
        Assert.Equal("provider_unavailable", (await failed.ReadJsonAsync()).Str("code"));
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
    }

    private static StringContent Json(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
}
