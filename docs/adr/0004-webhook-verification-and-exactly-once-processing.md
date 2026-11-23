# 4. Webhook verification and exactly-once processing

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Bank transfers and crypto deposits complete asynchronously: the provider answers `Pending` and reports the final outcome later by webhook. The webhook endpoint is public and moves payment state, so it is a target: a forged `payment.captured` would credit a player who never paid. Webhooks are also delivered at least once, sometimes late, and not always in order.

## Decision

**Authenticate by signature, not by network or API key.** Providers send:

```
Webhook-Timestamp: 1763900000
Webhook-Signature: v1=<hex HMAC-SHA256(secret, "{timestamp}.{raw body}")>
```

`WebhookVerifier`:

- signs the **timestamp together with the raw body bytes**, so an old body cannot be replayed under a fresh timestamp, and the body is never re-serialized before checking;
- rejects deliveries more than **5 minutes** from server time in either direction (replay window);
- compares with `CryptographicOperations.FixedTimeEquals`, over **every** presented signature and **every** active secret, without stopping at the first match, so timing reveals nothing;
- accepts **several secrets per provider** and several `v1=` values per header, so secrets rotate without downtime;
- takes the **event id from the signed body**, not from a header, so it cannot be swapped to slip a replay past deduplication.

**Process each event exactly once.** The event id goes into `webhook_events` (primary key `(provider, event_id)`) in the **same transaction** as the payment change. A redelivery finds the row and returns `duplicate`; a concurrent redelivery loses the insert race (or the payment's optimistic concurrency check) and gets `duplicate` or a retryable `409`. A crash can never apply an event without remembering it, or remember it without applying it.

**Let the state machine handle ordering.** `Payment.ApplyProviderUpdate` applies only legal forward moves. Anything else is acknowledged with `200` and ignored (`stale`), because rejecting it would make the provider redeliver it forever, and payments never move backwards.

**Answer so the provider does the right thing:** `404` for an unknown payment and `409 payment_not_ready` (with `Retry-After`) while the gateway's own call to that provider is still being recorded, because acknowledging those would lose the update; `200` for everything that has been handled, including stale, duplicate and ignored event types.

## Consequences

- A forged or replayed webhook cannot change a payment; the tests tamper with bodies, signatures and timestamps to prove it.
- `provider_mismatch` (a provider reporting on a payment another provider owns, typically after a timeout and failover) is logged at error level as a possible double charge for an operator to reverse.
- An out-of-order event that arrives *before* its predecessor (for example `captured` before the gateway recorded `pending`) is handled by the `payment_not_ready` retry; events that are simply superseded are dropped, by design.
- The scheme is easy to reproduce with `openssl dgst -sha256 -hmac`, which the README uses.

## Alternatives considered

- **IP allow-lists.** Brittle with cloud egress ranges and no protection against a compromised network path. Fine as an extra layer, not as the only one.
- **Mutual TLS.** Strong, but operationally heavy and not offered by most PSPs.
- **Asymmetric signatures (Ed25519).** Better when many parties verify; with one shared secret per provider, HMAC is standard and simpler.
- **Strict ordering with a per-payment sequence number.** Requires every provider to send one; the state machine gets the same safety without it.
