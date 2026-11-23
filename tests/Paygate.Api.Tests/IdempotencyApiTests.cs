using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Paygate.Application.Idempotency;

namespace Paygate.Api.Tests;

public sealed class IdempotencyApiTests(PaygateApiFactory factory) : IClassFixture<PaygateApiFactory>
{
    private readonly HttpClient _merchant = factory.CreateClient(PaygateApiFactory.MerchantKey);

    [Fact]
    public async Task Retrying_with_the_same_key_replays_the_original_response()
    {
        var key = Guid.NewGuid().ToString();
        var body = HttpTestExtensions.CardDeposit(amount: 7_700);

        var first = await _merchant.PostIdempotentAsync("/v1/payments", body, key);
        var retry = await _merchant.PostIdempotentAsync("/v1/payments", body, key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_rejected()
    {
        var key = Guid.NewGuid().ToString();
        await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 1_000), key);

        var reused = await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 9_999), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", (await reused.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task A_request_whose_key_is_still_in_flight_gets_409()
    {
        var key = Guid.NewGuid().ToString();
        var body = HttpTestExtensions.CardDeposit(amount: 3_300);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);

        // Simulate the first request still running: claim the key exactly as the middleware would.
        var store = factory.Services.GetRequiredService<IIdempotencyStore>();
        var decision = await store.BeginAsync(
            new IdempotencyRequest("m_demo", key, RequestFingerprint.Compute("POST", "/v1/payments", json), "POST", "/v1/payments"),
            TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyOutcome.Proceed, decision.Outcome);

        var duplicate = await _merchant.PostIdempotentAsync("/v1/payments", body, key);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("1", duplicate.Headers.RetryAfter?.ToString());
        Assert.Equal("idempotency_key_in_use", (await duplicate.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Concurrent_duplicates_create_exactly_one_payment()
    {
        using var slowFactory = new PaygateApiFactory();
        slowFactory.Settings["Providers:heron:Faults:LatencyMs"] = "300";
        var client = slowFactory.CreateClient(PaygateApiFactory.MerchantKey);
        var key = Guid.NewGuid().ToString();
        var body = HttpTestExtensions.CardDeposit(amount: 4_200);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostIdempotentAsync("/v1/payments", body, key)));

        var fresh = responses.Where(r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains("Idempotent-Replayed")).ToList();
        Assert.Single(fresh);
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected {(int)r.StatusCode}"));

        var list = await client.GetAsync("/v1/payments", TestContext.Current.CancellationToken);
        Assert.Single((await list.ReadJsonAsync())["data"]!.AsArray());
    }

    [Fact]
    public async Task Keys_are_scoped_per_merchant()
    {
        var key = Guid.NewGuid().ToString();
        var other = factory.CreateClient(PaygateApiFactory.OtherMerchantKey);

        var mine = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(), key)).ReadJsonAsync();
        var theirs = await (await other.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(), key)).ReadJsonAsync();

        Assert.NotEqual(mine.Str("id"), theirs.Str("id"));
    }

    [Fact]
    public async Task Missing_key_is_rejected_before_anything_happens()
    {
        var response = await _merchant.PostAsJsonAsync("/v1/payments", HttpTestExtensions.CardDeposit(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", (await response.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task A_validation_error_does_not_burn_the_key()
    {
        var key = Guid.NewGuid().ToString();
        var invalid = await _merchant.PostIdempotentAsync("/v1/payments", new { amount = 100, currency = "EUR", method = "card", country = "de", customerId = "p", paymentToken = "tok_visa_4242" }, key);

        var corrected = await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 100), key);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
    }

    [Fact]
    public async Task Refund_retries_replay_instead_of_refunding_twice()
    {
        var payment = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 10_000))).ReadJsonAsync();
        var key = Guid.NewGuid().ToString();
        var url = $"/v1/payments/{payment.Str("id")}/refunds";

        var first = await _merchant.PostIdempotentAsync(url, new { amount = 4_000, reason = "duplicate bet" }, key);
        var retry = await _merchant.PostIdempotentAsync(url, new { amount = 4_000, reason = "duplicate bet" }, key);
        var current = await _merchant.GetFromJsonAsync<JsonNode>($"/v1/payments/{payment.Str("id")}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("4000", current!.Str("refundedAmount"));
        Assert.Single(current!["refunds"]!.AsArray());
    }
}
