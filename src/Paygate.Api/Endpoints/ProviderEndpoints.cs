using Microsoft.AspNetCore.Http.HttpResults;
using Paygate.Api.Auth;
using Paygate.Api.Contracts;
using Paygate.Api.Infrastructure;
using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Infrastructure.Providers;
using Polly.Registry;

namespace Paygate.Api.Endpoints;

public static class ProviderEndpoints
{
    public static RouteGroupBuilder MapProviderEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);

        v1.MapGet("/providers", List)
            .WithTags("Providers")
            .RequireAuthorization()
            .WithName("ListProviders")
            .WithSummary("Providers with live circuit state and health")
            .WithDescription("Circuit state comes straight from each provider's Polly circuit breaker; health is a rolling window of the last 50 calls.");

        var admin = v1.MapGroup("/admin/providers/{name}")
            .WithTags("Admin")
            .RequireAuthorization(ApiKeyDefaults.AdminPolicy);

        admin.MapPut("/faults", UpdateFaults)
            .WithName("UpdateProviderFaults")
            .WithSummary("Inject faults into a simulated provider")
            .WithDescription("Set latency, error rate or an outage (errors or timeouts) to watch circuits open and traffic fail over.");

        admin.MapPost("/circuit/isolate", IsolateAsync)
            .WithName("IsolateProvider")
            .WithSummary("Take a provider out of rotation (force its circuit open)");

        admin.MapPost("/circuit/close", CloseAsync)
            .WithName("CloseProviderCircuit")
            .WithSummary("Put a provider back into rotation (close its circuit)");

        return v1;
    }

    private static Ok<IReadOnlyList<ProviderStatusResponse>> List(
        ProviderCatalog catalog, ProviderHealthRegistry health, ProviderFaultRegistry faults) =>
        TypedResults.Ok<IReadOnlyList<ProviderStatusResponse>>(catalog.All
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => ProviderStatusResponse.From(p, health.For(p.Name).Snapshot(), faults.Get(p.Name)))
            .ToList());

    private static Results<Ok<ProviderStatusResponse>, ProblemHttpResult> UpdateFaults(
        string name, UpdateFaultsRequest request, ProviderCatalog catalog, ProviderHealthRegistry health, ProviderFaultRegistry faults)
    {
        if (!catalog.TryGet(name, out var provider))
        {
            return Problems.NotFound($"Provider '{name}'");
        }

        var current = faults.Get(provider.Name);
        var updated = current with
        {
            LatencyMs = request.LatencyMs ?? current.LatencyMs,
            ErrorRate = request.ErrorRate ?? current.ErrorRate,
            Outage = request.Outage ?? current.Outage,
        };

        try
        {
            faults.Set(provider.Name, updated);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Problems.Result(StatusCodes.Status422UnprocessableEntity, "invalid_faults", "latencyMs must be within 0 to 60000 and errorRate within 0 to 1.");
        }

        return TypedResults.Ok(ProviderStatusResponse.From(provider, health.For(provider.Name).Snapshot(), updated));
    }

    private static Task<Results<Ok<ProviderStatusResponse>, ProblemHttpResult>> IsolateAsync(
        string name, ProviderCatalog catalog, ProviderHealthRegistry health, ProviderFaultRegistry faults, ResiliencePipelineProvider<string> pipelines) =>
        ChangeCircuitAsync(name, catalog, health, faults, pipelines, isolate: true);

    private static Task<Results<Ok<ProviderStatusResponse>, ProblemHttpResult>> CloseAsync(
        string name, ProviderCatalog catalog, ProviderHealthRegistry health, ProviderFaultRegistry faults, ResiliencePipelineProvider<string> pipelines) =>
        ChangeCircuitAsync(name, catalog, health, faults, pipelines, isolate: false);

    private static async Task<Results<Ok<ProviderStatusResponse>, ProblemHttpResult>> ChangeCircuitAsync(
        string name,
        ProviderCatalog catalog,
        ProviderHealthRegistry health,
        ProviderFaultRegistry faults,
        ResiliencePipelineProvider<string> pipelines,
        bool isolate)
    {
        if (!catalog.TryGet(name, out var provider))
        {
            return Problems.NotFound($"Provider '{name}'");
        }

        _ = pipelines.GetPipeline(provider.Name); // Make sure the breaker exists before steering it.
        var control = health.For(provider.Name).CircuitControl;
        if (isolate)
        {
            await control.IsolateAsync();
        }
        else
        {
            await control.CloseAsync();
        }

        return TypedResults.Ok(ProviderStatusResponse.From(provider, health.For(provider.Name).Snapshot(), faults.Get(provider.Name)));
    }
}
