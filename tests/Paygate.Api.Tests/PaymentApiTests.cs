using System.Net;
using System.Net.Http.Json;

namespace Paygate.Api.Tests;

public sealed class PaymentApiTests(PaygateApiFactory factory) : IClassFixture<PaygateApiFactory>
{
    private readonly HttpClient _merchant = factory.CreateClient(PaygateApiFactory.MerchantKey);

    [Fact]
    public async Task Card_deposit_is_captured_by_the_primary_acquirer()
    {
        var response = await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit());

        var payment = await response.ReadSuccessJsonAsync(HttpStatusCode.Created);
        Assert.Equal("captured", payment.Str("status"));
        Assert.Equal("heron", payment.Str("provider"));
        Assert.Equal($"/v1/payments/{payment.Str("id")}", response.Headers.Location?.ToString());
        Assert.Equal("accepted", payment.Str("attempts.0.outcome"));
        Assert.StartsWith("hrn_", payment.Str("providerReference"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bank_transfer_deposit_is_pending_until_the_provider_confirms()
    {
        var response = await _merchant.PostIdempotentAsync("/v1/payments", new
        {
            amount = 50_000,
            currency = "SEK",
            method = "bank_transfer",
            country = "SE",
            customerId = "player-7",
        });

        var payment = await response.ReadSuccessJsonAsync(HttpStatusCode.Created);
        Assert.Equal(("pending", "osprey"), (payment.Str("status"), payment.Str("provider")));
    }

    [Fact]
    public async Task Decline_is_returned_as_a_failed_payment_without_failover()
    {
        var response = await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 2_551));

        var payment = await response.ReadSuccessJsonAsync(HttpStatusCode.Created);
        Assert.Equal("failed", payment.Str("status"));
        Assert.Equal("insufficient_funds", payment.Str("failure.code"));
        Assert.Single(payment["attempts"]!.AsArray());
    }

    [Fact]
    public async Task Payment_without_an_eligible_provider_fails_with_no_route()
    {
        // Crypto in GBP: the only crypto provider does not support GBP.
        var response = await _merchant.PostIdempotentAsync("/v1/payments", new
        {
            amount = 1_000,
            currency = "GBP",
            method = "crypto_wallet",
            country = "GB",
            customerId = "player-1",
        });

        var payment = await response.ReadSuccessJsonAsync(HttpStatusCode.Created);
        Assert.Equal(("failed", "no_route"), (payment.Str("status"), payment.Str("failure.code")));
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_validation_problem_details()
    {
        var response = await _merchant.PostIdempotentAsync("/v1/payments", new
        {
            amount = 0,
            currency = "EURO",
            method = "card",
            country = "de",
            customerId = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadJsonAsync();
        var errors = problem["errors"]!.AsObject().Select(e => e.Key).ToList();
        Assert.Contains("Amount", errors);
        Assert.Contains("Currency", errors);
        Assert.Contains("Country", errors);
        Assert.Contains("CustomerId", errors);
    }

    [Fact]
    public async Task Card_deposit_requires_a_payment_token_and_never_accepts_card_numbers()
    {
        var missing = await _merchant.PostIdempotentAsync("/v1/payments", new { amount = 100, currency = "EUR", method = "card", country = "DE", customerId = "p" });
        var rawPan = await _merchant.PostIdempotentAsync("/v1/payments", new { amount = 100, currency = "EUR", method = "card", country = "DE", customerId = "p", paymentToken = "4111111111111111" });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("paymentToken", (await missing.ReadJsonAsync())["errors"]!.AsObject().Select(e => e.Key));
        Assert.Equal(HttpStatusCode.BadRequest, rawPan.StatusCode);
    }

    [Fact]
    public async Task Unsupported_currency_is_a_business_error()
    {
        var response = await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(currency: "XXX"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_currency", (await response.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Payments_are_only_visible_to_the_merchant_that_owns_them()
    {
        var created = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit())).ReadJsonAsync();
        var id = created.Str("id");
        var other = factory.CreateClient(PaygateApiFactory.OtherMerchantKey);

        var own = await _merchant.GetAsync($"/v1/payments/{id}", TestContext.Current.CancellationToken);
        var foreign = await other.GetAsync($"/v1/payments/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("not_found", (await foreign.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Listing_is_newest_first_with_keyset_pagination()
    {
        var client = factory.CreateClient(PaygateApiFactory.OtherMerchantKey);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await (await client.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 1_000 + i))).ReadJsonAsync()).Str("id"));
        }

        var first = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonNode>("/v1/payments?limit=2", TestContext.Current.CancellationToken);
        var next = Uri.EscapeDataString(first!.Str("nextCreatedBefore"));
        var second = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonNode>($"/v1/payments?limit=2&createdBefore={next}", TestContext.Current.CancellationToken);

        var page1 = first!["data"]!.AsArray().Select(p => p!.Str("id")).ToList();
        var page2 = second!["data"]!.AsArray().Select(p => p!.Str("id")).ToList();
        Assert.Equal([ids[2], ids[1]], page1);
        Assert.Equal(ids[0], page2[0]);
    }

    [Fact]
    public async Task Crypto_payout_is_pending_at_the_crypto_provider()
    {
        var response = await _merchant.PostIdempotentAsync("/v1/payouts", new
        {
            amount = 15_000,
            currency = "EUR",
            method = "crypto_wallet",
            country = "DE",
            customerId = "player-9",
            destinationToken = "tok_wallet_btc_01",
        });

        var payout = await response.ReadSuccessJsonAsync(HttpStatusCode.Created);
        Assert.Equal(("payout", "pending", "wren"), (payout.Str("kind"), payout.Str("status"), payout.Str("provider")));
    }
}
