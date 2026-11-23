using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Paygate.Application.Events;
using Paygate.Application.Idempotency;
using Paygate.Application.Payments;
using Paygate.Application.Webhooks;
using Paygate.Application.Providers;
using Paygate.Infrastructure.Events;
using Paygate.Infrastructure.Idempotency;
using Paygate.Infrastructure.Persistence;
using Paygate.Infrastructure.Providers;
using Paygate.Infrastructure.Webhooks;

namespace Paygate.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers one simulated PSP per entry under <c>Providers</c> in configuration.</summary>
    public static IServiceCollection AddSimulatedProviders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var providers = configuration.GetSection(SimulatedProviderOptions.SectionName)
            .Get<Dictionary<string, SimulatedProviderOptions>>() ?? [];

        if (providers.Count == 0)
        {
            throw new InvalidOperationException($"No providers are configured under '{SimulatedProviderOptions.SectionName}'.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var registry = new ProviderFaultRegistry();
            foreach (var (name, options) in providers)
            {
                registry.Set(name, options.Faults);
            }

            return registry;
        });

        foreach (var (name, options) in providers)
        {
            services.AddKeyedSingleton(name, options);
            services.AddSingleton<IPaymentProvider>(sp => new SimulatedPaymentProvider(
                name,
                options,
                sp.GetRequiredService<ProviderFaultRegistry>(),
                sp.GetRequiredService<TimeProvider>()));
        }

        return services;
    }

    /// <summary>EF Core persistence on SQLite (default) or PostgreSQL, plus the idempotency store.</summary>
    public static IServiceCollection AddPaygatePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? "Data Source=paygate.db";

        services.AddDbContextFactory<PaygateDbContext>(
            options =>
            {
                _ = database.Provider switch
                {
                    DatabaseProvider.Postgres => options.UseNpgsql(connectionString),
                    _ => options.UseSqlite(connectionString),
                };
                options.UseSnakeCaseNamingConvention();
            },
            ServiceLifetime.Singleton);

        // The business unit of work: one context per request.
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PaygateDbContext>>().CreateDbContext());
        services.AddScoped<IPaymentRepository, EfPaymentRepository>();
        services.AddScoped<IWebhookInbox, EfWebhookInbox>();
        services.AddSingleton<IIdempotencyStore, EfIdempotencyStore>();
        services.AddHostedService<IdempotencyCleanupService>();

        return services;
    }

    /// <summary>
    /// Domain events: transactional outbox plus a dispatcher that publishes to Kafka, or to an
    /// in-memory publisher when Kafka is not configured (so <c>dotnet run</c> needs nothing else).
    /// </summary>
    public static IServiceCollection AddPaygateEvents(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EventsOptions>()
            .Bind(configuration.GetSection(EventsOptions.SectionName))
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.BatchSize is > 0 and <= 1000, "Events: PollInterval must be positive and BatchSize within 1 to 1000.")
            .ValidateOnStart();
        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers) && !string.IsNullOrWhiteSpace(o.Topic), "Kafka: BootstrapServers and Topic are required.")
            .ValidateOnStart();

        var events = configuration.GetSection(EventsOptions.SectionName).Get<EventsOptions>() ?? new EventsOptions();
        if (events.Publisher == EventPublisherKind.Kafka)
        {
            services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        }
        else
        {
            services.AddSingleton<InMemoryEventPublisher>();
            services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventPublisher>());
        }

        services.AddHostedService<OutboxDispatcher>();
        return services;
    }

    /// <summary>Readiness checks for the database and, when enabled, Kafka.</summary>
    public static IHealthChecksBuilder AddPaygateDependencyChecks(this IHealthChecksBuilder builder, IConfiguration configuration, string tag)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.AddDbContextCheck<PaygateDbContext>("database", tags: [tag]);

        var events = configuration.GetSection(EventsOptions.SectionName).Get<EventsOptions>() ?? new EventsOptions();
        if (events.Publisher == EventPublisherKind.Kafka)
        {
            builder.AddCheck<KafkaHealthCheck>("kafka", tags: [tag]);
        }

        return builder;
    }

    /// <summary>Creates the schema if needed. Called once at start-up, before the server accepts traffic.</summary>
    public static async Task InitializePaygateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.GetRequiredService<IOptions<DatabaseOptions>>().Value.CreateSchemaOnStartup)
        {
            return;
        }

        var contexts = services.GetRequiredService<IDbContextFactory<PaygateDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }
}
