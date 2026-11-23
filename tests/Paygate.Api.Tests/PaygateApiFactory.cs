using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Paygate.Api.Tests;

/// <summary>
/// Boots the real API (all middleware, Polly pipelines, simulated providers) against a throw-away
/// SQLite file, with provider latency removed and short timeouts so failover tests run fast.
/// </summary>
public class PaygateApiFactory : WebApplicationFactory<Program>
{
    // Demo keys from appsettings.Development.json. Only their SHA-256 hashes are in configuration.
    public const string MerchantKey = "pgk_test_demo_7c2f9a41e8b35d06";
    public const string OtherMerchantKey = "pgk_test_other_3b8e1d72c4a9f650";
    public const string AdminKey = "pgk_admin_demo_91d4e6f2a7c83b05";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"paygate-tests-{Guid.NewGuid():N}.db");

    /// <summary>Extra configuration, applied after the defaults below. Set before the first client is created.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Paygate", $"Data Source={_databasePath};Pooling=False");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Resilience:AttemptTimeout", "00:00:00.200");
        builder.UseSetting("Resilience:TotalTimeout", "00:00:01");
        builder.UseSetting("Resilience:RetryBaseDelay", "00:00:00.001");
        builder.UseSetting("Providers:heron:Faults:LatencyMs", "0");
        builder.UseSetting("Providers:osprey:Faults:LatencyMs", "0");
        builder.UseSetting("Providers:wren:Faults:LatencyMs", "0");
        builder.UseSetting("RateLimiting:PermitsPerSecond", "1000");
        builder.UseSetting("RateLimiting:Burst", "1000");
        builder.UseSetting("Events:PollInterval", "00:00:00.050");

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public HttpClient CreateClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}

internal static class HttpTestExtensions
{
    public static object CardDeposit(long amount = 2_500, string country = "DE", string currency = "EUR") => new
    {
        amount,
        currency,
        method = "card",
        country,
        customerId = "player-42",
        paymentToken = "tok_visa_4242",
    };

    public static async Task<HttpResponseMessage> PostIdempotentAsync(this HttpClient client, string url, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text) ?? throw new JsonException($"Empty body (status {(int)response.StatusCode}).");
    }

    public static async Task<JsonNode> ReadSuccessJsonAsync(this HttpResponseMessage response, System.Net.HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    public static string Str(this JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var segment in path.Split('.'))
        {
            current = int.TryParse(segment, out var index) ? current?[index] : current?[segment];
        }

        return current?.GetValue<object>()?.ToString() ?? throw new KeyNotFoundException(path);
    }
}
