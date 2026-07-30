# B12 progression, reward, and pet durability

Date: 2026-07-31
Status: completed, verified, and applied to the development database
Next roadmap ticket: B13 - structured logs, traces, and readiness

## Outcome

B12 closes the known retry gaps around monster-death rewards, online-only
progression intervals, and implemented pet value operations. PostgreSQL is the
authoritative production path. Each accepted operation now commits its value
mutation, command receipt, audit evidence, and ordered outbox event in one
database transaction.

The implementation deliberately uses two kinds of identity:

- Monster rewards and progression intervals use a stable, server-derived
  operation identity. A client cannot choose a death ID, online duration, or
  reward value.
- Pet bag activation, level-up, and presence operations use operation UUIDs
  supplied by the secure native compatibility shim and bound to the
  authenticated account, active character, and canonical intent.

An exact retry returns the stored result without repeating the mutation.
Reusing an identity for different canonical input is rejected.

## Schema increments

The ordered PostgreSQL migration catalog adds:

1. `20260731_032_progression_reward_foundation`
   (`PostgresSchemaMigrationCatalog.ProgressionRewards.cs`)
   - adds `character_base.progression_reward_revision`;
   - adds globally keyed
     `monster_death_reward_settlements(death_event_id)`;
   - binds a settlement to its command inbox, audit row, and outbox event;
   - makes settlement rows immutable, including protection from `TRUNCATE`.
2. `20260731_033_progression_interval_authority`
   (`PostgresSchemaMigrationCatalog.ProgressionIntervals.cs`)
   - constrains experience-modifier online ticks to non-negative values;
   - adds one `character_progression_interval_authority` row per character;
   - records the online session UUID, last accepted sequence/end time, and
     aggregate revision.
3. `20260731_034_pet_durability_foundation`
   (`PostgresSchemaMigrationCatalog.PetDurability.cs`)
   - adds the bounded, per-character `pet_durable_stream_versions` stream;
   - adds `pet_durable_command_evidence` over inbox, audit, outbox, and
     delivery state;
   - cascades only the pet stream projection when a character is physically
     purged. Permanent command/audit retention remains governed by the B08/B11
     evidence policy.

These migrations are additive. Their published identifiers and checksums must
not be edited after application.

## Durable monster-death rewards

### Identity and ownership

`Application/Rewards/MonsterDeathRewardCommandEnvelope.cs` derives one UUIDv8
death-event ID from this server-owned tuple:

```text
runtime instance UUID
+ map ID
+ monster object ID
+ spawn generation
+ lethal health revision
```

The runtime instance UUID is carried through both legacy and ECS monster
snapshots by `MonsterMapRuntime` and `EcsMonsterMapRuntime`. Spawn generation
and health revision distinguish successive lives of the same runtime object.
The hash-derived ID remains identical through connection retries and
reconnects, while a new process/runtime instance cannot accidentally reuse the
previous process's death namespace.

`PostgresMonsterDeathRewardCommandExecutor` takes a transaction-scoped
advisory lock for that death ID before reading or creating its settlement.
Because `monster_death_reward_settlements.death_event_id` is a global primary
key, two characters racing to settle one death cannot both receive it. The
first valid transaction owns the claim; another owner or changed request is a
conflict. An eligible zero-value reward is still claimed so the same death
cannot later be reused for value.

### Atomic settlement

One transaction:

1. validates the active account/character and locks its progression row;
2. derives level, experience, Talent experience, Talent points, and the next
   progression revision from database state and the frozen server reward;
3. appends permanent audit and command-inbox evidence;
4. updates authoritative character progression with optimistic revision
   protection;
5. appends strict `progression.monster_reward_settled` outbox evidence;
6. inserts the immutable global death settlement; and
7. commits before gameplay packets acknowledge the reward.

The handler in `Game/GameClientHandler.DurableMonsterRewards.cs` applies the
committed PostgreSQL projection to runtime state. A duplicate returns the
original receipt and current authoritative projection; it does not repeat
level-up packets, experience gains, Talent gains, or the ECS projection.
World-boss area activation uses the same death-event ID rather than inventing
a second death identity.

### Explicit remaining gap

There is still a narrow pre-command crash window: the combat runtime can apply
the lethal in-memory health mutation and then the process can crash before the
durable reward command reaches PostgreSQL. In that case PostgreSQL cannot
recover a command it never received, and the reward can be lost. Closing this
requires a durable combat/death-intent journal or moving the authoritative
lethal transition into the same durable boundary. That larger combat
transaction is not invented in B12.

This limitation is different from retries after PostgreSQL receives the
command. Once the transaction commits, reconnect, lost acknowledgement,
executor restart, and concurrent replay are covered by durable evidence.

## Durable online progression intervals

