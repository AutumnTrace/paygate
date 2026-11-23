# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `Payment` aggregate with an explicit state machine, `Money` / `Currency` value objects (ISO-4217 minor units) and domain events
- `IPaymentProvider` port and three simulated PSPs (card acquirer, local bank transfer, crypto) with fault injection: latency, error rate and outage modes
- Rule-based routing: first matching rule by kind, method, currency, country and amount band; priorities for failover and weights for traffic splits
- Per-provider Polly v8 pipelines (total timeout, retry with jitter for idempotent providers only, circuit breaker, attempt timeout) and health-aware failover
- Payout timeouts are left `Pending` instead of failing over, so a payout can never be sent twice
- Metrics: payments by provider/status, routing failovers, circuit state, provider latency histogram
- REST API under `/v1` (ASP.NET Core minimal APIs): create deposit, create payout, get and list payments (keyset pagination), refunds, providers with live circuit state
- `Idempotency-Key` on every money-moving POST: fingerprint of method, path and body; replay with `Idempotent-Replayed: true`; `422` for a reused key with a different body; `409` while the first request is in flight; database-enforced uniqueness per merchant, expiry and a cleanup job
- API-key authentication per operator (SHA-256 at rest, constant-time comparison), merchant and admin policies, tenant isolation
- Token-bucket rate limiting per API client, `429` with `Retry-After`
- RFC 9457 problem details with stable `code` values; .NET 10 built-in request validation
- Signed provider webhooks at `POST /v1/webhooks/{provider}`: HMAC-SHA256 over timestamp and raw body, 5-minute replay window, constant-time comparison, secret rotation with multiple active secrets
- Exactly-once webhook processing (event id inbox committed with the payment change) and out-of-order handling through the state machine; a provider reporting on a payment it does not own is flagged as a possible double charge
- Admin endpoints for fault injection and manual circuit isolation
- EF Core persistence: SQLite by default, PostgreSQL by configuration; optimistic concurrency on payments; indexes for merchant listings, provider references and key expiry
- OpenAPI document and Scalar API reference, `/health/live` and `/health/ready`, Serilog request logging
- API tests with `WebApplicationFactory`: idempotency semantics (including real concurrent duplicates), refunds, failover, auth, rate limiting, health and OpenAPI
- Domain, routing and failover unit tests, including FsCheck property tests
- CI workflow: build, unit tests, coverage summary
