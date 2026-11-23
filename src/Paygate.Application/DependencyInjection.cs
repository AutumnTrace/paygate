using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paygate.Application.Idempotency;
using Paygate.Application.Observability;
using Paygate.Application.Payments;
using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Application.Routing;
using Polly;

namespace Paygate.Application;

public static class DependencyInjection
{
    /// <summary>Everything in the application layer: routing, payment use cases and idempotency settings.</summary>
    public static IServiceCollection AddPaygateApplication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddPaygateRouting(configuration);
        services.AddScoped<PaymentService>();

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .Validate(o => o.RetentionPeriod >= TimeSpan.FromMinutes(1), "Idempotency: RetentionPeriod must be at least one minute.")
            .Validate(o => o.InFlightLockTimeout > TimeSpan.Zero, "Idempotency: InFlightLockTimeout must be positive.")
            .Validate(o => o.MaxRequestBodyBytes is > 0 and <= 1_048_576, "Idempotency: MaxRequestBodyBytes must be within 1 byte and 1 MiB.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Routing, failover and one resilience pipeline per registered <see cref="IPaymentProvider"/>.
    /// Providers themselves are registered by the infrastructure layer.
    /// </summary>
    public static IServiceCollection AddPaygateRouting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        services.AddOptions<RoutingOptions>()
            .Bind(configuration.GetSection(RoutingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RoutingOptions>, RoutingOptionsValidator>();

        services.AddOptions<ProviderResilienceOptions>()
            .Bind(configuration.GetSection(ProviderResilienceOptions.SectionName))
            .Validate(o => o.AttemptTimeout > TimeSpan.Zero && o.TotalTimeout >= o.AttemptTimeout, "Resilience: AttemptTimeout must be positive and not exceed TotalTimeout.")
            .Validate(o => o.FailureRatio is > 0 and <= 1, "Resilience: FailureRatio must be in (0, 1].")
            .Validate(o => o.MinimumThroughput >= 2, "Resilience: MinimumThroughput must be at least 2.")
            .ValidateOnStart();

        services.AddSingleton<ProviderCatalog>();
        services.AddSingleton<ProviderHealthRegistry>();
        services.AddSingleton<PaygateMetrics>();
        services.AddSingleton<IRoutingEngine>(sp => new RuleBasedRoutingEngine(
            sp.GetRequiredService<IOptionsMonitor<RoutingOptions>>(),
            sp.GetRequiredService<ProviderCatalog>(),
            Random.Shared));
        services.AddSingleton<ProviderInvoker>();
        services.AddSingleton<PaymentRouter>();

        // One named pipeline per provider, so each has its own circuit breaker.
        services.AddResiliencePipelines<string>(context =>
        {
            var catalog = context.ServiceProvider.GetRequiredService<ProviderCatalog>();
            foreach (var name in catalog.Names)
            {
                context.AddResiliencePipeline(name, (builder, pipelineContext) =>
                {
                    var sp = pipelineContext.ServiceProvider;
                    builder.TimeProvider = sp.GetRequiredService<TimeProvider>();
                    ProviderPipeline.Configure(
                        builder,
                        sp.GetRequiredService<IOptions<ProviderResilienceOptions>>().Value,
                        sp.GetRequiredService<ProviderHealthRegistry>().For(name),
                        sp.GetRequiredService<PaygateMetrics>(),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Paygate.Resilience"));
                });
            }
        });

        return services;
    }
}
