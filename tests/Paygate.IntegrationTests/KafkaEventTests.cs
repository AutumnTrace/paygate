using System.Text;
using System.Text.Json.Nodes;
using Confluent.Kafka;

namespace Paygate.IntegrationTests;

/// <summary>Domain events travel from the outbox in PostgreSQL to a real Kafka topic.</summary>
[Trait("Category", "Integration")]
[Collection(StackCollection.Name)]
public sealed class KafkaEventTests(StackFixture stack) : IDisposable
{
    private readonly StackApiFactory _factory = new(stack);

    [Fact]
    public async Task Payment_events_reach_the_topic_in_order_with_cloudevents_headers()
    {
        var client = _factory.CreateMerchantClient();
        var payment = await (await client.PostIdempotentAsync("/v1/payments", Http.CardDeposit(3_100))).ReadJsonAsync();
        var paymentId = payment.Str("id");

        var messages = await ConsumeAsync(paymentId, expected: 2, TimeSpan.FromSeconds(60));

        Assert.Equal(["payment.created.v1", "payment.status_changed.v1"], messages.Select(m => Header(m, "ce_type")));
        Assert.All(messages, m => Assert.Equal(paymentId, m.Message.Key));
        Assert.All(messages, m => Assert.Equal("1.0", Header(m, "ce_specversion")));
        Assert.Single(messages.Select(m => m.Partition.Value).Distinct());

        var changed = JsonNode.Parse(messages[1].Message.Value)!;
        Assert.Equal("captured", changed["to"]!.ToString());
        Assert.Equal("3100", changed["amountMinor"]!.ToString());
        Assert.Equal(Header(messages[1], "ce_id"), changed["eventId"]!.ToString());
    }

    private async Task<List<ConsumeResult<string, string>>> ConsumeAsync(string key, int expected, TimeSpan timeout)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = stack.Kafka.GetBootstrapAddress(),
            GroupId = $"tests-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(StackFixture.Topic);

        var found = new List<ConsumeResult<string, string>>();
        var deadline = DateTime.UtcNow + timeout;
        while (found.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = await Task.Run(() => consumer.Consume(TimeSpan.FromMilliseconds(500)), TestContext.Current.CancellationToken);
            if (result?.Message?.Key == key)
            {
                found.Add(result);
            }
        }

        consumer.Close();
        Assert.True(found.Count >= expected, $"Expected {expected} events for {key}, got {found.Count}.");
        return found;
    }

    private static string Header(ConsumeResult<string, string> result, string name) =>
        Encoding.UTF8.GetString(result.Message.Headers.GetLastBytes(name));

    public void Dispose() => _factory.Dispose();
}
