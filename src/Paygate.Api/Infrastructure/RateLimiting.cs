using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Paygate.Api.Auth;

namespace Paygate.Api.Infrastructure;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Sustained requests per second per API key.</summary>
    public int PermitsPerSecond { get; set; } = 20;

    /// <summary>Short bursts allowed on top of the sustained rate (token bucket size).</summary>
    public int Burst { get; set; } = 40;

    /// <summary>Webhook deliveries per second per provider and source address.</summary>
    public int WebhookPermitsPerSecond { get; set; } = 100;
}

public static class RateLimitPolicies
{
    public const string PerClient = "per-client";
    public const string Webhooks = "webhooks";

    /// <summary>
    /// Token bucket per API client: one noisy operator cannot starve the others. Partitioned after
    /// authentication, so the key is the client id, never the raw API key.
    /// </summary>
    public static IServiceCollection AddPaygateRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(PerClient, http => RateLimitPartition.GetTokenBucketLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? $"ip:{http.Connection.RemoteIpAddress}",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, options.Burst),
                    TokensPerPeriod = Math.Max(1, options.PermitsPerSecond),
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            limiter.AddPolicy(Webhooks, http => RateLimitPartition.GetFixedWindowLimiter(
                $"{http.GetRouteValue("provider")}:{http.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.WebhookPermitsPerSecond),
                    Window = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                }));

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 1;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = Problems.Title(StatusCodes.Status429TooManyRequests),
                        Detail = $"Rate limit exceeded. Retry after {retryAfter} s.",
                        Extensions = { ["code"] = "rate_limited" },
                    },
                }).ConfigureAwait(false);
            };
        });

        return services;
    }
}
