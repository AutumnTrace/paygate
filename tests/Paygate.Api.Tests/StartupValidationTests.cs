using Microsoft.Extensions.Options;

namespace Paygate.Api.Tests;

/// <summary>Settings that would break the app once it is running are refused at start-up, naming the setting.</summary>
public sealed class StartupValidationTests
{
    [Theory]
    [InlineData("Idempotency:CleanupInterval", "00:00:00", "Idempotency: CleanupInterval")]
    [InlineData("Idempotency:CleanupInterval", "-00:01:00", "Idempotency: CleanupInterval")]
    [InlineData("Webhooks:MaxBodyBytes", "0", "Webhooks: MaxBodyBytes")]
    [InlineData("Webhooks:MaxBodyBytes", "2097152", "Webhooks: MaxBodyBytes")]
    public void Invalid_settings_stop_the_app_at_start_up(string key, string value, string message)
    {
        using var factory = new PaygateApiFactory();
        factory.Settings[key] = value;

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Assert.Single(Unwrap(error).OfType<OptionsValidationException>());
        Assert.Contains(validation.Failures, f => f.StartsWith(message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_defaults_start()
    {
        using var factory = new PaygateApiFactory();

        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }

                yield break;
            }

            yield return current;
        }
    }
}
