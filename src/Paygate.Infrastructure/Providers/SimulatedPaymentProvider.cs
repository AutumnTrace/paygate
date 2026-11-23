using System.Collections.Concurrent;
using Paygate.Application.Providers;
using Paygate.Domain.Payments;

namespace Paygate.Infrastructure.Providers;

/// <summary>
/// A stand-in PSP with configurable latency, error rate and outages. It behaves like a real one in
/// the ways that matter for orchestration:
/// <list type="bullet">
/// <item>it deduplicates by idempotency key when <see cref="ProviderCapabilities.SupportsIdempotencyKeys"/> is on,
/// so a retried call returns the first answer instead of charging twice;</item>
/// <item>asynchronous methods (bank transfer, crypto) answer Pending and confirm later by webhook;</item>
/// <item>amounts ending in <c>51</c> minor units are declined, like a test card that always fails.</item>
/// </list>
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider
{
    /// <summary>Amounts whose last two minor-unit digits equal this are declined (e.g. 25.51 EUR).</summary>
    public const int DeclineSuffix = 51;

    private const int MaxRememberedKeys = 50_000;

    private readonly SimulatedProviderOptions _options;
    private readonly ProviderFaultRegistry _faults;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, object> _processed = new(StringComparer.Ordinal);

    public SimulatedPaymentProvider(string name, SimulatedProviderOptions options, ProviderFaultRegistry faults, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        Name = name;
        _options = options;
        _faults = faults;
        _time = time;
        DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? name : options.DisplayName;
        Capabilities = new ProviderCapabilities
        {
            Methods = options.Methods.ToHashSet(),
            Currencies = options.Currencies.ToHashSet(StringComparer.Ordinal),
            Countries = options.Countries.ToHashSet(StringComparer.Ordinal),
            SupportsPayouts = options.SupportsPayouts,
            SupportsRefunds = options.SupportsRefunds,
            SupportsIdempotencyKeys = options.SupportsIdempotencyKeys,
        };
    }

    public string Name { get; }

    public string DisplayName { get; }

    public ProviderCapabilities Capabilities { get; }

    /// <summary>How many requests reached the simulated PSP's processing step (after network faults).</summary>
    public int ProcessedCount => _processed.Count;

    public async Task<ProviderResponse> ProcessAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SimulateNetworkAsync(cancellationToken).ConfigureAwait(false);

        if (Capabilities.SupportsIdempotencyKeys && _processed.TryGetValue(request.IdempotencyKey, out var previous))
        {
            return (ProviderResponse)previous;
        }

        ProviderResponse response;
        if (request.Amount.MinorUnits % 100 == DeclineSuffix)
        {
            response = ProviderResponse.Declined(request.Kind == PaymentKind.Deposit ? "insufficient_funds" : "beneficiary_account_closed");
        }
        else
        {
            var status = _options.AsyncMethods.Contains(request.Method) ? ProviderResponseStatus.Pending : ProviderResponseStatus.Captured;
            response = new ProviderResponse(status, NewReference());
        }

        return (ProviderResponse)Remember(request.IdempotencyKey, response);
    }

    public async Task<ProviderRefundResponse> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Capabilities.SupportsRefunds)
        {
            throw new NotSupportedException($"{Name} does not support refunds.");
        }

        await SimulateNetworkAsync(cancellationToken).ConfigureAwait(false);

        if (Capabilities.SupportsIdempotencyKeys && _processed.TryGetValue(request.IdempotencyKey, out var previous))
        {
            return (ProviderRefundResponse)previous;
        }

        return (ProviderRefundResponse)Remember(request.IdempotencyKey, new ProviderRefundResponse(NewReference("rf")));
    }

    private async Task SimulateNetworkAsync(CancellationToken cancellationToken)
    {
        var faults = _faults.Get(Name);

        if (faults.LatencyMs > 0)
        {
            var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
            await Task.Delay(TimeSpan.FromMilliseconds(faults.LatencyMs * jitter), _time, cancellationToken).ConfigureAwait(false);
        }

        switch (faults.Outage)
        {
            case OutageMode.Timeouts:
                // Hang like an overloaded PSP until the gateway's timeout cancels the call.
                await Task.Delay(Timeout.InfiniteTimeSpan, _time, cancellationToken).ConfigureAwait(false);
                break;
            case OutageMode.Errors:
                throw new ProviderUnavailableException(Name, "503 Service Unavailable (simulated outage)");
        }

        if (faults.ErrorRate > 0 && Random.Shared.NextDouble() < faults.ErrorRate)
        {
            throw new ProviderUnavailableException(Name, "502 Bad Gateway (simulated error rate)");
        }
    }

    private object Remember(string idempotencyKey, object response)
    {
        if (_processed.Count > MaxRememberedKeys)
        {
            _processed.Clear();
        }

        return _processed.GetOrAdd(idempotencyKey, response);
    }

    private string NewReference(string? infix = null) =>
        infix is null
            ? $"{_options.ReferencePrefix}_{Guid.NewGuid():N}"[..(_options.ReferencePrefix.Length + 17)]
            : $"{_options.ReferencePrefix}_{infix}_{Guid.NewGuid():N}"[..(_options.ReferencePrefix.Length + infix.Length + 18)];
}
