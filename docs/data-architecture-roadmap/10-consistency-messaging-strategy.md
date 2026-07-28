# 10. Consistency and messaging strategy

## 10.1 Recommended patterns

| Pattern | Repository application | Required behavior |
| --- | --- | --- |
| Transactional inbox | Forge/enhance/mentor, pet hatch/level/presence, talent/zodiac upgrade, monster reward, character create/delete, future trade/auction/purchase | Unique scoped operation ID and request hash; store result in same PG transaction; same ID/different payload is a security conflict |
| Transactional outbox | Character/inventory/progression/pet changes that require cache invalidation, audit export, leaderboard update, or cross-process notification | Insert with authoritative mutation; at-least-once delivery; consumer dedupe; immediate live result does not wait for or originate from outbox |
| Optimistic concurrency | Character lifecycle/facets, inventory version, progression version, pet revision, ownership fence | Conditional update; zero rows is an explicit conflict; never last-write-wins silently |
| Row locks | Short multi-row inventory/currency/pet/future trade transactions | Deterministic lock order, command/lock timeout, no network calls while transaction is open |
| At-least-once delivery | Outbox to projections/notifications | Idempotent consumer keyed by outbox event ID and aggregate version |
| Cache-aside | Character summaries and immutable content after Redis gate | Read cache, miss PG, versioned set; outbox invalidates. Never cache-aside an unversioned balance for mutation |
| Event-driven invalidation | Equipment/stat summaries, presence-dependent displays, leaderboards | Outbox after PG commit; tolerate duplicate/out-of-order events with aggregate version |
| Materialized projections | Existing equipment/stat views; future leaderboard/read models | Derived and rebuildable; report build version/watermark |
| Reconciliation jobs | Balance versus ledger, item uniqueness, inbox/outbox gaps, cache version, migration/backfill parity | Bounded batches, read-only/report mode first, idempotent repair with audit and approval for destructive changes |

## 10.2 No cross-database dual writes

Do not implement:

```text
UPDATE PostgreSQL;
SET Redis;
return success;
```

as two independent writes. Commit PostgreSQL state plus outbox once. An asynchronous consumer updates Redis. If it crashes, the outbox retries; if Redis is missing, PostgreSQL remains correct.

Likewise, do not write the same authoritative document to PostgreSQL and MongoDB. A future document projection must be explicitly derived, versioned, and rebuildable.

Multiple `SKIP LOCKED` workers can observe aggregate version 2 before version 1. Every outbox event therefore carries aggregate ID/version and declares one of two policies:

- versioned-state consumers apply the newest version and ignore stale/duplicate events; or
- order-sensitive consumers are partitioned/serialized by aggregate and advance only the next expected version, with gap retry/reconciliation.

Global ordering is neither promised nor required.

## 10.3 Command and event identity

- The unchanged legacy client does not currently provide a proven stable operation ID for inventory, forge, character lifecycle, pet, or progression commands. Durable cross-reconnect idempotency is therefore an explicit compatibility gate, not an already available property.
- Choose and test one solution per valuable command family: extend the secure shim/control envelope with an operation ID retained across retries; add a server-issued operation token that the compatible client/shim can echo; or document that only connection-local suppression is possible. A new server GUID per receipt and a time-window hash of command contents do **not** distinguish a retry from two legitimate identical actions.
- Inbox uniqueness is scoped to stable authenticated principal/aggregate + command family + operation ID, not session generation, so a retry after reconnect deduplicates.
- Client/shim-generated operation IDs are acceptable only as opaque uniqueness tokens; authorization and semantics still come from the bound server principal and validated command.
- The server derives a request hash from canonical validated semantics, not raw bytes.
- Server-originated monster reward events need an identity that cannot repeat after restart. Allocate a non-repeating server-boot/map-runtime instance ID (preferably from PG or another durable monotonic/random-uniqueness contract), combine it with monster runtime/spawn ordinal and death revision, and retain the resulting death ID unchanged across every reward retry. Spawn generation alone can reset and is insufficient.
- Current pet store methods generate audit GUIDs internally; propagate the command operation ID instead so audit uniqueness becomes actual retry safety.
- Ledger/audit rows reference the canonical inbox row/identity rather than assuming client operation IDs are globally unique.
- Give retry tokens an explicit expiry. Retain the inbox result for at least token lifetime plus clock/reconnect grace; after inbox purge the token is invalid and must be rejected, never executed as new. Retain high-value ledger/audit evidence according to its longer policy.

## 10.4 Reconciliation

Run scheduled, bounded checks for:

- duplicate slot occupancy and orphaned item/template references;
- wallet balance versus ledger;
- progression grants versus reward inbox;
- pet presence uniqueness and revision monotonicity;
- stale account/presence projections;
- outbox backlog and poison messages;
- cache/version mismatches after Redis is introduced;
- schema migration history versus build manifest;
- content revision compatibility.

Automatic repairs must be narrowly defined, idempotent, audited, and separated from detection. Economy mismatches should alert and quarantine before destructive correction.
