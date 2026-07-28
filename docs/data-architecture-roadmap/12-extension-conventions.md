# 12. Extension conventions for new features

## 12.1 Recommended module layout

Keep files below the repository maintainability threshold and organize by feature and layer, for example:

```text
src/Godswar.Server/
  Application/
    Inventory/
      Commands/
      Queries/
      IInventoryTransactions.cs
  Domain/
    Inventory/
      InventoryRules.cs
      InventoryResults.cs
  Infrastructure/
    Postgres/
      Inventory/
        PostgresInventoryTransactions.cs
        InventorySql.cs
    Redis/
      Session/
  World/
    Components/
    Systems/
    Boundaries/
  Networking/
    Protocol/
    Sessions/
```

The repository may reach that layout incrementally; do not move everything in one rewrite. Generated catalogs should be chunked or moved to embedded versioned resources rather than letting hand-maintained files exceed 20 KB. The current `SecureUdpSessionAuthority` is already split across responsibility-specific partial files and remains below the repository threshold; preserve that pattern as it evolves.

## 12.2 Registration conventions

Each feature module should expose an explicit composition method or descriptor registered from `Program.cs` (or a future Generic Host composition root) containing:

- application handlers;
- persistence contracts and their PostgreSQL implementation;
- optional Redis adapter behind an explicit capability flag;
- ECS components/systems and stable ordering;
- codecs/command handlers;
- health checks and low-cardinality metrics.

Unrelated ECS systems depend only on domain events/contracts. They do not know whether a feature uses PostgreSQL, Redis, or no persistence.

## 12.3 Persistent component mappings

Maintain a small registry/document for each persisted facet:

- durable aggregate and field owner;
- load DTO version;
- hydrator/dehydrator;
- current database columns/tables;
- derived fields excluded from persistence;
- concurrency version/fence;
- compatible previous version;
- migration/backfill test.

This is not automatic component serialization. Mappings are explicit application/infrastructure code.

## 12.4 PostgreSQL conventions

- Migration ID: retain `YYYYMMDD_NNN_lowercase_name`.
- Once applied, SQL and checksum are immutable; add a new forward migration.
- Every migration has plan/checksum tests, empty-schema integration, representative-old-schema integration, and reconciliation queries.
- A feature's SQL stays in its infrastructure module; parameterized commands only.
- Transactions are use-case-specific and expose committed domain results, not `NpgsqlTransaction`.
- Define command/lock timeout, relevant indexes, explain plan for hot queries, and backup/rollback impact.

## 12.5 Redis conventions

If/when Redis is approved:

- central typed key builders and value versioning;
- documented TTL, refresh, invalidation, reconstruction, owner, outage behavior, and maximum cardinality for every key;
- no raw user strings in keys;
- Lua scripts versioned/tested beside the owning module;
- local fallback semantics tested;
- health distinguishes cache degradation from ownership-coordination failure.

## 12.6 MongoDB conventions

No collection may be added without the section 8 ADR. If approved, it must define collection owner, `_id`, `schemaVersion`, validation, compound indexes, maximum document size, migration/backfill, retention, backup/restore, consistency boundary, and behavior when MongoDB is unavailable. No gameplay aggregate may be dual-written to PG and Mongo.

## 12.7 Serialization, commands, idempotency, and observability

- Persistence DTOs and wire DTOs are separate and explicitly versioned.
- Binary protocol decoders bound every length/count/string and return typed validation failures.
- Each valuable command declares idempotency scope, request hash, inbox retention, transaction, committed response, and retry behavior.
- Each feature declares metric names/finite dimensions, structured log event IDs/redaction, trace spans, readiness dependencies, and queue limits.
- Tests must include integration, concurrent duplicate, crash point, recovery/reconciliation, and old/new schema compatibility.
- Health checks distinguish live, ready, degraded, and draining; they never expose secrets or player identifiers.

## 12.8 Avoid a universal repository

Do not introduce `IRepository<T>`, a generic component store, or one abstraction pretending SQL, Redis, and documents have identical semantics. Useful contracts express intent:

- `TryCommitInventoryCommandAsync`;
- `LoadCharacterSnapshotAsync`;
- `TryAcquirePlayerOwnershipAsync`;
- `ReadWorldContentRevisionAsync`.

The contract must make transaction and consistency behavior visible.
