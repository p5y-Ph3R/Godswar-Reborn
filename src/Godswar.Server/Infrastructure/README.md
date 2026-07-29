# Infrastructure boundary

Infrastructure code implements application contracts for concrete providers.
PostgreSQL adapters may use Npgsql and domain/application types, but must not
send packets, mutate ECS state, or depend on game handlers.

The current `State` directory is a frozen legacy mixture of models, rules,
PostgreSQL, JSON, and migrations. New adapters belong here; existing code
moves incrementally behind feature-specific contracts. Provider lifetime and
composition remain owned by `Program.cs`.
