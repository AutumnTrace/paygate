using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Paygate.Api.Infrastructure;

public static class HealthChecks
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// <c>/health/live</c>: the process is up (no dependencies checked, so a database blip never restarts pods).
    /// <c>/health/ready</c>: dependencies are reachable and the instance can take traffic.
    /// </summary>
    public static WebApplication MapPaygateHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync });
        return app;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                status = report.Status.ToString(),
                durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    error = e.Value.Exception?.Message,
                }),
            },
            cancellationToken: context.RequestAborted);
    }
}
