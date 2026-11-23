using Paygate.Application.Providers;
using Paygate.Domain.Payments;

namespace Paygate.UnitTests.Routing;

/// <summary>A provider whose every call follows a script, so failover scenarios are deterministic.</summary>
internal sealed class ScriptedProvider(string name, bool idempotent = true, bool payouts = true) : IPaymentProvider
{
    private int _calls;

    public string Name { get; } = name;

    public string DisplayName => Name;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Methods = new HashSet<PaymentMethod> { PaymentMethod.Card, PaymentMethod.BankTransfer },
        Currencies = new HashSet<string> { "EUR", "SEK" },
        SupportsPayouts = payouts,
        SupportsRefunds = true,
        SupportsIdempotencyKeys = idempotent,
    };

    /// <summary>Behaviour for call number N (1-based). Defaults to capturing.</summary>
    public Func<int, CancellationToken, Task<ProviderResponse>> Behaviour { get; set; } = (_, _) => Capture();

    public int Calls => Volatile.Read(ref _calls);

    public List<string> IdempotencyKeys { get; } = [];

    public Task<ProviderResponse> ProcessAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        lock (IdempotencyKeys)
        {
            IdempotencyKeys.Add(request.IdempotencyKey);
        }

        return Behaviour(Interlocked.Increment(ref _calls), cancellationToken);
    }

    public Task<ProviderRefundResponse> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderRefundResponse($"{Name}_rf_1"));

    public static Task<ProviderResponse> Capture() =>
        Task.FromResult(new ProviderResponse(ProviderResponseStatus.Captured, "ref_" + Guid.NewGuid().ToString("N")[..8]));

    public static Task<ProviderResponse> Decline(string code) => Task.FromResult(ProviderResponse.Declined(code));

    public static Task<ProviderResponse> Unavailable(string provider) =>
        Task.FromException<ProviderResponse>(new ProviderUnavailableException(provider, "503 Service Unavailable"));

    public static async Task<ProviderResponse> Hang(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}
