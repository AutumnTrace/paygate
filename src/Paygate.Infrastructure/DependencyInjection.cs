using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paygate.Application.Providers;
using Paygate.Infrastructure.Providers;

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
}
