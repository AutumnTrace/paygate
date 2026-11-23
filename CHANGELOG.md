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
- Domain, routing and failover unit tests, including FsCheck property tests
- CI workflow: build, unit tests, coverage summary
