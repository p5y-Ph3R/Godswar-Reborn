# Infrastructure boundary

Infrastructure code implements application contracts for concrete providers.
PostgreSQL adapters may use Npgsql and domain/application types, but must not
send packets, mutate ECS state, or depend on game handlers.

The current `State` directory is a frozen legacy mixture of models, rules,
PostgreSQL, JSON, and migrations. New adapters belong here; existing code
moves incrementally behind feature-specific contracts. Provider lifetime and
composition remain owned by `Program.cs`.

B08 adds `PostgresApplicationDataRuntime`, which owns the shared Npgsql pool
for extracted character-snapshot, talent-command, and outbox paths. A talent
upgrade commits its authoritative rank/point mutation, immutable audit and
inbox result, and versioned outbox event in one transaction. The dispatcher
leases one event immediately before invoking a consumer, performs callbacks
outside database transactions, advances durable per-aggregate positions only
after success, and retains failed work for bounded retry or poison handling.
Disabling the dispatcher stops delivery without bypassing the authoritative
transaction or deleting retained inbox/outbox rows.