`Game/GameSessionRegistry.DurableProgressionIntervals.cs` owns one bounded
online-session state machine per active client session. It creates a new
server UUID at session start, assigns monotonically increasing interval
sequences, and retains a pending envelope until PostgreSQL accepts or
authoritatively rejects it.

`PostgresProgressionIntervalSettlementCommandExecutor` validates intervals
against `character_progression_interval_authority`:

- the first interval in a session must be sequence 1;
- intervals in one session must be contiguous in sequence and time;
- overlaps, gaps, reordered sequences, and stale sessions are rejected;
- a reconnect may start a new session at sequence 1 after an offline gap, so
  offline time is not charged; and
- the server-derived operation identity is deterministic for the same
  character, session, sequence, and interval.

One transaction locks the active character and:

- accrues Zodiac energy and its fractional remainder;
- updates the Zodiac online-day and duration watermark;
- decrements active experience/talent boost duration only for the accepted
  online interval and never below zero;
- advances interval authority;
- writes permanent audit and inbox receipt data; and
- appends strict `progression.online_interval_settled` outbox evidence.

A restart retry returns the stored receipt. It cannot add Zodiac energy or
consume boost duration twice. The runtime applies only the committed
projection and coalesces player notifications separately from persistence.

If the final disconnect write fails or its PostgreSQL outcome is unknown,
`GameSessionRegistry.DurableProgressionRetry.cs` transfers the unchanged
pending envelope to a process-owned handoff capped at 4,096 entries. A
supervised one-second loop retries with capped exponential backoff, and a
replacement session must drain that character's older envelope before it can
advance interval authority. The envelope retains the disconnect timestamp,
so retry time is never counted as online time. Saturation fails closed rather
than silently growing the handoff.

This handoff is deliberately volatile. It covers transient failures and
unknown commit outcomes while the process remains alive; PostgreSQL still
deduplicates a retry after an unknown commit. A server process or host crash
before PostgreSQL accepts the envelope can lose only the uncommitted tail
since the most recent successful checkpoint (normally one configured
persistence interval, plus time spent shutting down). A future durable local
or database intent journal is required to close that crash window.

## Retry-safe pet value commands

### Secure native identity

The managed command catalog contains:

- family 2: `PetLevelUpgrade`;
- family 26: `BagItemActivation`; and
- family 27: `PetPresenceTransition`.

The native shim mirrors these families in `SecureClientProtocol.h`.
`SecurePetCommandIdentity.{h,cpp}` recognizes legacy opcodes 10051 (bag
activation), 10239 (take), 10240 (call out), 10241 (recall), and 10285
(level-up). `SecurePendingOperationRegistry.Pets.cpp` assigns a cryptographic
operation UUID to the canonical intent and reuses it for a bounded retry.

For shared opcode 10051, the canonical intent contains only the authoritative
bag slot. The untrusted client item/action hint is ignored. PostgreSQL locks
and reads the actual row, then decides whether that slot contains a supported
pet egg or an equippable item.

Monster reward family 28 and progression interval family 29 are server-only;
they do not need native client families or client-generated UUIDs.

### PostgreSQL transaction

`PostgresPetDurableCommandExecutor` owns the PostgreSQL transaction for:

- bag item activation, including supported pet-egg hatch and right-click
  equipment activation;
- one-level pet upgrades with exact experience spending and stat growth; and
- take, call-out, and recall presence transitions.

The executor locks the active character and relevant item or pet rows, applies
the transition, and stores one canonical receipt with its audit. A successful
transition also advances the per-character pet stream and appends its strict
outbox event; a rejected transition does neither. Concurrent duplicates commit
once. Exact retries after process restart replay the original result, including
a hatch's randomized aptitude/stat outcome. A changed bag slot, pet ID, or
presence action under the same UUID is a request hash conflict.

Family 26 also participates in the B09 inventory authority:

- hatching records the consumed egg in the inventory ledger;
- equipping records the bag-to-equipment move;
- replacing occupied equipment records two ordered moves under one inventory
  revision;
- each mutation advances the inventory revision exactly once; and
- `character_inventory_reconciliation` remains aligned with the committed
  item rows and revision.

Equipment activation continues to use the server-owned `EquipmentSlots` and
`EquipmentEligibility` catalogs for authoritative slot, profession, level,
mount, and mount-gear checks. PostgreSQL supplies the locked equipped-item
projection to that shared policy. This keeps the durable path aligned with
the established equipment rules instead of treating every non-egg item as
interchangeable.

The secure handler refreshes the authoritative bag/pet/equipment projection
after a committed result or rejection. It does not trust stale client state to
construct the result. Ride-active is a server observation rather than part of
the client request hash: the executor first locks and classifies the actual
database item, then persists a replayable rejection only when that item is a
mount and Ride currently blocks the transition. Presence retries also rebuild
the current carried/summoned projection. A delayed old Call Out retry after a
newer Recall therefore cannot re-summon the pet or replay stale visuals.

## Persistence and transport boundaries

