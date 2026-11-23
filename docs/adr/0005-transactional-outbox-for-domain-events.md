# 5. Domain events leave through a transactional outbox

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Other services react to payments: the ledger books money movements, risk updates player velocity, reconciliation matches settlements. They learn about payments from events on Kafka. Publishing directly after `SaveChanges` has two failure modes: the process dies between commit and publish (the event is lost and the ledger never hears about a captured deposit), or the publish succeeds and the commit fails (an event describes something that never happened). Kafka being down must also never stop players from paying.

## Decision

- The `Payment` aggregate records domain events (`payment.created.v1`, `payment.status_changed.v1`, `payment.refunded.v1`) as it changes.
- `PaygateDbContext.SaveChangesAsync` moves them into `outbox_messages` **in the same transaction** as the change, together with the W3C `traceparent` of the current request.
- `OutboxDispatcher`, a background service, reads unpublished rows in insertion order, publishes them through `IEventPublisher`, and marks them published. Failures are counted per row and retried on the next tick; published rows are purged after a retention period.
- `KafkaEventPublisher` uses an idempotent producer with `acks=all`, keys every message by payment id (so one payment's events stay in order within a partition), and follows the CloudEvents Kafka binding in binary mode (`ce_id`, `ce_type`, `ce_source`, `ce_time` headers, JSON value). Without Kafka configured, an in-memory publisher logs the events and serves them at `GET /v1/admin/events`, so `dotnet run` needs nothing else.

## Consequences

- No lost events and no phantom events. Delivery is **at least once**: a crash after publishing but before marking republishes the batch, so consumers deduplicate on `ce_id`. The idempotent producer removes duplicates caused by broker-side retries.
- Events are delayed by up to one poll interval (1 s by default). A Kafka outage grows the outbox; payments carry on.
- The dispatcher assumes one active instance. Running several needs row claiming (`FOR UPDATE SKIP LOCKED` on PostgreSQL) or leader election; this is on the roadmap.
- Consumers can join the payment's trace from the `traceparent` header.

## Alternatives considered

- **Publish after commit, best effort.** Loses events on crashes; unacceptable for money.
- **Distributed transaction (database + broker).** Kafka does not take part in XA transactions, and the coupling would make Kafka availability a payment dependency.
- **Change data capture (Debezium) on the outbox or payments table.** Removes the polling dispatcher and scales well; adds Kafka Connect to operate. A good next step once there are several services, and the outbox table is already the right shape for it.
