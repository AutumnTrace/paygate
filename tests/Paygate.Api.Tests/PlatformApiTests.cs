using System.Net;
using System.Text.Json.Nodes;

namespace Paygate.Api.Tests;

/// <summary>Cross-cutting behaviour: authentication, rate limiting, health probes and the OpenAPI document.</summary>
public sealed class PlatformApiTests(PaygateApiFactory factory) : IClassFixture<PaygateApiFactory>
{
    [Fact]
    public async Task Requests_without_an_api_key_get_401_problem_details()
    {
        var response = await factory.CreateClient().GetAsync("/v1/providers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("ApiKey", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("unauthorized", (await response.ReadJsonAsync()).Str("code"));
    }

    [Fact]
    public async Task A_wrong_api_key_is_rejected()
    {
        var response = await factory.CreateClient("pgk_test_demo_7c2f9a41e8b35d07").GetAsync("/v1/providers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_keys_cannot_create_payments()
    {
        var response = await factory.CreateClient(PaygateApiFactory.AdminKey).PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Providers_list_shows_capabilities_and_circuit_state()
    {
        var providers = await factory.CreateClient(PaygateApiFactory.MerchantKey).GetAsync("/v1/providers", TestContext.Current.CancellationToken);

        var list = (await providers.ReadSuccessJsonAsync(HttpStatusCode.OK)).AsArray();
        Assert.Equal(["heron", "osprey", "wren"], list.Select(p => p!.Str("name")));
        Assert.All(list, p => Assert.Equal("closed", p!.Str("circuit.state")));
        Assert.Equal("false", list.Single(p => p!.Str("name") == "wren")!.Str("supportsIdempotencyKeys").ToLowerInvariant());
    }

    [Fact]
    public async Task Each_api_client_has_its_own_token_bucket()
    {
        using var limited = new PaygateApiFactory();
        limited.Settings["RateLimiting:Burst"] = "2";
        limited.Settings["RateLimiting:PermitsPerSecond"] = "1";
        var merchant = limited.CreateClient(PaygateApiFactory.MerchantKey);
        var other = limited.CreateClient(PaygateApiFactory.OtherMerchantKey);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await merchant.GetAsync("/v1/providers", TestContext.Current.CancellationToken)).StatusCode);
        }

        var limitedResponse = await merchant.GetAsync("/v1/providers", TestContext.Current.CancellationToken);
        var otherResponse = await other.GetAsync("/v1/providers", TestContext.Current.CancellationToken);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses.Append(limitedResponse.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
        Assert.NotNull(limitedResponse.Headers.RetryAfter);
        Assert.Equal("rate_limited", (await limitedResponse.ReadJsonAsync()).Str("code"));
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_probes_report_healthy(string path)
    {
        var response = await factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);

        var body = await response.ReadSuccessJsonAsync(HttpStatusCode.OK);
        Assert.Equal("Healthy", body.Str("status"));
    }

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        var body = await (await factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken)).ReadJsonAsync();

        Assert.Equal("database", body.Str("checks.0.name"));
    }

    [Fact]
    public async Task OpenApi_document_describes_auth_and_idempotency()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var document = await response.ReadSuccessJsonAsync(HttpStatusCode.OK);

        Assert.NotNull(document["paths"]!["/v1/payments"]!["post"]);
        Assert.NotNull(document["paths"]!["/v1/payouts"]!["post"]);
        Assert.NotNull(document["paths"]!["/v1/providers"]!["get"]);
        Assert.Equal("X-Api-Key", document.Str("components.securitySchemes.ApiKey.name"));

        var parameters = document["paths"]!["/v1/payments"]!["post"]!["parameters"]!.AsArray();
        Assert.Contains(parameters, p => p!.Str("name") == "Idempotency-Key" && p!.Str("in") == "header");
    }

    [Fact]
    public async Task Scalar_api_reference_is_served_in_development()
    {
        var response = await factory.CreateClient().GetAsync("/scalar/v1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unhandled_routes_return_problem_details()
    {
        var response = await factory.CreateClient(PaygateApiFactory.MerchantKey).GetAsync("/v1/nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsType<JsonObject>(await response.ReadJsonAsync());
    }
}
