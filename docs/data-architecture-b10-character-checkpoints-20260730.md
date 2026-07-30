# B10 character checkpoints and bounded persistence workers

Date: 2026-07-30
Status: completed for PostgreSQL-backed position and HP/MP checkpoints
Next roadmap ticket: B11 - character lifecycle and tombstones

## Outcome

B10 replaces the former per-handler position saver and scattered direct
HP/MP writes with one process-wide, bounded checkpoint boundary. Position and
vitals now have independent monotonic revisions, and PostgreSQL stores the
active checkpoint owner plus a monotonic ownership generation on the
authoritative `character_base` row.

Normal movement and routine vitals changes are coalesced off the gameplay
path. Map transfers, revival, mount MP commits, and session finalization use
explicit flush-through barriers. A delayed write from an earlier owner or
revision cannot overwrite a newer PostgreSQL checkpoint.

This is deliberately not the B15 player-ownership fence. B10 protects only
position and vitals checkpoints. Inventory, currency, progression, and other
valuable transactions need the broader ownership lock/fence defined by B15.

## Durable schema and PostgreSQL authority

Forward migration
`20260730_030_character_checkpoint_versions` adds these fields to
`public.character_base`:

- `position_revision bigint NOT NULL DEFAULT 0`;
- `checkpoint_owner_id uuid`; and
- `checkpoint_owner_generation bigint NOT NULL DEFAULT 0`.

It also validates non-negative position and existing vitals revisions,
non-negative owner generations, and the invariant that an active owner has a
non-empty UUID and a positive generation. A released row keeps its generation
but clears `checkpoint_owner_id`, so a later acquisition cannot reset the
fence.

`PostgresCharacterCheckpointStore` implements
`ICharacterCheckpointStore` through one shared `NpgsqlDataSource`:

1. `AcquireAsync` atomically installs the requested owner UUID. Reacquiring
   the same active UUID is idempotent; replacing or reacquiring an inactive
   owner increments the durable generation.
2. Position and vitals writes lock the account-owned character row with
   `SELECT ... FOR UPDATE`.
3. Ownership is checked before revision or payload comparison.
4. A lower stored revision applies with a conditional update that includes
   account, character, owner UUID, owner generation, and prior revision.
   Exactly one affected row is required.
5. An exact replay returns `AlreadyApplied`; an older request returns
   `Superseded`; the same revision with a different payload returns
   `RevisionConflict`; an old owner returns `OwnershipLost`; and an invalid
   account/character pair returns `CharacterNotFound`.
6. `ReleaseAsync` takes the same row lock, validates the exact owner, clears
   only the owner UUID, and requires exactly one affected row.

`CharacterCheckpointWriteResult.Satisfies` treats only `Applied`,
`AlreadyApplied`, or `Superseded` with a stored revision at least as new as
the requested revision as a completed barrier. Cross-transport or delayed
callers therefore cannot infer success from a zero-row update.
`Superseded` proves a newer revision, not payload equality; transfer safety
therefore also relies on the current single-owner, serialized lifecycle.

The character snapshot contract, PostgreSQL snapshot reader, JSON snapshot
reader, and `GameCharacter` now carry `PositionRevision` alongside the
existing `VitalsRevision`. Checkpoint owner UUID and generation are
runtime-only fields and are not serialized as player data.

Primary evidence:

- `src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.CharacterCheckpoints.cs`
- `src/Godswar.Server/Application/Characters/CharacterCheckpointContracts.cs`
- `src/Godswar.Server/Application/Characters/ICharacterCheckpointStore.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterCheckpointStore.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterCheckpointStore.Ownership.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterCheckpointStore.Writes.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterSnapshotReader.Core.cs`
- `src/Godswar.Server/State/GameCharacter.cs`

## Bounded process-wide coordinator

`CharacterCheckpointCoordinator` owns a bounded `Channel` and one in-memory
entry per account, character, and facet. A newer checkpoint for the same key
coalesces over pending work; an older revision is ignored and an equal
revision with different state is rejected. Capacity is checked against the
distinct pending-key dictionary. Queued and retrying keys stay within that
bound, while the fixed worker count separately bounds writes whose old
ownership was invalidated while already in flight.

Ordinary saves use non-blocking `TryEnqueue`. Transfer and lifecycle barriers
use `FlushThroughAsync`. Acquisition, release, and flush-through calls share a
separate bounded semaphore with finite admission and command deadlines.

