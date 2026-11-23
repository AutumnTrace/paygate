using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paygate.Application.Webhooks;

namespace Paygate.Api.Tests;

public sealed class WebhookApiTests(PaygateApiFactory factory) : IClassFixture<PaygateApiFactory>
{
    // Demo secret from appsettings.Development.json.
    private const string OspreySecret = "whsec_osprey_demo_8a3d6b1f0e9c2754";

    private readonly HttpClient _merchant = factory.CreateClient(PaygateApiFactory.MerchantKey);
    private readonly HttpClient _provider = factory.CreateClient();

    private async Task<JsonNode> PendingBankTransferAsync()
    {
        var payment = await (await _merchant.PostIdempotentAsync("/v1/payments", new
        {
            amount = 50_000,
            currency = "SEK",
            method = "bank_transfer",
            country = "SE",
            customerId = "player-7",
        })).ReadSuccessJsonAsync(HttpStatusCode.Created);
        Assert.Equal("pending", payment.Str("status"));
        return payment;
    }

    private Task<HttpResponseMessage> DeliverAsync(
        string provider, object payload, string secret = OspreySecret, DateTimeOffset? at = null, Func<string, string>? tamper = null)
    {
        var body = JsonSerializer.Serialize(payload);
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/webhooks/{provider}")
        {
            Content = new StringContent(tamper?.Invoke(body) ?? body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, timestamp, Encoding.UTF8.GetBytes(body)));
        return _provider.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<string> StatusOfAsync(string paymentId) =>
        (await _merchant.GetFromJsonAsync<JsonNode>($"/v1/payments/{paymentId}", TestContext.Current.CancellationToken))!.Str("status");

    [Fact]
    public async Task A_signed_webhook_captures_a_pending_payment()
    {
        var payment = await PendingBankTransferAsync();

        var response = await DeliverAsync("osprey", new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", providerReference = payment.Str("providerReference") });

        var result = await response.ReadSuccessJsonAsync(HttpStatusCode.OK);
        Assert.Equal(("applied", "captured"), (result.Str("outcome"), result.Str("paymentStatus")));
        Assert.Equal("captured", await StatusOfAsync(payment.Str("id")));
    }

    [Fact]
    public async Task A_redelivered_event_is_processed_only_once()
    {
        var payment = await PendingBankTransferAsync();
        var webhook = new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = payment.Str("id") };

        var first = await (await DeliverAsync("osprey", webhook)).ReadJsonAsync();
        var second = await (await DeliverAsync("osprey", webhook)).ReadJsonAsync();

        Assert.Equal("applied", first.Str("outcome"));
        Assert.Equal("duplicate", second.Str("outcome"));
    }

    [Fact]
    public async Task An_out_of_order_event_is_acknowledged_but_never_moves_the_payment_backwards()
    {
        var payment = await PendingBankTransferAsync();
        await DeliverAsync("osprey", new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = payment.Str("id") });

        // "authorized" was sent before "captured" but arrives after it.
        var late = await DeliverAsync("osprey", new { id = $"evt_{Guid.NewGuid():N}", type = "payment.authorized", paymentId = payment.Str("id") });

        var result = await late.ReadSuccessJsonAsync(HttpStatusCode.OK);
        Assert.Equal(("stale", "captured"), (result.Str("outcome"), result.Str("paymentStatus")));
        Assert.Equal("captured", await StatusOfAsync(payment.Str("id")));
    }

    [Fact]
    public async Task A_failed_webhook_keeps_the_provider_failure_code()
    {
        var payment = await PendingBankTransferAsync();

        await DeliverAsync("osprey", new { id = $"evt_{Guid.NewGuid():N}", type = "payment.failed", paymentId = payment.Str("id"), failureCode = "account_closed" });

        var current = await _merchant.GetFromJsonAsync<JsonNode>($"/v1/payments/{payment.Str("id")}", TestContext.Current.CancellationToken);
        Assert.Equal(("failed", "account_closed"), (current!.Str("status"), current!.Str("failure.code")));
    }

    [Fact]
    public async Task A_webhook_from_a_provider_that_does_not_own_the_payment_is_flagged()
    {
        var payment = await PendingBankTransferAsync();

        var response = await DeliverAsync(
            "heron",
            new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = payment.Str("id") },
            secret: "whsec_heron_demo_5f1c8e2a9b7d4036");

        Assert.Equal("provider_mismatch", (await response.ReadSuccessJsonAsync(HttpStatusCode.OK)).Str("outcome"));
        Assert.Equal("pending", await StatusOfAsync(payment.Str("id")));
    }

    [Fact]
    public async Task A_bad_signature_is_rejected_and_changes_nothing()
    {
        var payment = await PendingBankTransferAsync();

        var response = await DeliverAsync(
            "osprey",
            new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = payment.Str("id") },
            tamper: body => body.Replace("captured", "expired", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_signature", (await response.ReadJsonAsync()).Str("code"));
        Assert.Equal("pending", await StatusOfAsync(payment.Str("id")));
    }

    [Fact]
    public async Task An_old_delivery_is_rejected_as_a_replay()
    {
        var payment = await PendingBankTransferAsync();

        var response = await DeliverAsync(
            "osprey",
            new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = payment.Str("id") },
            at: DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("timestamp_outside_tolerance", (await response.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Unsigned_requests_are_rejected()
    {
        var response = await _provider.PostAsJsonAsync("/v1/webhooks/osprey", new { id = "evt_1", type = "payment.captured" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing_signature", (await response.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Unknown_providers_and_payments_are_404()
    {
        var unknownProvider = await DeliverAsync("nope", new { id = "evt_1", type = "payment.captured" });
        var unknownPayment = await DeliverAsync("osprey", new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, unknownProvider.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownPayment.StatusCode);
        Assert.Equal("payment_not_found", (await unknownPayment.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task Event_types_the_gateway_does_not_use_are_acknowledged()
    {
        var response = await DeliverAsync("osprey", new { id = $"evt_{Guid.NewGuid():N}", type = "settlement.report_ready" });

        Assert.Equal("ignored", (await response.ReadSuccessJsonAsync(HttpStatusCode.OK)).Str("outcome"));
    }

    [Fact]
    public async Task A_payload_without_an_event_id_is_rejected()
    {
        var response = await DeliverAsync("osprey", new { type = "payment.captured" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_payload", (await response.ReadJsonAsync()).Str("code"));
    }
}
