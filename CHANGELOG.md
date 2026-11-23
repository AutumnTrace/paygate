# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- CI runs the Docker image on its own (SQLite, as in the README's prebuilt-image option) next to the compose stack, and the release workflow pulls the published image anonymously and makes a test deposit against it

## [0.1.0] - 2026-11-23

First release.

### Added

**Payments API**

- REST API under `/v1` on ASP.NET Core minimal APIs: create deposit, create payout, get and list payments (keyset pagination), full and partial refunds, providers with live circuit state (#2)
- `Idempotency-Key` on every money-moving `POST`: fingerprint of method, path and body; stored responses replayed with `Idempotent-Replayed: true`; `422` for a reused key with a different body; `409` while the first request is in flight; uniqueness per merchant enforced by the database, expiry and a cleanup job (#2)
- API-key authentication per operator (SHA-256 at rest, constant-time comparison), merchant and admin policies, tenant isolation; token-bucket rate limiting per API client with `Retry-After` (#2)
- RFC 9457 problem details with stable `code` values and a `traceId`; .NET 10 built-in request validation; OpenAPI document and Scalar API reference (#2)

**Routing and resilience**

- `IPaymentProvider` port and three simulated providers (card acquirer, local bank transfer and domestic cards, crypto) with fault injection: latency, error rate and outage modes, changeable at runtime through admin endpoints (#1, #2)
- Rule-based routing: first matching rule by kind, method, currency, country and amount band; priorities for failover order and weights for traffic splits; validated at start-up (#1)
- One Polly v8 pipeline per provider through `Microsoft.Extensions.Resilience`: total timeout, retry with jitter for idempotent providers only, circuit breaker, attempt timeout; health-aware failover; manual isolate and close (#1, #2)
- Business declines are final and never failed over; a payout that times out stays `Pending` instead of being sent through another provider (#1)

**Webhooks**

- Signed provider webhooks at `POST /v1/webhooks/{provider}`: HMAC-SHA256 over timestamp and raw body, 5-minute replay window, constant-time comparison, several secrets per provider for rotation (#3)
- Exactly-once processing (event id inbox committed with the payment change), out-of-order handling through the state machine, and a possible-double-charge flag when a provider reports on a payment another provider owns (#3)

**Domain and data**

- `Payment` aggregate with an explicit state machine, `Money` and `Currency` value objects (ISO-4217 minor units), domain events
- EF Core persistence on SQLite (default) or PostgreSQL with snake-case naming, optimistic concurrency on payments, and indexes for merchant listings, provider references and key expiry (#2)
- Transactional outbox: domain events stored with the change that raised them and published in order by a background dispatcher, at least once, with the request's trace context (#4)
- Kafka publisher (idempotent producer, `acks=all`, CloudEvents binary headers, keyed by payment id) on topic `payments.v1`; in-memory publisher and `GET /v1/admin/events` when Kafka is not configured (#4)

**Operations**

- OpenTelemetry traces, metrics and logs; OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, Azure Monitor when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set; Serilog; `/health/live` and `/health/ready` with database and Kafka checks (#2, #4)
- Metrics for payments by provider and status, routing failovers, circuit state, provider latency, webhooks and idempotency outcomes (#1, #4)
- Multi-stage Dockerfile on a chiseled, non-root runtime image; `docker compose` stack with PostgreSQL 17, Kafka 4.3 (KRaft) and the Aspire dashboard (#4)
- CI: build, unit and API tests with coverage summary, Testcontainers integration tests, Docker build and a full-stack compose smoke test; CodeQL; release workflow publishing the image to GHCR (#4)
- Architecture decision records, BenchmarkDotNet benchmarks and the full README (#5)

**Tests**

- Unit tests for the domain, routing, failover on the real Polly pipelines and webhook signatures, including FsCheck property tests
- API tests with `WebApplicationFactory`, including concurrent idempotent requests, live failover and webhooks
- Integration tests with Testcontainers on PostgreSQL and Kafka

[Unreleased]: https://github.com/AutumnTrace/paygate/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/AutumnTrace/paygate/releases/tag/v0.1.0