`IOException`, `TimeoutException`, and `DbException` are retried with bounded
exponential delay and jitter. Retry age is finite. Exhausting it, receiving
`RevisionConflict`, or faulting a worker faults the coordinator, removes
readiness, cancels sibling work, and surfaces through the supervised host task
instead of silently leaving a dead background loop.

An asynchronous `OwnershipLost` or `CharacterNotFound` result is recorded as
a terminal outcome and removes that pending item. It does not fault global
readiness or notify the handler that originally enqueued it. Synchronous
flush-through barriers do return those statuses to their caller.

Shutdown changes the coordinator to `Draining`, completes once queued,
active, retrying, and direct work is empty, and cancels remaining work after
the configured drain deadline. If a storage provider ignores cancellation,
disposal still returns within its two deadlines without zeroing live
accounting; a late completion drains normally and cannot overwrite the
terminal `Disposed` state.

Implementation is split to respect the repository's 20 KB file limit:

- `CharacterCheckpointCoordinator.cs` - lifetime, readiness, supervision,
  heartbeat, and bounded shutdown;
- `CharacterCheckpointCoordinator.Queue.cs` - admission, coalescing, and
  owner invalidation;
- `CharacterCheckpointCoordinator.Worker.cs` - writes, retry policy, and
  fault propagation;
- `CharacterCheckpointCoordinator.Direct.cs` - bounded acquisition,
  flush-through, and release operations; and
- `CharacterCheckpointCoordinator.Models.cs` - finite work-key and pending
  state.

## Runtime lifecycle

`Program.cs` creates exactly one coordinator. PostgreSQL uses
`PostgresCharacterCheckpointStore`; the explicit local JSON profile uses
`LegacyCharacterCheckpointStore`. Startup runs the coordinator, waits for
readiness before accepting game traffic, and includes its task in runtime
supervision.

The handler and registry integration is:

- world entry acquires the owner fence, reloads the character snapshot, and
  requires its position and vitals revisions to match the acquired row before
  publishing the owner to runtime state;
- a reference-counted per-account acquisition gate and exact current-session
  checks serialize competing handler acquisitions; an in-flight stale
  handler releases its acquired owner before the legitimate replacement can
  acquire;
- accepted legacy and secure-realtime movement increments
  `PositionRevision` and shares the process-wide coalescing worker;
- the secure-realtime handler's private position-save channel was removed;
- routine combat, status, recovery, and background HP/MP writes enqueue the
  newest vitals revision;
- map transfer persists the target safe position synchronously before
  staging the scene change and persists the source position if rollback is
  required;
- revival flushes both the new safe position and revived vitals before it
  completes;
- mount activation flushes the MP debit before reporting durable success;
- mutation results pass through `InstallUpdatedCharacter`, preserving live
  position, vitals, revisions, and owner fields when a repository returns a
  fresh character projection, and advancing the vitals revision if a changed
  maximum clamps current HP or MP; and
- disconnect runs bounded position and vitals barriers concurrently, then
  uses a separate bounded deadline to release the exact owner fence.

The obsolete `CharacterPositionPersistenceCoordinator` and the
secure-realtime handler's second persistence task were removed.

Primary lifecycle evidence:

- `src/Godswar.Server/Program.cs`
- `src/Godswar.Server/GameClientHandlerFactory.cs`
- `src/Godswar.Server/Game/GameClientHandler.CharacterCheckpoints.cs`
- `src/Godswar.Server/Game/GameClientHandler.LoginWorldEntry.cs`
- `src/Godswar.Server/Game/GameClientHandler.MapTransitions.cs`
- `src/Godswar.Server/Game/GameClientHandler.RealtimeMovement.cs`
- `src/Godswar.Server/Game/GameClientHandler.MovementCombat.cs`
- `src/Godswar.Server/Game/GameSessionRegistry.AccountSessions.cs`
- `src/Godswar.Server/Game/GameSessionRegistry.CharacterCheckpoints.cs`

## Configuration and observability

Both checked-in profiles explicitly configure:

| Setting | Default |
| --- | ---: |
| Queue capacity | 1,024 distinct keys |
| Worker count | 4 |
| Direct-operation concurrency | 8 |
| Direct admission timeout | 1,000 ms |
| Store command timeout | 5,000 ms |
| Base retry delay | 100 ms |
| Maximum retry delay | 2,000 ms |
| Maximum retry age | 30,000 ms |
| Shutdown drain timeout | 10,000 ms |

`ServerOptions` validates finite bounds and supports corresponding
`GODSWAR_CHECKPOINT_*` environment overrides. Invalid JSON or environment
values fail startup.

