using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Paygate.Application.Observability;

namespace Paygate.Api.Infrastructure;

public static class Telemetry
{
    /// <summary>
    /// OpenTelemetry traces, metrics and logs. Exporters are switched on by configuration only:
    /// OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (the Aspire dashboard in docker compose,
    /// or any collector), Azure Monitor when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set.
    /// </summary>
    public static WebApplicationBuilder AddPaygateTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var version = typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("paygate", serviceVersion: version))
            .WithTracing(tracing => tracing
                .AddSource(PaygateTelemetry.Name)
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter(PaygateTelemetry.Name, "Polly")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitor();
        }

        return builder;
    }
}
