# 13. Migration strategy

The strategy is incremental, forward-only, and compatible with incomplete gameplay. Temporary coexistence is allowed only behind explicit adapters and comparison gates. Permanent dual writes are not.

## Phase 0 - Re-establish a coherent release baseline

- **Goal:** make source, applied migrations, database backup, and build identity reproducible before any new data change.
- **Scope/tasks:** inventory the 23 observed migration records; freeze IDs/checksums; commit/tag all already-applied pet migration/code work together; capture schema/history/row-count manifest; verify a restorable backup; repair/reconcile Docker's historical init mount with the embedded runner; add an immediate fail-closed profile guard so raw `LoginOrCreateAccountAsync` and username-only game binding can run only in an explicitly named local-development profile.
- **Likely files/modules:** `State/DatabaseMigrations`, `PostgresSchemaMigrationCatalog*`, `PostgresSchemaMigrationPlan`, `ServerOptions`, listener/login composition, `docker-compose.yml`, `docs/database-migrations.md`, CI/tooling. Preserve unrelated gameplay changes.
- **Dependencies:** repository owner confirms the dirty pet work is intended; access to a disposable restored DB.
- **Data migrations:** none new. This phase records and verifies already-applied migrations.
- **Acceptance criteria:** clean checkout can build and migrate an empty PG 17 database and the representative backup to the exact expected history; current database is not ahead of the tagged binary; production-like profiles cannot start the raw account-creating/username-only authentication path.
- **Tests:** checksum/ordered-prefix/ahead-of-binary, empty bootstrap, restored-backup rehearsal, data invariants.
- **Metrics:** migration duration/outcome/history version; reconciliation mismatch count.
- **Rollback:** restore the verified pre-phase backup and run the prior matching binary; do not delete migration history.
- **Risks:** accidentally treating uncommitted WIP as disposable; using a production-like DB as a test target.
- **Complexity:** Medium.

## Phase 1 - Data ownership and command durability decisions

- **Goal:** make one authoritative owner and durability class explicit for every implemented field/command.
- **Scope/tasks:** adopt section 4 as an ADR; catalog packet commands and classify runtime/checkpoint/transactional behavior; record reward failure semantics, deletion policy, reconnect guarantee, account character-slot cardinality, legacy operation-ID compatibility, and final raw TCP retirement policy.
- **Likely files/modules:** `docs`, `Protocol/Opcodes.cs`, `GameClientHandler*.cs`, `World/Components`, `State` DTOs.
- **Dependencies:** Phase 0 baseline.
- **Data migrations:** none.
- **Acceptance criteria:** every implemented mutating opcode has an owner, transaction/ack rule, idempotency need, and failure result.
- **Tests:** architecture/document consistency check against registered opcode handlers.
- **Metrics:** coverage count of classified versus unclassified commands.
- **Rollback:** documentation-only revert.
- **Risks:** silently assuming semantics for incomplete features; mark unresolved behavior instead.
- **Complexity:** Small.

## Phase 2 - Application and persistence boundaries

- **Goal:** remove direct transport/ECS dependency on the monolithic `IGameStore` without changing gameplay behavior.
- **Scope/tasks:** introduce typed command/query handlers and feature contracts; first extract read-only `IWorldContentReader` and `ICharacterSnapshotReader`, then inventory/progression/pet transactions; keep adapters delegating to existing store during transition; add architecture tests forbidding Npgsql/Redis references from networking/domain/ECS.
- **Likely files/modules:** new `Application`/`Infrastructure` folders; `Program.cs`; `IGameStore.cs`; the current `_store`-using `Game` files.
- **Dependencies:** Phase 1 command classification.
- **Data migrations:** none.
- **Acceptance criteria:** selected handlers call application use cases; protocol bytes and database results remain golden-vector compatible; no ECS component holds a persistence client.
- **Tests:** handler parity, contract fakes, architecture dependency checks.
- **Metrics:** application-command latency/outcomes; unhandled/uncategorized command count.
- **Rollback:** composition flag/delegating legacy adapter; no schema change.
- **Risks:** a large interface split becoming a large rewrite. Migrate one vertical slice at a time.
- **Complexity:** Large overall, Small/Medium per slice.