`CharacterCheckpointMetrics` publishes low-cardinality enqueue and write
outcomes, retries, write duration, queue depth, active writes, scheduled
retries, readiness, oldest dirty age, and worker heartbeat age through
`Godswar.Server.Application.CharacterCheckpoints`.

## PostgreSQL production path and JSON compatibility

The PostgreSQL adapter is the production authority. Its owner UUID,
generation, revisions, payload, and conditional update are protected by one
database row lock and survive process restart.

`LegacyCharacterCheckpointStore` exists only for the explicit
local-development JSON profile and focused compatibility tests. It offers
the same result vocabulary and process-local ordering, and JSON persists
position/vitals revisions. Its owner and generation live only in a
`ConcurrentDictionary`; they disappear on restart and cannot fence another
process or host. It must not be described or deployed as a scale-out
ownership mechanism.

The nullable coordinator fallbacks inside focused handler/registry tests are
also compatibility seams. Normal server startup always supplies the shared
coordinator.

## Verification

Automated coverage includes:

- queue capacity one, latest-value coalescing, stale/equal revision handling,
  finite direct admission, transient retry, retry exhaustion, owner-release
  invalidation, cancellation-uncooperative disposal, readiness, and critical
  fault propagation;
- migration fields and constraints, schema-release upgrade/idempotence, and
  checkpoint-state preservation;
- PostgreSQL acquisition/reacquisition, replacement fencing, exact replay,
  conflicting replay, stale writes, release, wrong-account handling,
  concurrent reordered position/vitals writes, and exact fixture cleanup;
- JSON compatibility acquisition, position/vitals persistence, replacement
  ownership, stale-owner rejection, and snapshot reload;
- snapshot-contract compatibility, forced stale/replacement acquisition
  interleaving, acquired-owner cleanup after failed refresh, independent
  final barriers, vitals-clamp revisioning, and secure-realtime handler
  integration; and
- JSON/environment option validation plus checked-in safe defaults.

The mandatory B03 gate now expects 31 migrations headed by
`20260730_030_character_checkpoint_versions` and requires
`PostgreSQL versioned character checkpoints`.

Verification at documentation time:

- Release solution build: **0 warnings, 0 errors**;
- complete managed protocol suite: **237 passed, 0 failed**;
- focused checkpoint, handler lifecycle, snapshot, secure-realtime, JSON, and
  architecture-ratchet checks: **6 passed, 0 failed**; and
- mandatory disposable PostgreSQL B03 gate: **36 required checks and 3
  migration scenarios passed**, cleanup passed, with 31 migrations headed by
  `20260730_030_character_checkpoint_versions`.

The machine-readable PostgreSQL result is
`artifacts/b03/postgres-ci-result.json`.

## Rollback

Migration 030 is additive and must remain in migration history after
deployment. Do not remove its columns, rewrite its checksum, or decrement an
ownership generation.

Application rollback is a coordinated binary rollback to the previous
single-instance server while leaving the additive fields in place. Drain
current sessions first so their final barriers and owner releases can run.
The local JSON adapter is not a production rollback mechanism.

No runtime feature flag bypasses the coordinator in the production
composition. If an emergency compatibility flag is later added, it must be
restricted to one process and must never turn zero-row PostgreSQL writes into
reported transfer success.

## Known gaps and B11 handoff

- A process crash can lose the bounded, coalesced tail that has not reached
  PostgreSQL. Recovery intentionally starts from the last durable checkpoint;
  checkpoint traffic is not a transactional event log.
- Final session flushing is deadline-bound and best effort during transport
  failure or process shutdown.
- B10 covers position and HP/MP. Other online-duration and gameplay
  persistence paths retain their feature-specific policies.
- Metrics exist in-process; exporter wiring, dashboards, alerts, and private
  readiness operations remain B13.
- JSON ownership is process-local, resets after release or restart, and is
  unsuitable for multi-process use.
- PostgreSQL acquisition is an unconditional newer-owner takeover. It fences
  old checkpoint writes but does not prevent two processes from admitting the
  same character into gameplay.
- Asynchronous `OwnershipLost` and `CharacterNotFound` results do not
  currently disconnect or directly notify the originating session.
- A `Superseded` flush barrier accepts a newer revision without proving equal
  payload. The current single-owner serialized lifecycle is part of this
  safety argument.
- B15 must extend durable ownership fencing across all valuable player
  transactions before safe horizontal player ownership is claimed.

The next dependency-ordered ticket is B11. It must make character creation
and deletion retry-safe, define account-slot cardinality, add tombstone and
restore/purge semantics, and preserve the B10 checkpoint revisions and owner
invariants across lifecycle transitions.
