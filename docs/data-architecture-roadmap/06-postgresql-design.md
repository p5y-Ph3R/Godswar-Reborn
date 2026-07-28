# 6. PostgreSQL design

## 6.1 Responsibilities

PostgreSQL owns all durable player identity, ownership, value, progression, permanent world changes, security/economy audit, command deduplication, and outbox state. It also remains suitable for versioned relational content plus targeted JSONB metadata.

Use schemas to clarify ownership without an immediate physical split:

- `auth`: credentials/account security if later separated from legacy `public.accounts`;
- `game`: authoritative player state;
- `content`: versioned templates/definitions;
- `ops`: inbox/outbox/audit/reconciliation metadata;
- `research`: protocol-capture corpus after it is separated from runtime queries;
- `legacy`: retired read-only archives.

Changing schema names is not the first task; ownership and contracts come first.

For content, separate authoring authority from runtime-serving authority:

- **Requires clarification:** choose one source-controlled authoring input per content family (generated source/resources or an approved authoring database/tool). Captured packets are evidence, not silently authoritative content.
- A build/publish step validates that input and produces one immutable `content_release` manifest.
- PostgreSQL is the runtime-serving projection for the selected release where relational querying is needed; packaged immutable resources may serve a content family only if that family does not also read mutable PG copies.
- Every server pins one release ID. Exactly one representation owns a given runtime field for that release; all other copies are generated projections with checksums.

## 6.2 Existing and proposed aggregates

| Area/status | High-level rows and keys | Constraints/indexes | Concurrency/transaction/audit | Expected queries and delete behavior |
| --- | --- | --- | --- | --- |
| Accounts - Existing, harden | Current `accounts(id PK, username, password/status/VIP...)`; target explicit `normalized_username` and versioned verifier | Current exact-case username uniqueness; target unique normalized identity, verifier-format check, and bounded status enums | Credential CAS/version; security audit for verifier/admin/status changes | Current lookup is exact-case in PG. Target lookup uses normalized identity. RESTRICT destructive account deletion; use a lifecycle/tombstone before controlled purge |
| Characters - Existing, evolve | `character_base(id PK, account_id FK, name, class/camp, checkpoint/progression...)` initially; later split only where invariants justify | Current global exact-case name uniqueness/account FK and no account-slot or broad numeric checks; target normalized name + account slot/cardinality, level/HP/MP/map checks, and indexes by account/name | Target `lifecycle_version`, facet versions, owner fence; existing create is one transaction; add deletion request/audit | Login list by account, load by ID. Replace immediate hard cascade with requested deletion, restore window, then audited purge |
| Inventory/equipment - Existing | `character_items(id bigint PK, user_id FK, prop_id FK, item_location, slot_index, stack, quality, grade, attributes...)` | Current unique `(user_id,item_location,slot_index)`, `item_location IN (0,1,2)`, template FK, and character/location indexes; target add slot-range, stack, quality, grade, and attribute/socket checks | Target `inventory_version` on owner aggregate or item revisions; retain current row locks and add one operation inbox/audit/outbox | Full bag/equipment load and slot mutation. CASCADE only during approved character purge; catalog deletion RESTRICT |
| Currency - Existing fields, target ledger | Current Money/Stone balance plus target `currency_ledger(operation_id, character_id, currency, delta, balance_after, reason, created_at)` | Current balances lack a complete nonnegative/ledger constraint model; target unique operation ID, currency enum, nonnegative balance check, and character/time index | Target balance update + immutable ledger + inbox/outbox atomically with deterministic lock order | Point balance read, support/audit history. Ledger never cascades silently; retain snapshots if character purged |
| Progression - Existing | level/EXP/talent/zodiac rows and modifier tables | Current keys/indexes vary and broad nonnegative/source-uniqueness checks are absent; target add nonnegative numeric checks, one logical character/grid/modifier source, and active-expiry indexes | Current row locks where implemented; target expected version, reward source/event ID, and audit exceptional grants | Login aggregate and point updates; retain audit |
| Skills/talents - Existing | composite character/skill or character/talent PK | Current composite PK/FKs to content; no general database rank checks; target bounded rank/level checks | Target upgrade plus points/currency/inbox in one transaction | Load all by character; cascade only with controlled character purge |
| Pets - Partially implemented in dirty tree | `pet_templates`, `character_pets`, stat/bonus/skill children, `pet_operation_audit` | owner FKs; pet revision; unique carried/summoned partial indexes; level 1-120 checks | Hatch/presence/level transaction. Propagate client operation ID rather than generate it inside store | Load owned pets; command by pet/owner. Controlled delete/audit; template RESTRICT |
| Boosts/world control - Existing | character modifiers, world-boss areas, faction-area control | Current keys/indexes/death-token behavior as defined by existing tables; target prove/enforce one logical modifier source/control interval and query-driven expiry/map indexes | Existing idempotent kill token where implemented; target monotonic online watermark and complete audit | Active-by-character/map/time query; archive expired audit as policy requires |
| Content - Existing | item, attribute, skill, talent, NPC, map, link, monster, pet templates; JSONB metadata where appropriate | Current stable PKs/FKs vary by catalog; target add a content revision and only demonstrated JSONB expression/GIN indexes | Target publish one immutable content revision; do not let rolling versions race startup upserts | Lookup by ID/map; preload immutable revision. Retain at least previous compatible release |
| Audit/inbox/outbox - Missing/generalize | `command_inbox(id, principal_id, aggregate_type/id, command_family, operation_id, request_hash, result, token_expires_at, committed_at)`, `outbox(id, aggregate_type/id, version, event_type, payload, attempts, available_at)`, feature audits | Unique `(principal_id, aggregate_type, aggregate_id, command_family, operation_id)`; ledgers reference inbox `id`; outbox ready/aggregate indexes; payload size checks | Written in same transaction as authoritative change | Inbox lookup on retry; version-aware outbox polling; token validity and inbox retention aligned |
| Quests - Missing | Do not create until gameplay contract exists; likely character quest header/objectives/reward claims | Future unique character/quest/version and objective FKs | Reward claim atomically with progression/inventory and inbox | Future only |
| Guilds/trades/mail/auctions/entitlements - Missing | Do not create speculative schemas. Use separate PG aggregates when feature semantics are approved | Relational constraints and query-driven indexes | Trade/auction/purchase value commits atomically with ledger/audit/outbox | Future only; no repository code currently supports them |
| Research captures - Existing but separate concern | capture session/transactions/templates | Session/time/opcode indexes; partition/retention if volume warrants | Append-only research ingestion, never in player transaction | Runtime should consume reviewed content releases, not depend on indefinite capture history |

