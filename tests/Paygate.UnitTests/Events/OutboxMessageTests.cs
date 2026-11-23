using System.Text.Json.Nodes;
using Paygate.Domain.Payments;
using Paygate.Infrastructure.Events;

namespace Paygate.UnitTests.Events;

public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Events_are_keyed_by_payment_and_lead_with_the_envelope_fields()
    {
        var paymentId = Guid.CreateVersion7();
        var changed = new PaymentStatusChanged(paymentId, "m_demo", PaymentKind.Deposit, PaymentStatus.Pending, PaymentStatus.Captured, "osprey", 50_000, "SEK", Now);

        var message = OutboxMessage.From(changed);
        var json = JsonNode.Parse(message.Payload)!.AsObject();

        Assert.Equal(paymentId.ToString(), message.Key);
        Assert.Equal("payment.status_changed.v1", message.Type);
        Assert.Equal(changed.EventId, message.ToEnvelope().Id);
        Assert.Equal(
            ["eventId", "eventType", "paymentId", "merchantId", "occurredAt", "kind", "from", "to", "provider", "amountMinor", "currency"],
            json.Select(p => p.Key));
        Assert.Equal(("pending", "captured"), (json["from"]!.ToString(), json["to"]!.ToString()));
    }

    [Fact]
    public void Partially_refunded_status_is_snake_case_in_the_payload()
    {
        var changed = new PaymentStatusChanged(Guid.NewGuid(), "m_demo", PaymentKind.Deposit, PaymentStatus.Captured, PaymentStatus.PartiallyRefunded, "heron", 100, "EUR", Now);

        var json = JsonNode.Parse(OutboxMessage.From(changed).Payload)!;

        Assert.Equal("partially_refunded", json["to"]!.ToString());
    }
}