## Phase 3 - PostgreSQL platform hardening

- **Goal:** make PostgreSQL an observable, tested, fail-closed production dependency.
- **Scope/tasks:** validated provider allowlist; one configured `NpgsqlDataSource`; command/lock/pool limits; separate production migration command/job; transactional steps by default plus explicit resumable online steps for concurrent indexes/batched backfills/deferred validation; readiness checks; mandatory disposable PG 17 CI; fail-on-skip; remove ambiguous JSON fallback in production profiles.
- **Likely files/modules:** `ServerOptions`, `Program.cs`, `PostgresGameStore`, migration runner, Docker/CI, tests.
- **Dependencies:** Phase 0; contract extraction can proceed in parallel.
- **Data migrations:** metadata/build compatibility table if approved.
- **Acceptance criteria:** invalid provider/config fails startup; migrations are an explicit gate; resumable steps expose durable progress and validation; PG outage flips readiness; CI always exercises migrations and repositories.
- **Tests:** Testcontainers or equivalent PG container, pool exhaustion, timeouts, interrupted transactional and resumable steps, concurrent-index/deferred-constraint rehearsal, schema ahead/behind/compatible additive suffix, restored backup.
- **Metrics:** pool use/wait, query and transaction latency, retries/timeouts, migration status, readiness reason.
- **Rollback:** retain application-start migration for local profile; deploy previous config/binary against compatible schema.
- **Risks:** environment-specific connection/pool defaults; migration job and app racing without advisory-lock policy.
- **Complexity:** Medium.

## Phase 4 - First low-risk PostgreSQL aggregate

- **Goal:** prove new boundaries with read-heavy world content before player-value mutation.
- **Scope/tasks:** implement `IWorldContentReader`, pin a content revision, load map/NPC/monster/item definitions, and stop runtime fallback reads from arbitrary packet-capture history.
- **Likely files/modules:** `PostgresGameStore.WorldSync.cs`, seed/catalog files, `GameSessionRegistry.NpcCatalog.cs`, map/monster initialization, new content infrastructure module.
- **Dependencies:** Phases 2-3.
- **Data migrations:** content-release/version metadata and indexes only if required.
- **Acceptance criteria:** identical NPC/monster/map bootstrap for captured fixtures; one server instance pins one compatible content revision.
- **Tests:** catalog golden vectors, old/new content compatibility, missing-content fail readiness, query plans.
- **Metrics:** content-load latency, cache hit, revision, fallback/missing count.
- **Rollback:** switch composition to the existing content reader; retain old columns/tables.
- **Risks:** captured database contains content not reproducible from source. Inventory and reconcile before cutover.
- **Complexity:** Medium.

## Phase 5 - Character load snapshot and lifecycle

- **Goal:** load one consistent character aggregate and make create/delete retry-safe.
- **Scope/tasks:** confirm characters-per-account and client slot semantics; add `CharacterLoadSnapshot`; use a short consistent PG read transaction; include inventory, stats inputs, skills/talents/zodiac/boosts/pets; add account-slot/cardinality constraint, lifecycle version, and command operation ID; replace hard immediate delete with tombstone/restore/purge policy.
- **Likely files/modules:** `PostgresGameStore.Characters*`, `GameClientHandler.LoginWorldEntry.cs`, hydrators, character schema/migrations.
- **Dependencies:** Phases 2-3; pet schema baseline from Phase 0.
- **Data migrations:** lifecycle/version/deletion columns, inbox rows; backfill defaults.
- **Acceptance criteria:** login never combines incompatible row versions; create cannot exceed the approved account slots; duplicate create/delete returns the original result; deleted character follows approved recovery policy.
- **Tests:** concurrent login/create/delete, crash after commit before ACK, old/new binary load compatibility.
- **Metrics:** character-load component/query latency, conflicts, duplicate command count, load failures by finite reason.
- **Rollback:** keep expanded columns; route reads through legacy adapter while no contract-only writes exist.
- **Risks:** long read transaction, wide legacy row, client preview timing.
- **Complexity:** Large.

