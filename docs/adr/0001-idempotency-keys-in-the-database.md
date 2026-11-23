# 1. Idempotency keys are stored in the main database

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Payment APIs are called over unreliable networks. An operator's backend that sends `POST /v1/payments` and gets a timeout cannot tell whether the deposit was created. If it retries blindly, the player can be charged twice; if it gives up, a real deposit can go missing. The client needs a way to retry safely.

The guarantee has to hold when:

- the same request is retried after the first one finished (replay the original answer);
- the retry arrives while the first one is still running (do not run it twice);
- a buggy client reuses a key for a different request (do not silently return the wrong answer);
- the gateway runs as several instances behind a load balancer.

## Decision

Every money-moving `POST` requires an `Idempotency-Key` header (1 to 255 visible ASCII characters). The `IdempotencyMiddleware` and `EfIdempotencyStore` implement it:

1. **Fingerprint** the request: SHA-256 over method, path and the exact body bytes.
2. **Claim** the key by inserting a row into `idempotency_keys` with primary key `(merchant_id, key)`, status `InFlight`, a lock deadline and an expiry. The database's unique constraint decides the race: exactly one concurrent request wins, on any number of instances.
3. A loser reads the winner's row:
   - different fingerprint → `422 idempotency_key_reused`;
   - `Completed` → replay the stored status, `Location`, content type and body with `Idempotent-Replayed: true`;
   - `InFlight` → `409 idempotency_key_in_use` with `Retry-After: 1`;
   - `InFlight` but past its lock deadline (the owner crashed) → take it over with a conditional `UPDATE ... WHERE locked_until = @seen`, so only one waiter wins.
4. When the endpoint finishes, **final** responses (2xx and business 4xx such as `422`) are stored before they are sent, so an immediate retry is a replay, not a `409`. Validation errors (`400`), `409`, `429` and `5xx` **release** the key so a corrected or later request can use it.
5. Keys are scoped per merchant, expire after 24 hours, and a background job deletes expired rows using the index on `expires_at`.

Domain errors are turned into problem responses inside the endpoint (an endpoint filter), not by the global exception handler, so the middleware sees and stores them like any other response.

## Consequences

- Correctness rests on a primary key, which is the strongest and simplest concurrency primitive available. Integration tests fire 10 concurrent duplicates at PostgreSQL and check that exactly one payment and one key row exist.
- Every idempotent request costs one extra insert and one update. That is small next to a PSP round trip.
- The stored response is the full body (a few hundred bytes per payment), kept for 24 hours.
- The key protects the HTTP request. Calls to providers have their own idempotency keys (see [ADR 3](0003-retry-only-idempotent-psp-calls.md)); refunds derive the provider key from the client's key, so a client retry after a `503` cannot refund twice.

## Alternatives considered

- **Redis with `SET NX`.** Fast and common, but adds infrastructure and splits the truth: the payment commits in PostgreSQL while the key lives in Redis, so a crash between the two leaves them disagreeing. Keeping both in one database avoids that.
- **In-process locks / `ConcurrentDictionary`.** Wrong as soon as there are two instances.
- **Deduplicating on a business field** (for example the operator's transaction id). Useful as an extra check, but it does not give replay of the original response, and not every endpoint has such a field.
- **Fingerprinting the parsed request instead of raw bytes.** More forgiving of whitespace changes, but slower and tied to the DTO shape. Clients are expected to resend the same bytes when they retry.
