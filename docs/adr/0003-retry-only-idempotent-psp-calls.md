# 3. Retry a provider only when the call is idempotent

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Retries are the cheapest way to ride out a dropped connection or a brief PSP hiccup, and the most dangerous thing to do to a payment. A retried authorization that the PSP had in fact processed is a double charge; a retried payout is money sent twice. Whether a retry is safe depends on whether the PSP deduplicates requests, which varies by provider and by endpoint.

## Decision

Each provider has one Polly v8 pipeline, registered through `Microsoft.Extensions.Resilience` (`AddResiliencePipelines`), built in this order:

```
total timeout (4 s) -> retry (2x, exponential + jitter) -> circuit breaker -> attempt timeout (1 s) -> PSP
```

- **Retries run only when the call is idempotent.** The invoker sets a `paygate.is-idempotent` property on the resilience context from the provider's `SupportsIdempotencyKeys` capability, and the retry strategy's `ShouldHandle` checks it. Every request carries a stable provider idempotency key (`pg_<payment id>`; refunds use a key derived from the client's `Idempotency-Key`), so a retry reaches the PSP as the same operation. Providers that cannot deduplicate get exactly one attempt.
- **Only technical failures are handled**: `ProviderUnavailableException` (5xx, connection errors) and `TimeoutRejectedException`. Business declines are return values, so they are never retried and never count against the circuit breaker.
- **The circuit breaker sits inside the retry**, so every attempt counts towards opening it, and an open circuit stops further retries at once.
- **Jitter** (`UseJitter = true`) spreads retries so many gateway instances do not hammer a recovering PSP in lockstep.
- If the circuit opens during our own retries, the attempt is still reported as `timed_out` or `failed`, because the provider was called and may have received the request. Only a call that never left the gateway is `skipped_circuit_open`. This distinction drives the payout rule in [ADR 2](0002-rule-based-routing-with-health-aware-failover.md).

The default numbers (1 s per attempt, 4 s total, breaker opens at 50% failures over at least 3 calls in 30 s, stays open 30 s) favour fast failover for a player waiting on a deposit button. They are configuration, validated at start-up.

## Consequences

- Retries help exactly where they are safe and are switched off where they are not, without per-call-site decisions.
- The simulated crypto provider (`wren`) has no idempotency keys, so a transient error there fails the attempt immediately. That is visible in the tests and in `GET /v1/providers` (`supportsIdempotencyKeys: false`).
- Polly emits its own metrics (`resilience.polly.strategy.events`) next to paygate's, so retries, breaks and timeouts are visible per provider.

## Alternatives considered

- **Retry everything a few times.** Simple and common, and the classic cause of duplicate charges.
- **Never retry, rely on failover.** Safe, but moves traffic away from a healthy PSP because of one dropped packet, and fails over payouts that should not move.
- **Hedging (parallel requests to two providers).** Lowers tail latency for reads; for money movement it guarantees duplicates.
