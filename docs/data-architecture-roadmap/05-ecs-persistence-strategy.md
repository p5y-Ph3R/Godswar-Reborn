# 5. ECS persistence strategy

## 5.1 What to persist

Persist explicit durable facets, not arbitrary ECS component memory:

- character identity/ownership and lifecycle;
- last accepted safe location checkpoint;
- vitals checkpoint according to the documented crash policy;
- progression, wallet, skills, talents, zodiac, and durable boost durations;
- inventory/equipment/mount item rows and attribute/socket state;
- pet ownership and permanent pet progression once that feature is committed;
- explicitly permanent world changes;
- command inbox, audit, and outbox records required to prove valuable mutations.

Use the existing PostgreSQL character/item/pet tables as the authoritative model. Hydrators such as `GameCharacterEcsHydrator` should translate a versioned `CharacterLoadSnapshot` into runtime components. The persistence model must not mirror every component one-for-one.

## 5.2 What must remain runtime-only

Do not persist:

- runtime `EntityId` index/generation handles;
- sockets, sessions, packet buffers, queues, endpoint addresses, cryptographic keys, replay windows;
- movement intents, input sequence windows, transient cooldowns/reservations;
- AOI/viewer membership, `WorldReady`, aggro targets, patrol targets, attack timers;
- ordinary monster HP/lifecycle/RNG state;
- scheduler clocks, recovery/status timers, background-loop cursors that are safely reconstructable;
- transient combat status unless a product rule explicitly promotes it to a durable online-duration effect.

## 5.3 Derived and replicated state

Recalculate `PlayerCalculatedStatsComponent`, equipment scores/ranks, maximum stat projections, active pet bonuses, and composed status snapshots from committed durable inputs plus a pinned content revision. Do not update both source fields and calculated totals as independent authorities.

`PlayerEcsSnapshotAdapter`, packet-builder DTOs, UDP snapshots, movement revisions, and viewer-specific projections are replicated state. They may be cached briefly in memory for baseline/delta construction but are not database rows.

## 5.4 Durable identity and aggregate boundaries

- PostgreSQL integer/bigint IDs identify accounts, characters, items, pets, and future durable aggregates.
- ECS `EntityId` remains an ephemeral `(index, generation)` handle.
- Network object IDs remain scoped runtime identifiers and must map to a durable character/monster definition through session/map-owned tables.
- A player ownership generation/fencing token must accompany writes once more than one process can own a character. It is not the ECS generation.

Recommended aggregate transaction boundaries:

- account credential;
- character lifecycle/bootstrap;
- inventory + equipment + wallet for one operation;
- progression/zodiac operation;
- pet operation;
- future trade/auction transaction, which may lock multiple aggregates in deterministic order and use escrow.

Do not force all character state into one transaction on every change. Use narrow sub-aggregate versions (`inventory_version`, `progression_version`, `checkpoint_version`, pet `revision`) plus a session ownership fence.

Contracts are operation-specific when invariants cross aggregates. A forge, purchase, reward, or future trade must not call independently committing `IInventoryTransactions` and `ICurrencyTransactions`. It uses one `IForgeTransaction`, `IPurchaseTransaction`, `IRewardTransaction`, or equivalent PG port that locks in deterministic order and commits inbox, wallet, items, audit, and outbox once. Database completions return through the owning player mailbox so two concurrent command completions cannot project into ECS/session state out of order.

## 5.5 Dirty tracking and save timing

Use explicit per-facet dirty/checkpoint state:

- `PositionDirty(map, x, z, ownerGeneration, positionRevision)`;
- `VitalsDirty(hp, mp, lifeRevision, vitalsRevision)`;
- online-duration watermarks;
- no generic `ComponentPool<T>.IsDirty`.

Transactional player-value operations are write-through: validate, commit PostgreSQL, then project the committed result into ECS/session state. Position/vitals are coalesced through bounded single-slot mailboxes and periodic checkpoints. A save coordinator clears a dirty revision only when the same or newer revision commits; a late completion must never clear newer dirty state.

Suggested triggers:

- movement: current two-second bounded checkpoint, adjusted only by measurement;
- vitals: periodic/coalesced plus death, map transfer, logout;
- online-duration boosts/zodiac: current periodic interval plus logout, with non-overlapping durable watermarks;
- valuable state: immediately in its command transaction;
- autosave: dirty facets only, jittered, bounded, and off the map tick.

## 5.6 Snapshot, event, retry, shutdown, and recovery model

Use a hybrid:

- relational current-state rows are the load snapshot;
- append-only audit/ledger rows explain valuable changes;
- a transactional outbox carries post-commit notifications/cache invalidation;
- a command inbox/deduplication row makes retry safe;
- do not adopt full event sourcing or persist whole ECS worlds.

Failed checkpoints retry through a bounded coalescing worker with exponential backoff, jitter, a maximum retry age, and metrics. Valuable command failures do not report success and retain no partially projected value. Outbox delivery is at least once and consumers are idempotent.

Graceful shutdown:

1. mark readiness false and stop new login/zone admission;
2. stop accepting new gameplay commands;
3. drain bounded command mailboxes to a deadline;
4. flush dirty checkpoints and outbox dispatch to a configured deadline;
5. release session ownership only after the final fenced save;
6. close transports and Npgsql data source;
7. if the deadline expires, terminate and rely on last committed state/reconciliation rather than unbounded shutdown.

