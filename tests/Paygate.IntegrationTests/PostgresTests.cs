using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paygate.Application.Payments;
using Paygate.Application.Webhooks;
using Paygate.Domain.Common;

namespace Paygate.IntegrationTests;

/// <summary>The concurrency guarantees, proven against a real PostgreSQL rather than SQLite.</summary>
[Trait("Category", "Integration")]
[Collection(StackCollection.Name)]
public sealed class PostgresTests(StackFixture stack) : IDisposable
{
    private readonly StackApiFactory _factory = new(stack);

    [Fact]
    public async Task Deposit_and_idempotent_replay_work_on_postgres()
    {
        var client = _factory.CreateMerchantClient();
        var key = Guid.NewGuid().ToString();

        var first = await client.PostIdempotentAsync("/v1/payments", Http.CardDeposit(), key);
        var replay = await client.PostIdempotentAsync("/v1/payments", Http.CardDeposit(), key);
        var payment = await first.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("captured", payment.Str("status"));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(payment.Str("id"), (await replay.ReadJsonAsync()).Str("id"));
    }

    [Fact]
    public async Task Concurrent_duplicates_create_exactly_one_payment()
    {
        _factory.Settings["Providers:heron:Faults:LatencyMs"] = "200";
        var client = _factory.CreateMerchantClient();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostIdempotentAsync("/v1/payments", Http.CardDeposit(4_200), key)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains("Idempotent-Replayed"));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Unexpected {(int)r.StatusCode}"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM payments"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM idempotency_keys"));
    }

    [Fact]
    public async Task Concurrent_redeliveries_of_one_webhook_apply_it_once()
    {
        var client = _factory.CreateMerchantClient();
        var payment = await (await client.PostIdempotentAsync("/v1/payments", Http.BankTransfer())).ReadJsonAsync();
        var body = JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type = "payment.captured", paymentId = payment.Str("id") });

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => DeliverAsync("osprey", body)));
        var outcomes = await Task.WhenAll(responses.Where(r => r.IsSuccessStatusCode).Select(async r => (await r.ReadJsonAsync()).Str("outcome")));

        // Losers of the race see either the inbox row (duplicate) or the bumped payment version (409, which
        // providers retry). Either way the event is applied exactly once.
        Assert.Single(outcomes, o => o == "applied");
        Assert.All(outcomes, o => Assert.Contains(o, new[] { "applied", "duplicate" }));
        Assert.All(responses.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM webhook_events"));
        Assert.Equal("Captured", await ScalarAsync($"SELECT status FROM payments WHERE id = '{payment.Str("id")}'"));
    }

    [Fact]
    public async Task Optimistic_concurrency_rejects_a_stale_write()
    {
        var client = _factory.CreateMerchantClient();
        var id = Guid.Parse((await (await client.PostIdempotentAsync("/v1/payments", Http.CardDeposit(10_000))).ReadJsonAsync()).Str("id"));

        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();
        var repoA = first.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var repoB = second.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var a = (await repoA.GetAsync("m_demo", id, TestContext.Current.CancellationToken))!;
        var b = (await repoB.GetAsync("m_demo", id, TestContext.Current.CancellationToken))!;

        a.Refund(Guid.NewGuid(), Money.Of(6_000, "EUR"), null, "hrn_rf_a", DateTimeOffset.UtcNow);
        await repoA.SaveChangesAsync(TestContext.Current.CancellationToken);
        b.Refund(Guid.NewGuid(), Money.Of(6_000, "EUR"), null, "hrn_rf_b", DateTimeOffset.UtcNow);

        // Without the version check both refunds would land and 120.00 EUR would leave a 100.00 EUR payment.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repoB.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6_000L, await ScalarAsync($"SELECT refunded_minor FROM payments WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Schema_has_the_indexes_the_queries_rely_on()
    {
        await _factory.CreateMerchantClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);

        var indexes = new List<string>();
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = 'public'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains(indexes, i => i.Contains("ON public.payments", StringComparison.Ordinal) && i.Contains("(merchant_id, created_at DESC)", StringComparison.Ordinal));
        Assert.Contains(indexes, i => i.Contains("UNIQUE", StringComparison.Ordinal) && i.Contains("(provider, provider_reference)", StringComparison.Ordinal));
        Assert.Contains(indexes, i => i.Contains("ON public.idempotency_keys", StringComparison.Ordinal) && i.Contains("(merchant_id, key)", StringComparison.Ordinal));
        Assert.Contains(indexes, i => i.Contains("ON public.idempotency_keys", StringComparison.Ordinal) && i.Contains("(expires_at)", StringComparison.Ordinal));
        Assert.Contains(indexes, i => i.Contains("ON public.webhook_events", StringComparison.Ordinal) && i.Contains("(provider, event_id)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_checks_postgres_and_kafka()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checks = body["checks"]!.AsArray().Select(c => (c!.Str("name"), c!.Str("status"))).ToList();
        Assert.Contains(("database", "Healthy"), checks);
        Assert.Contains(("kafka", "Healthy"), checks);
    }

    private async Task<HttpResponseMessage> DeliverAsync(string provider, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/webhooks/{provider}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(StackApiFactory.OspreyWebhookSecret, timestamp, Encoding.UTF8.GetBytes(body)));
        return await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _factory.Dispose();
}