## 6.3 Constraints and query rules

- Prefer database constraints for invariants that must survive every code path: nonnegative balances/stacks/EXP, valid enum ranges, unique slot occupancy, one active pet role, and valid catalog references.
- Use checked C# arithmetic and PostgreSQL checks together.
- Use `timestamptz` in UTC and server-calculated times.
- Do not use account/character names as foreign keys.
- Index actual predicates: character ownership, normalized username/name, active expiry, outbox readiness, and audit operation ID. Do not duplicate unique-constraint indexes.
- Use JSONB only for bounded metadata whose subfields do not require frequent relational joins/constraints. Add GIN or expression indexes only after a real query appears.
- Keep audit snapshots independent enough to remain understandable after mutable rows are removed.

## 6.4 Access technology

Continue using **Npgsql directly** for migrations and hot/transactional command paths. The repository already relies on precise SQL, `FOR UPDATE`, advisory locks, and transaction sequencing, and it has no EF Core convention to preserve. Adding EF Core now would introduce a second migration/change-tracking model and obscure ECS/application ownership.

Dapper is optional for repetitive read-only projections after contracts are split, but it is not required for the first phases. If introduced, it must share the configured `NpgsqlDataSource` and never own migrations.

`NpgsqlDataSource` should be registered once per process with validated connection/pool/timeouts, instrumented, and injected only into PostgreSQL infrastructure adapters. Add explicit command/lock timeouts by workload. Pool exhaustion must make readiness/overload visible rather than block the map tick.

## 6.5 Migration release safety

The connected local development PostgreSQL history was observed at 23 migrations through `20260729_022_pet_level_progression`, while Git HEAD tracks only through `20260728_012_pet_aptitude_catalog`; the later migrations are currently uncommitted files. Because `PostgresSchemaMigrationPlan` correctly rejects an ahead-of-binary database, HEAD alone cannot reproduce or safely roll back against that database.

Before another schema change:

1. freeze migration IDs already applied;
2. commit/tag the complete corresponding code and migration catalog as one coherent release;
3. capture a schema/history/build manifest and verified backup;
4. repair the missing packet-metadata baseline dependency, then prove empty-database bootstrap and representative-backup forward migration;
5. make CI fail if integration tests skip;
6. remove the historical filesystem init mount or formally make it invoke the same embedded migration contract-never two independent histories.

Run migrations as a release step/one-shot job before making new instances ready. Keep the runner callable from the application for controlled local development, but production startup should verify schema compatibility rather than repeatedly seed/migrate without an explicit deployment gate.

Keep one-transaction migrations as the default, but add a separately declared online/resumable migration step type for production-sized operations that cannot or should not run in that transaction:

- `CREATE INDEX CONCURRENTLY`;
- large keyset-paginated backfills with durable batch checkpoints;
- `ADD CONSTRAINT ... NOT VALID` followed by later `VALIDATE CONSTRAINT`;
- long retention/archive moves.

These steps require explicit metadata, pre/post conditions, resumability, progress/lock/WAL metrics, cancellation behavior, and a reconciliation gate. They must never be hidden inside ordinary application startup or treated as completed before validation.
