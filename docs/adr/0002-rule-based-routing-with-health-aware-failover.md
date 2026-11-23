# 2. Rule-based routing with health-aware failover

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

An operator connects several payment service providers (PSPs): card acquirers, local bank-transfer providers, crypto on/off-ramps. Which one should process a payment depends on business rules (method, currency, player country, ticket size, negotiated fees) and on live conditions (a PSP that is timing out should not get traffic). Payments teams change these rules often and need to read them at a glance.

## Decision

**A routing table in configuration, first match wins.** Each rule can filter on kind, method, currency, country and an amount band (inclusive minimum, exclusive maximum, in minor units of the payment's currency), and lists targets with a **priority** and a **weight**:

```json
{ "Name": "nordic-cards", "Methods": ["Card"], "Countries": ["SE", "NO", "DK", "FI"],
  "Targets": [ { "Provider": "heron", "Priority": 1, "Weight": 70 },
               { "Provider": "osprey", "Priority": 1, "Weight": 30 } ] }
```

`RuleBasedRoutingEngine` turns a payment into an ordered candidate list: the first matching rule, priority groups in ascending order, a weighted shuffle within each group (Efraimidis-Spirakis, so the lower-weight provider is still a failover option), then a capability filter so no provider ever sees a payment it cannot process. The table is validated at start-up (unknown providers, inverted bands, zero weights fail fast). The engine is pure and does not look at health, which keeps it cheap and easy to test.

**Health is applied at call time by Polly.** `PaymentRouter` walks the candidates. Each provider has its own resilience pipeline with a circuit breaker; when the circuit is open, Polly rejects the call in microseconds and the router records `skipped_circuit_open` and moves on. When the break duration is over, the next real payment becomes the half-open trial call. There is no separate health pre-check: reading the breaker's state alone would never let the trial call through.

**Fail over on technical failures only:**

| Outcome | Deposit | Payout |
|---|---|---|
| Accepted (captured, authorized, pending) | done | done |
| Business decline (`insufficient_funds`) | **final**, no failover | **final**, no failover |
| Error response / circuit open | next candidate | next candidate |
| Timeout (after retries) | next candidate | **stays `Pending`** with that provider |

A decline would only be declined again elsewhere, and spraying a declined card across acquirers looks like card testing. A payout that timed out may already be on its way to the player, so sending it through a second provider could pay twice; it waits for the provider's webhook or reconciliation instead.

## Consequences

- Rules read top to bottom exactly as they behave. Weights give A/B splits and gradual migrations between providers without code changes.
- A provider outage costs the first affected payments one timeout budget each (four seconds by default), then the circuit opens and traffic moves with no added latency. `GET /v1/providers` and the `paygate.provider.circuit_state` gauge show the state live; operators can also isolate a provider by hand.
- Failing a deposit over after a timeout accepts a small risk: the first provider may have captured late. Its webhook then arrives for a payment another provider owns; the gateway flags it as `provider_mismatch` and logs a possible double charge for reversal. This is the trade-off most orchestrators make for deposits, and the reason payouts behave differently.
- Amount bands compare minor units of the payment's own currency, so they must be paired with a currency filter. There is no FX conversion in routing.

## Alternatives considered

- **Cost- or success-rate-based dynamic scoring.** Better long-term, but opaque and hard to reason about during an incident. The weighted table is a stepping stone: weights could later be fed from measured approval rates.
- **Health in the routing engine** (skip unhealthy providers when planning). Duplicates the breaker's state machine and breaks half-open recovery.
- **Retry the same provider forever instead of failing over.** Keeps one provider's semantics, but turns a provider outage into a gateway outage.
