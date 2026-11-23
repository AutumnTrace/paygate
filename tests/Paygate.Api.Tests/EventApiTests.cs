using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Paygate.Api.Tests;

/// <summary>Domain events flow through the transactional outbox to the (in-memory) publisher.</summary>
public sealed class EventApiTests(PaygateApiFactory factory) : IClassFixture<PaygateApiFactory>
{
    private async Task<List<JsonNode>> EventsForAsync(string paymentId, int expected)
    {
        var admin = factory.CreateClient(PaygateApiFactory.AdminKey);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var events = await admin.GetFromJsonAsync<JsonArray>("/v1/admin/events?limit=500", TestContext.Current.CancellationToken);
            var mine = events!.Where(e => e!.Str("key") == paymentId).Select(e => e!).Reverse().ToList();
            if (mine.Count >= expected)
            {
                return mine;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Expected {expected} events for {paymentId}.");
    }

    [Fact]
    public async Task A_captured_deposit_publishes_created_then_status_changed()
    {
        var merchant = factory.CreateClient(PaygateApiFactory.MerchantKey);
        var payment = await (await merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 6_000))).ReadJsonAsync();

        var events = await EventsForAsync(payment.Str("id"), expected: 2);

        Assert.Equal(["payment.created.v1", "payment.status_changed.v1"], events.Select(e => e.Str("type")));
        var changed = events[1]["data"]!;
        Assert.Equal(("created", "captured", "heron"), (changed.Str("from"), changed.Str("to"), changed.Str("provider")));
        Assert.Equal("6000", changed.Str("amountMinor"));
        Assert.Equal("EUR", changed.Str("currency"));
    }

    [Fact]
    public async Task A_refund_publishes_a_refund_event()
    {
        var merchant = factory.CreateClient(PaygateApiFactory.MerchantKey);
        var payment = await (await merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit(amount: 9_000))).ReadJsonAsync();
        await merchant.PostIdempotentAsync($"/v1/payments/{payment.Str("id")}/refunds", new { amount = 1_500 });

        var events = await EventsForAsync(payment.Str("id"), expected: 4);

        var refunded = events.Single(e => e.Str("type") == "payment.refunded.v1")["data"]!;
        Assert.Equal(("1500", "1500"), (refunded.Str("refundAmountMinor"), refunded.Str("totalRefundedMinor")));
    }

    [Fact]
    public async Task The_events_endpoint_is_admin_only()
    {
        var response = await factory.CreateClient(PaygateApiFactory.MerchantKey).GetAsync("/v1/admin/events", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
