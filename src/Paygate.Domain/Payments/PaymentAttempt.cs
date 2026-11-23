namespace Paygate.Domain.Payments;

/// <summary>One provider call made while routing a payment. Kept for audit and returned by the API.</summary>
public sealed class PaymentAttempt
{
    private PaymentAttempt()
    {
        Provider = string.Empty;
    }

    internal PaymentAttempt(int sequence, string provider, AttemptOutcome outcome, TimeSpan duration, string? error, DateTimeOffset at)
    {
        Id = Guid.CreateVersion7();
        Sequence = sequence;
        Provider = provider;
        Outcome = outcome;
        DurationMs = (long)duration.TotalMilliseconds;
        Error = error;
        At = at;
    }

    public Guid Id { get; private set; }

    public int Sequence { get; private set; }

    public string Provider { get; private set; }

    public AttemptOutcome Outcome { get; private set; }

    public long DurationMs { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset At { get; private set; }
}