Application contracts live under `Application/Rewards`,
`Application/Progression`, and `Application/Pets`. PostgreSQL implementations
live under the matching `Infrastructure` folders and are composed by
`PostgresApplicationDataRuntime`. Gameplay handlers depend on those focused
contracts rather than constructing Npgsql commands.

The B12 application contracts own their intent, result, and projection types.
Legacy `State` models are translated at the game/storage boundary; new
application code does not depend on the broad `IGameStore` implementation.

Production PostgreSQL behavior is fail closed:

- if the durable reward executor is unavailable, a death reward is not
  granted through the legacy store;
- identified pet traffic cannot downgrade into the old raw mutation path;
- raw PostgreSQL pet mutation entry points reject once the durable stream is
  active; and
- durable online progression requires the PostgreSQL interval executor.

The JSON store remains an explicit local-development compatibility provider.
It does not provide PostgreSQL command inbox/outbox, global death claims,
cross-process retry evidence, or the durable interval authority described
above. Unidentified raw legacy TCP pet traffic is also weaker and is allowed
only where the controlled compatibility profile explicitly permits it. These
paths are not production-equivalent.

## Concurrency, scaling, and B15

B12 uses database row locks, unique keys, optimistic revisions, immutable
death claims, and strict per-aggregate event ordering. Those controls prevent
duplicate value inside the implemented command transactions.

They do not replace B15's PostgreSQL player-ownership fence. Before multiple
game-server processes can safely own or transfer one connected player, every
valuable transaction must lock and validate the shared owner row and monotonic
owner generation for the transaction's full lifetime. A stale process must
not commit merely because it holds a valid command UUID. B15 remains the
scale-out prerequisite for rewards, progression intervals, pet value, and the
earlier economy aggregates.

## Verification coverage

Automated B12 coverage includes:

- golden and validation checks for death-event and interval identity;
- global two-character contention for one monster death;
- exact reward replay, changed-request conflict, zero-value claim, and
  settlement retention after character purge;
- interval duplicate/restart replay, overlap, gap, stale/reordered session,
  reconnect, and offline-gap behavior;
- concurrent pet hatch/level operations, exact restart replay, changed-intent
  conflict, randomized hatch-result stability, and presence transitions;
- B09 inventory-ledger/reconciliation checks for family-26 hatch and equip;
- raw PostgreSQL pet-mutation fail-closed checks;
- migration catalog/order/constraint checks;
- native pet classifier, bounded registry, retry, result, and opcode-10051
  regression checks; and
- the architecture dependency ratchet.

Final verification on 2026-07-31:

- `dotnet build GodswarServer.sln -c Release --no-restore`: passed with
  0 warnings and 0 errors;
- the complete managed protocol suite: 250 passed, 0 failed;
- Release native shim build plus `Godswar.NetShim.Checks.exe --offline`:
  passed, with `Net.dll` SHA-256
  `D32C41F80EBCBB5C7870953B095C73D425D06EF3A882C957F4E04303252144E8`;
- mandatory B03 PostgreSQL 17 gate: 42 required checks and four migration
  scenarios passed in 427,506 ms; cleanup passed; machine-readable local
  evidence is `artifacts/b03/b12-final-result.json`;
- the development database advanced from 32 migrations/head 031 to
  35 migrations/head `20260731_034_pet_durability_foundation`;
- development rows were preserved across application: 11 accounts,
  9 characters, 105 items, and 1 pet before and after;
- all 118 changed files passed the 20 KB/600-line repository limit; and
- `git diff --check` passed.

## Rollback and operations

Migrations 032-034 are additive and remain in migration history during an
application rollback. Do not delete immutable reward settlements, reset
aggregate revisions, or remove interval authority to make an older binary
appear compatible.

For a controlled rollback:

1. drain active sessions so pending intervals are settled;
2. stop new secure pet and reward commands;
3. stop outbox dispatchers cleanly;
4. preserve all authoritative rows, inbox receipts, audits, ledgers, and
   outbox events; and
5. deploy only a compatibility binary that understands the added columns and
   refuses unsafe fallback mutations.

The native shim remains separately recoverable through its checksummed
installer rollback. Removing it makes the controlled client unable to supply
pet operation UUIDs; PostgreSQL pet mutations then fail closed rather than
downgrading.

## B13 handoff

B13 should export the already bounded, low-cardinality command metrics and
readiness signals for:

- reward commits, duplicates, conflicts, revisions, and settlement failures;
- interval commits, overlaps/gaps, stale sessions, and boost/Zodiac update
  failures;
- pet outcomes, request conflicts, stream gaps, and inventory reconciliation;
- outbox backlog, retry, poison, and consumer-gap state; and
- critical persistence worker health.

It must keep account IDs, character IDs, pet IDs, death IDs, operation UUIDs,
IP addresses, and attacker-controlled payload text out of metric labels.
