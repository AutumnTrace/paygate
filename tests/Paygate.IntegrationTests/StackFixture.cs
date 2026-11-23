using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Paygate.IntegrationTests;

/// <summary>Real PostgreSQL and Kafka in containers, started once for the whole test run.</summary>
public sealed class StackFixture : IAsyncLifetime
{
    public const string Topic = "payments.v1";

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public KafkaContainer Kafka { get; } = new KafkaBuilder("apache/kafka:4.3.2").Build();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), Kafka.StartAsync());

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Kafka.GetBootstrapAddress() }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = Topic, NumPartitions = 3, ReplicationFactor = 1 }]);
    }

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Kafka.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}

/// <summary>The real API on PostgreSQL with the Kafka publisher. Each factory gets its own database.</summary>
public sealed class StackApiFactory(StackFixture stack) : WebApplicationFactory<Program>
{
    public const string MerchantKey = "pgk_test_demo_7c2f9a41e8b35d06";
    public const string OspreyWebhookSecret = "whsec_osprey_demo_8a3d6b1f0e9c2754";

    public Dictionary<string, string> Settings { get; } = [];

    public string ConnectionString { get; } = new NpgsqlConnectionStringBuilder(stack.Postgres.GetConnectionString())
    {
        Database = $"paygate_{Guid.NewGuid():N}",
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Database:Provider", "Postgres");
        builder.UseSetting("ConnectionStrings:Paygate", ConnectionString);
        builder.UseSetting("Events:Publisher", "Kafka");
        builder.UseSetting("Events:PollInterval", "00:00:00.100");
        builder.UseSetting("Kafka:BootstrapServers", stack.Kafka.GetBootstrapAddress());
        builder.UseSetting("Kafka:Topic", StackFixture.Topic);
        builder.UseSetting("Resilience:AttemptTimeout", "00:00:00.500");
        builder.UseSetting("Resilience:TotalTimeout", "00:00:02");
        builder.UseSetting("RateLimiting:PermitsPerSecond", "1000");
        builder.UseSetting("RateLimiting:Burst", "1000");
        foreach (var provider in new[] { "heron", "osprey", "wren" })
        {
            builder.UseSetting($"Providers:{provider}:Faults:LatencyMs", "0");
        }

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public HttpClient CreateMerchantClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", MerchantKey);
        return client;
    }
}

internal static class Http
{
    public static object CardDeposit(long amount = 2_500) => new
    {
        amount,
        currency = "EUR",
        method = "card",
        country = "DE",
        customerId = "player-42",
        paymentToken = "tok_visa_4242",
    };

    public static object BankTransfer(long amount = 50_000) => new
    {
        amount,
        currency = "SEK",
        method = "bank_transfer",
        country = "SE",
        customerId = "player-7",
    };

    public static async Task<HttpResponseMessage> PostIdempotentAsync(this HttpClient client, string url, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    public static string Str(this JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
        }

        return current?.ToString() ?? throw new KeyNotFoundException(path);
    }
}