## Phase 6 - Checkpoint persistence and ownership generations

- **Goal:** make position/vitals/online-time saves monotonic, bounded, and crash-defined.
- **Scope/tasks:** define checkpoint revisions; remove unbounded per-character semaphores; use bounded off-tick workers; add ownership generation/fence to conditional writes; supervise critical loops; document safe-spawn and lost-tail policy.
- **Likely files/modules:** `CharacterPositionPersistenceCoordinator`, realtime persistence files, `PostgresGameStore.Progression.cs`, `GameSessionRegistry.BackgroundLoops.cs`, `Program.cs`.
- **Dependencies:** Phases 2-3 and lifecycle ownership decision.
- **Data migrations:** checkpoint/owner revision columns and constraints.
- **Acceptance criteria:** stale save cannot overwrite newer map/vitals; every synchronous transfer checkpoint reports exactly one applied row or an explicit conflict/not-found result before transfer continues; queue sizes are bounded; critical-loop fault marks not-ready and terminates/drains; crash loss is within declared bound.
- **Tests:** delayed/reordered save completions, zero-row/wrong-account position update, queue capacity one, process crash, reconnect, two simulated owners.
- **Metrics:** dirty age, queue depth/coalesces/drops, checkpoint latency/conflicts, loop heartbeat/fault.
- **Rollback:** retain columns; switch to old coordinator only for single instance and older write format.
- **Risks:** disconnect latency and false ownership conflicts.
- **Complexity:** Large.

## Phase 7 - Transactional inventory and economy safety

- **Goal:** make existing valuable commands replay-safe and auditable.
- **Scope/tasks:** add command inbox, currency/item operation ledger, transactional outbox; propagate operation IDs; cover move/equip/consume/grant/forge/enhance/mentor/holy stone and GM grants; enforce balance/item checks.
- **Likely files/modules:** `PostgresGameStore.Inventory*`, `PostgresGameStore.Crafting.cs`, Gear Mentor/enhancement files, handlers, new operations schema.
- **Dependencies:** Phases 1-3; ownership fence from Phase 6 before multi-instance.
- **Data migrations:** inbox, outbox, currency/item audit/ledger, aggregate versions/check constraints.
- **Acceptance criteria:** duplicate/reordered retry cannot duplicate or lose item/currency; all successful mutations have audit and outbox; same operation ID/different request is rejected.
- **Tests:** concurrency races, crash before/after commit/ACK/outbox dispatch, arithmetic boundaries, reconciliation.
- **Metrics:** commit/conflict/duplicate/failure counts, ledger mismatch, outbox backlog/age.
- **Rollback:** old handler may read new state, but once operation IDs/ledgers become authoritative, rollback binary must understand them; maintain a compatibility release.
- **Risks:** retrofit of client operation IDs; economic correctness is high risk.
- **Complexity:** Large.

## Phase 8 - Progression, zodiac, boosts, rewards, and pets

- **Goal:** apply the same durable-command model to remaining implemented player value.
- **Scope/tasks:** deterministic monster-death/reward ID; progression/zodiac/boost interval inboxes; pet command IDs and revisions; commit-first result projection; close the "monster died but reward save failed" gap.
- **Likely files/modules:** `PostgresGameStore.Progression.cs`, zodiac/skills files, `GameClientHandler.Progression.cs`, combat kill projection, pet files/migrations.
- **Dependencies:** inbox/outbox foundation and coherent pet release.
- **Data migrations:** reward/interval audit/inbox fields, pet audit key correction, constraints.
- **Acceptance criteria:** each death grants at most once; online duration never consumes offline/overlapping time; pet retry returns same result.
- **Tests:** duplicate kill, server crash at each boundary, clock interval overlap, pet concurrency/reconnect, reconciliation.
- **Metrics:** reward duplicate/conflict/failure, interval overlap rejected, pet revision conflicts, outbox lag.
- **Rollback:** feature flags per application handler; preserve additive schema.
- **Risks:** client protocol may not supply IDs; derive safe server event IDs where necessary.
- **Complexity:** Large.
