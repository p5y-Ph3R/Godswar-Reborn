# B15 PostgreSQL player ownership fence

Status: completed and verified on 2026-07-31.

## Outcome

B15 promotes the owner UUID and monotonic generation introduced for B10
checkpoints into the single player-session ownership fence for every
PostgreSQL-backed valuable character mutation.

The authority remains the existing `public.character_base` row:

- `checkpoint_owner_id` identifies the current server-created connection
  owner;
- `checkpoint_owner_generation` is never reset and advances whenever a
  different owner acquires the character; and
- `lifecycle_state = 'active'` is required for a current owner.

The column names are retained for binary and migration compatibility, but
their application meaning is now broader than checkpoints. No second owner
table, Redis lock, migration, dual write, or distributed transaction was
introduced.

## Correctness boundary

`PlayerOwnershipFence` is the application contract carried beside a
`CommandEnvelope<T>`. It is excluded from the command request digest:
reconnecting through a newer owner must still be able to replay the same
durable operation ID, while PostgreSQL independently decides whether the
caller may act now.

For a valuable transaction,
`PostgresPlayerOwnershipGuard.LockCurrentAsync`:

1. locks the account-owned `character_base` row with `FOR UPDATE`;
2. verifies the exact owner UUID and generation;
3. verifies that the character remains active; and
4. holds the conflicting row lock until the complete mutation, inbox,
   ledger/audit, and outbox transaction commits or rolls back.

Ownership replacement uses an `UPDATE` of that same row. It therefore waits
for an in-flight valuable transaction. After replacement commits, an old
owner cannot acquire the transaction guard even if it presents a numerically
higher invented generation. Generation comparison is exact, not
"greater-than wins."

After a committed or replayed durable result,
`ValidateCurrentAsync` re-reads the fence before the caller projects or
acknowledges that result. An ownership-loss exception fails closed. It does
not turn a committed command into an uncommitted command; the stable operation
ID remains replayable by the current owner.

## Protected mutation families

The checked-in architecture ratchet inventories these 18 PostgreSQL executor
families:

| Area | Durable mutations |
| --- | --- |
| Inventory/economy | developer item grant and bag clear; forge; gear enhancement; decomposition; material conversion; make attribute stone; item delete and move; equipment/bag transfer; Holy Stone |
| Progression | talent upgrade; online progression interval; monster-death reward |
| Zodiac | activation, upgrade, and selection |
| Pets | hatch, level, and bag/presence mutations through the pet durable executor |

Their transaction entry points lock the player fence before inbox access,
advisory/death locks, inventory rows, pet rows, progression rows, or other
child aggregate state. Pre-route replay APIs now require the same ownership
fence; a command UUID alone is not authorization.

The legacy direct Zodiac-level transaction is protected by the same
transaction-wide guard and post-commit validation even though it is not one
of the extracted executor families. The unused broad
`AddZodiacAccumulationAsync` mutation was removed from `IGameStore` and both
store implementations so it cannot become an unfenced production shortcut.
The persisted accumulation fields and their load/save round trip remain
unchanged.

`PlayerOwnershipArchitectureChecks` fixes the reviewed executor inventory and
requires the guard plus post-commit validation in every valuable executor.
The character-lifecycle executor is the only explicit account-level
exception: create has no character yet, while delete now rejects an actively
owned character.

## Session and ECS integration

One server-created `_commandConnectionId` now identifies both command
correlation and ownership acquisition for a game connection.
`GameClientHandler` acquires ownership before world entry, refreshes the
authoritative snapshot, installs the exact fence on the runtime character,
and binds it to the current account session in `GameSessionRegistry`.

The registry records the fence on both account and world registrations.
World readiness, map transfer, projection refresh, and removal can validate
the exact registration rather than relying only on account ID or object
reference. A replacement local session cannot be removed or marked stale by
the session it replaced. Durable registry composition also rejects a world
join without a valid, current fence; the default-fence behavior remains only
for non-durable JSON/test compatibility.

Before a durable command, the handler captures and binds its current fence.
After the asynchronous durable call, it revalidates the same local
registration before changing the ECS/client projection or sending the
result. A stale handler disconnects instead of continuing with an old
generation.

Transient gameplay effects are fenced at their publication boundaries too.
A replaced session cannot broadcast chat or leave state, apply combat
damage, complete a delayed cast, consume queued realtime input, publish a
realtime snapshot or correction, broadcast movement, or enqueue a position
checkpoint. The realtime path checks once before input application and again
immediately before each externally visible effect.