Crash recovery loads the last committed current state, ignores stale process-local presence, reacquires ownership with a higher fencing token, consumes pending outbox rows, and reconciles any incomplete operations by inbox/ledger identity. Runtime monster/AOI/status state is rebuilt from content and session rules.

## 5.7 Concurrency and schema versioning

Add optimistic versions where valuable aggregates lack them. Every update should include the expected version and, after scale-out, the current ownership fence. A zero-row update is a conflict, not silent success. Row locks remain appropriate for short, high-value multi-row transactions; lock rows in deterministic ID order.

Persist explicit contract/content schema versions rather than CLR type names. Each load mapper supports the current version and the immediately preceding rolling-deployment version. Migrations use expand/contract:

1. add nullable/new columns or tables;
2. deploy code that reads old and new and writes the new representation;
3. backfill and reconcile;
4. deploy code that requires the new representation;
5. remove the old representation in a later release.

The current `PostgresSchemaMigrationPlan` rejects every database ahead of the binary. That fail-closed rule is safe now but prevents an older instance from remaining alive after an additive expand migration, so it does not yet support rolling schema deployment.

The target keeps checksum/order integrity for every migration known to a binary and adds a stable compatibility manifest with active schema contract, minimum reader/writer build, and content revision. An older binary may tolerate only an explicitly declared safe additive suffix and must still reject unsupported/unknown contracts; a new binary must support both old and expanded forms during the rollout. Contract migrations occur only after old writers drain.

## 5.8 Expected sequences

### Character login and loading

1. TLS authentication verifies credentials; raw compatibility is not a target trust path.
2. Consume a one-time game ticket and bind the account/session.
3. Acquire the player-session ownership generation locally; later acquire a fenced distributed lease only when scale-out exists.
4. Open a short read-only `REPEATABLE READ` load transaction for character base, inventory/equipment, progression, skills/talents/zodiac, boosts, and pets; copy the versioned snapshot and commit/close the transaction.
5. Outside the transaction, validate content/schema versions and calculate derived stats.
6. Hydrate ECS through dedicated hydrators; register map membership as not world-ready.
7. Send login bootstrap; mark ready only after required client acknowledgements.
8. Record presence as a projection. A failure before ready releases ownership and exposes no partial world entity.

### Character creation

1. Validate authenticated account, normalized name, class/camp rules, and idempotency key.
2. In one PostgreSQL transaction insert the character, starter items/equipment, skills, inbox result, audit, and outbox.
3. Commit, then return the committed character summary.
4. A retry reads the inbox result and does not create another character.

### Normal gameplay updates

1. Transport validates authentication, frame/datagram, sequence, replay, ownership generation, and rate limits.
2. Decoder creates an intent; the single map owner validates and mutates runtime ECS.
3. ECS emits immutable results; replication sends transient outcomes.
4. Dirty checkpoint facets enqueue bounded saves. Valuable rewards use a durable command transaction before value acknowledgement.

### Inventory modification

1. Keep the command on TLS/TCP.
2. Validate ownership, slots, item type, expected inventory version, and idempotency key.
3. One PostgreSQL transaction consumes/moves/creates items, adjusts wallet if applicable, records inbox/audit/outbox, and advances version.
4. Commit, update ECS/session projections, invalidate summaries asynchronously, then acknowledge.

### Currency modification

1. Use a server-defined reason/source operation ID; never accept a client-provided balance or delta without server-side derivation.
2. Lock/version the wallet; enforce checked arithmetic and nonnegative constraints.
3. Insert ledger + update balance + inbox/outbox in one transaction.
4. Commit before success acknowledgement. Retry returns the prior committed result.

### Trade completion

**Missing feature; target sequence only.** Acquire both participants in deterministic order, revalidate offer/item/wallet state, commit escrow transfer, balances, item ownership, inboxes, audits, and outbox in one PostgreSQL transaction. Never use Redis locks or cross-database transactions for trade correctness.

### Character autosave

Snapshot dirty facet values/revisions without blocking the tick, enqueue to a bounded coalescing worker, issue conditional PG updates, and clear only committed revisions. A save conflict causes ownership verification/reload, not blind overwrite.

### Disconnect

Stop new commands, remove world-ready/AOI presence, enqueue final checkpoint, flush durable online-duration watermarks to a deadline, release the local/future fenced ownership record, and close transport. Failure is logged/metriced; crash recovery relies on TTL/ownership generation and last checkpoint.

### Reconnect

Authenticate again, consume a new ticket, reject or replace the old connection using a generation, acquire ownership, load the latest committed snapshot, and create new transport epochs/replay windows. Never reuse UDP keys or assume the old endpoint.

### Logout

Perform disconnect flushing, mark the session intentionally closed, expire reconnect state, and append a security/account-session audit if required. Durable character data stays in PostgreSQL; presence disappears.

### Zone transfer

1. Validate portal/transfer intent and target capacity.
2. Freeze new source-map gameplay commands for that player.
3. Persist a versioned transfer record/target safe checkpoint.
4. For the current single process, atomically move map membership and increment world generation.
5. For a future second process, hand off a signed transfer token and fenced ownership generation; target acknowledges ownership before source releases.
6. Rehydrate target map and send a reliable keyframe. Reject packets from old world/transport generations.

### Server crash and recovery

Process-local tickets, sessions, AOI, replay windows, and monsters disappear. PostgreSQL current rows and inbox/outbox/audit survive. New instances run verified migrations, recover pending outbox work, treat stale online flags as projections, acquire a new ownership generation, and load players from their last committed checkpoint. Reconciliation alerts on balance/item/version discrepancies.
