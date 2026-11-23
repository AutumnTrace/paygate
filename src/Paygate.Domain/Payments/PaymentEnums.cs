namespace Paygate.Domain.Payments;

/// <summary>Direction of the money movement, seen from the player.</summary>
public enum PaymentKind
{
    /// <summary>The player pays in.</summary>
    Deposit,

    /// <summary>The operator pays the player out (a withdrawal).</summary>
    Payout,
}

public enum PaymentMethod
{
    Card,
    BankTransfer,
    CryptoWallet,
}

public enum PaymentStatus
{
    /// <summary>Accepted by the gateway, not yet sent to a provider.</summary>
    Created,

    /// <summary>A provider accepted the request; the final outcome arrives later by webhook.</summary>
    Pending,

    /// <summary>Funds are reserved but not yet captured.</summary>
    Authorized,

    /// <summary>Funds moved. For a payout this means the money was sent.</summary>
    Captured,

    PartiallyRefunded,
    Refunded,
    Failed,
    Expired,
}

/// <summary>What happened on one provider call during routing.</summary>
public enum AttemptOutcome
{
    /// <summary>The provider accepted the payment (captured, authorized or pending).</summary>
    Accepted,

    /// <summary>The provider gave a business decline, for example insufficient funds. Final, no failover.</summary>
    Declined,

    /// <summary>Technical failure (error response, exhausted retries). The router fails over.</summary>
    Failed,

    /// <summary>The provider did not answer in time. The router fails over.</summary>
    TimedOut,

    /// <summary>The provider's circuit breaker was open, so the gateway did not call it.</summary>
    SkippedCircuitOpen,
}