Online progression work carries the fence in its session-owned state and
retry envelope. A retry is not silently rebound to an unrelated owner.
Disconnect finalization settles the current session's bounded online tail
before releasing ownership.

The production PostgreSQL composition requires the extracted durable
executors, checkpoint coordinator, and progression interval executor at
construction time. Missing dependencies fail startup, and broad registry or
handler persistence fallbacks fail closed. Compatibility JSON/local paths
may retain their process-local behavior, but they do not provide the B15
multi-process guarantee and must not be represented as production
authority.

## Character lifecycle

Character create, restore, and purge remain account/lifecycle operations with
their existing account-slot transaction and monotonic lifecycle version.
Character delete now returns terminal `CharacterInUse` when
`checkpoint_owner_id` is set. It no longer clears ownership as a side effect.
The current session must release the fence explicitly before deletion.

The legacy broad PostgreSQL delete compatibility path also refuses to select
an actively owned character. Production still uses the extracted,
inbox/outbox-backed lifecycle executor.

## Observability

`godswar_player_ownership_validations_total` has exactly two bounded
dimensions:

- `stage`: `transaction` or `post_commit`;
- `outcome`: `current`, `ownership_lost`, or `character_not_found`.

No account, character, owner, session, operation, IP address, or
attacker-controlled value is a metric label. Existing command outcome,
checkpoint-conflict, readiness, and structured-log controls remain in place.

## Verification

The B15 gate is:

```powershell
dotnet build GodswarServer.sln --configuration Release --no-restore --nologo
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll
powershell -NoProfile -File tools/InvokeB03PostgresCiGate.ps1
git diff --check
```

Coverage includes:

- envelope binding and invalid/default fence rejection;
- exact lower and forged-higher generation rejection;
- owner replacement blocking behind a valuable transaction row lock;
- monotonic replacement after release;
- stale-owner and missing-character outcomes;
- bounded metric dimensions;
- fixed source inventory for all valuable PostgreSQL executors;
- delete rejection while an owner is active;
- durable replay requiring current ownership;
- session replacement, world registration, transfer, and projection guards;
  replacement-session chat, combat, leave, and realtime-movement effect
  rejection;
- durable composition and invalid world-join rejection;
- direct Zodiac-level ownership races and retirement of the unused broad
  accumulation mutation; and
- the existing malformed, duplicate, lost-ack, concurrency, migration,
  network, and persistence suites.

Final evidence:

- Release solution build: passed with **0 warnings and 0 errors**.
- Full managed protocol suite: **263 passed, 0 failed**. Its **48 skips** are
  the explicitly database-backed checks run by the mandatory gate below.
- Disposable PostgreSQL 17.9 gate: **42 required checks passed** across
  **4 migration scenarios**.
- Schema release: **35 migrations**, ending at
  `20260731_034_pet_durability_foundation`.
- Disposable database cleanup: passed; no `godswar_b03_*` database remained.
- PostgreSQL gate duration: **362,612 ms** on this local workstation.
- Ownership contracts, stale-session races, fixed-executor inventory,
  direct-Zodiac, fail-closed composition, replay, and source ratchets all
  passed as part of those suites.
- `git diff --check`: passed.
- Every changed C# source file remains below the repository's 20 KB limit.
- The `legacy-raw` loopback development server was rebuilt from the final
  source and became healthy with restart count 0; PostgreSQL remained healthy
  and its live volume was not recreated.

## Migration and rollback

B15 deliberately adds no schema migration. Existing migration 030 already
created the authoritative nullable owner UUID, non-negative monotonic
generation, and constraints. Reusing that row also keeps older compatible
binaries from seeing two competing authorities.

Rollback is a coordinated application rollback to the B14 binary while
retaining the additive B10 columns and their current values. Draining active
sessions before rollback avoids an older process running without the broader
value fence. Do not reset generations, clear owners in bulk, or introduce a
Redis-only owner during rollback.

## Limitations and next work

- B15 prevents two owners from both committing valuable PostgreSQL state. It
  does not itself route or instantly disconnect a stale session on another
  process.
- TLS tickets, authenticated UDP session state, and current server routing
  remain process-local.
- JSON compatibility storage has no cross-process fence guarantee.
- The combat event must still reach the durable reward executor; B15 does not
  close the previously documented pre-journal crash gap.
- Presence such as the legacy account online flag is a projection, not the
  value-authority fence.

The next dependency-ordered ticket is **B16: Redis decision ADR**. Redis
remains deferred unless measured topology, latency, TTL, or coordination
requirements justify it. If B16 approves Redis, B17 leases and routes must
carry the PostgreSQL-issued generation; Redis must never become the authority
that can reset or override this fence.
