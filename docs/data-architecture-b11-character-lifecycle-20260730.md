# B11 character lifecycle and tombstones

Date: 2026-07-30
Status: implementation complete; final verification results pending
Next roadmap ticket: B12 - progression, reward, and pet durability

## Outcome

B11 turns character creation and deletion into an explicit lifecycle for the
one character slot supported by the installed client. PostgreSQL is the
authoritative production path. A delete no longer destroys the character
aggregate immediately: it creates a recoverable tombstone, hides that row
from login and gameplay reads, clears its B10 checkpoint owner, and records a
30-day restore window followed by a 7-day purge grace period.

Secure-client create and delete commands carry stable client operation UUIDs.
Their PostgreSQL mutation, permanent command receipt, audit row, and strict
versioned outbox event share one transaction. While a command remains pending,
an exact retry carrying the same UUID can therefore return the committed
result after a lost acknowledgement or reconnect without performing the
mutation twice. Reusing a pending UUID with a different canonical intent is
rejected.

Restore and purge use the same application and PostgreSQL command boundary,
but are service-only operations in this increment. The original client has no
reviewed restore or purge packet or UI. B11 does not invent one.

## Confirmed client cardinality and packet boundary

The supported contract remains
`CharacterSlotPolicy.SingleCharacterV1`: account slot `0` may have at most one
active character.

Repository evidence supports this decision:

- the B06 snapshot contract and original-client preview flow represent zero or
  one character, not a selectable collection of slots;
- `captures/champ-talent-20260519-115333.log` contains the captured 80-byte
  `CreateRole` request with opcode `10003`;
- the reviewed request layouts use an 80-byte create frame and a 68-byte
  `DeleteRole` frame with opcode `10004`; and
- the first 32-byte delete field is client-supplied account text. It is not an
  authority boundary. The server-derived authenticated account remains the
  principal, while only the requested character name belongs in the delete
  intent.

Supporting symbols:

- `src/Godswar.Server/Application/Characters/CharacterSnapshotContract.cs`
- `src/Godswar.Server/Protocol/Opcodes.cs`
- `src/Godswar.Server/Game/GameClientHandler.LoginWorldEntry.cs`
- `client/network-shim/src/SecureCharacterLifecycleIdentity.h`
- `client/network-shim/src/SecureCharacterLifecycleIdentity.cpp`

Supporting more than one character per account remains a future,
versioned-product decision. It requires a compatible client selection flow
and a new slot policy rather than relaxing the B11 constraint in place.

## Migration 031 and lifecycle authority

Forward migration `20260730_031_character_lifecycle_foundation` adds
`accounts.character_lifecycle_version`. It is a non-negative, monotonic
version for the account/slot aggregate and is initialized to `1` for an
account that already has a character.

The migration adds these fields to `character_base`:

- `character_slot smallint NOT NULL DEFAULT 0`;
- `lifecycle_state varchar(16) NOT NULL DEFAULT 'active'`;
- `lifecycle_version bigint NOT NULL DEFAULT 1`;
- `deleted_at timestamptz`;
- `restore_until timestamptz`; and
- `purge_after timestamptz`.

Checks restrict the slot to `0`, the state to `active` or `deleted`, and the
timestamp combinations to a valid active row or a valid ordered tombstone.
A deleted row cannot retain a B10 `checkpoint_owner_id`.

`ux_character_base_active_account_slot` is a partial unique index over
`(account_id, character_slot)` where the state is active. A tombstone does not
occupy the active slot, so an account may create a replacement while the old
row remains recoverable. The existing global character-name index remains in
force: a tombstone keeps its name reserved until controlled purge.

Migration preflight refuses to apply `SingleCharacterV1` when an existing
account has more than one character. It does not silently select or delete a
row. Deleted rows are indexed for account/slot lookup and due-purge scanning.

Every successful create, delete, restore, or purge:

1. serializes on the authoritative account row;
2. increments `accounts.character_lifecycle_version`;
3. uses that revision for the `account_character_slot` aggregate;
4. copies it to the affected character while that row exists; and
5. emits its outbox event at that exact revision.

This account-owned counter does not reset when a character is purged or
replaced, so strict outbox ordering cannot collide with an earlier character
that occupied the same slot.

An upgraded account can already be at lifecycle version `1` without a
historical `character.created` event. Before the first B11 event is inserted,
the executor uses insert-if-absent semantics to initialize the lifecycle
consumer position to the immediately preceding account version in the same
transaction. The first delete of such an upgraded character can therefore
deliver strict revision `2` without waiting forever for a synthetic revision
`1`. A newly created account starts at `0`, so its first create event remains
revision `1`.

Primary evidence:

- `src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.CharacterLifecycle.cs`
- `src/Godswar.Server/State/CharacterLifecycleState.cs`
- `src/Godswar.Server/State/GameCharacter.cs`
- `tests/Godswar.Server.ProtocolChecks/PostgresCharacterLifecycleMigrationChecks.cs`
- `tests/Godswar.Server.ProtocolChecks/PostgresCharacterLifecycleMigrationIntegrationChecks.cs`

## Secure command identity

The secure network shim classifies only the two original-client mutations:

| Original opcode | Secure command family | Code | Canonical identity |
| --- | --- | ---: | --- |
| `CreateRole` (`10003`) | `CharacterCreate` | 22 | slot, name, and appearance/class intent |
| `DeleteRole` (`10004`) | `CharacterDelete` | 23 | slot and character name; client account text ignored |

Equivalent wire retries reuse the same operation UUID only while that command
is pending. After a terminal secure result, the shim removes the pending
intent and retains a bounded tombstone containing only operation ID and
family. It does not use that tombstone to match intent: a later deliberate
command with the same create/delete fields receives a fresh UUID. Malformed
frame lengths, blank or invalid names, invalid bounded create fields, missing
authenticated principal, and result-family mismatches are rejected.

The C# application adds service-only family `24` (`CharacterRestore`) and
family `25` (`CharacterPurge`). These do not appear in the client shim because
the original client supplies no corresponding command.

Primary evidence:

- `client/network-shim/src/SecureClientProtocol.h`
- `client/network-shim/src/SecureCharacterLifecycleIdentity.cpp`
- `client/network-shim/src/SecurePendingOperationRegistry.CharacterLifecycle.cpp`
- `client/network-shim/tests/SecureCharacterLifecycleIdentityTests.cpp`
- `src/Godswar.Server/Application/Commands/CommandEnvelope.cs`
- `src/Godswar.Server/Application/Commands/LegacyCommandIdentityPolicy.cs`
- `src/Godswar.Server/Application/Characters/CharacterLifecycleCommands.cs`
- `src/Godswar.Server/Application/Characters/CharacterLifecycleCommandEnvelopes.cs`

## PostgreSQL transaction and replay semantics

`ICharacterLifecycleCommandExecutor` defines create, delete, restore, and
purge. The PostgreSQL implementation uses B08's permanent
`command_inbox`, `command_audit`, and `outbox_events` tables rather than a
second lifecycle-specific idempotency store.

The aggregate type is `account_character_slot`; its key is
`<accountId>:<slot>`, and its outbox ordering policy is strict. Canonical
request bytes are hashed. A completed inbox row carries a bounded,
hash-verified lifecycle receipt with the result, account, slot, character,
revision, name, retention timestamps, audit reference, and successful outbox
event ID.

The durable outcomes distinguish:

- created, deleted, restored, and purged success;
- occupied slot or unavailable name;
- missing character or name mismatch;
- stale lifecycle version or invalid state;
- expired restore or restore blocked by an active replacement; and
- purge that has not reached its eligible time.

An exact operation replay returns the stored receipt. A UUID/hash conflict is
not treated as a duplicate. Terminal business rejections are also durable, so
a retry receives the same answer rather than racing a later state and
changing meaning.

Delete's canonical client intent is only slot plus character name. The active
character ID and lifecycle version captured by a fresh handler are
server-derived compare-and-set preconditions, not client intent and not part
of the request hash. This lets the same pending UUID and name replay after
deletion has removed the active projection, while that UUID with a different
name still conflicts.

Create reserves the account revision and writes the character base row,
starter equipment and bag items, starter skills, and B09 economy baseline in
the same PostgreSQL transaction as its inbox, audit, and outbox evidence.
Delete changes the active row into a tombstone and clears its checkpoint owner
without cascading child rows. It preserves B10's monotonic checkpoint owner
generation, so a restored character cannot reuse an earlier fence generation.
Restore requires the expected tombstone revision, an unexpired restore window,
and an empty active slot. Purge requires the expected tombstone revision and
`purge_after` to be due before the physical delete may cascade through owned
child rows.

Primary evidence:

- `src/Godswar.Server/Application/Characters/ICharacterLifecycleCommandExecutor.cs`
- `src/Godswar.Server/Application/Characters/CharacterLifecycleExecution.cs`
- `src/Godswar.Server/Infrastructure/Characters/CharacterLifecyclePersistenceCodec.cs`
- `src/Godswar.Server/Infrastructure/Characters/CharacterLifecycleOutboxConsumer.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterLifecycleCommandExecutor*.cs`
- `tests/Godswar.Server.ProtocolChecks/PostgresCharacterLifecycleCommandIntegrationChecks*.cs`

## Handler and active-row visibility

The secure `CreateRole` and `DeleteRole` handlers use the operation metadata
added by the shim and the PostgreSQL lifecycle executor. Both a newly
committed success and an exact successful duplicate refresh the authoritative
B06 snapshot. When that current snapshot still matches the successful receipt,
the handler sends the original native success packet and the current character
preview.

A successful historical receipt can be replayed after later lifecycle work has
changed the slot. In that case the handler suppresses the now-stale native
create/delete success, sends the current authoritative preview, and still sends
an `Applied` or `Replayed` secure result to settle the operation UUID. This
prevents an old acknowledgement from replacing the current selection view
while still completing the durable retry protocol.

Secure lifecycle mutations without a usable client operation ID fail closed.
The account is always derived from the authenticated server session. A
client-supplied delete username is ignored.

A provider exception, a missing durable receipt, a receipt whose durable
identity does not match the requested operation, or an authoritative snapshot
refresh failure does not send a false success or settle the secure operation.
The shim keeps the bounded operation pending so a retry can recover the
PostgreSQL receipt and current snapshot. A current projection that has merely
moved beyond a valid historical receipt is not such a failure; it follows the
settled historical-receipt behavior above.

Character selection, snapshot loading, stats lookup, direct character reads,
and checkpoint ownership acquisition filter for `lifecycle_state = 'active'`.
A tombstoned character therefore cannot re-enter gameplay or reacquire a B10
checkpoint lease through the normal runtime path.

Primary evidence:

- `src/Godswar.Server/Game/GameClientHandler.LoginWorldEntry.cs`
- `src/Godswar.Server/Game/GameClientHandler.DurableCharacterLifecycle.cs`
- `src/Godswar.Server/Infrastructure/PostgresApplicationDataRuntime.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterSnapshotReader.Core.cs`
- `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterCheckpointStore.Ownership.cs`
- `src/Godswar.Server/State/PostgresGameStore.Characters.cs`
- `src/Godswar.Server/State/PostgresGameStore.CharacterLifecycle.cs`

## Raw legacy and JSON compatibility limits

The durable retry guarantee applies only to the secure PostgreSQL path.

When the PostgreSQL durable lifecycle executor is active, the runtime rejects
raw legacy TCP create/delete commands because they have no stable client
operation UUID. It does not silently fall back to a weaker mutation path. The
broad PostgreSQL `CreateCharacterAsync` and `DeleteCharacterAsync` methods are
compatibility-bootstrap entry points only: they fail closed once a lifecycle
consumer position or lifecycle event exists for that account slot. This keeps
an unversioned direct mutation from interleaving with the durable lifecycle
stream.

The JSON provider also remains local-development compatibility. It persists
active/deleted lifecycle state, the one-slot rule, a monotonically increasing
per-account lifecycle version derived from retained rows, and the same 30-day
plus 7-day timestamps. It does not provide PostgreSQL row locks, permanent
inbox/audit/outbox receipts, multi-process serialization, or the production
restore/purge service boundary. Purging all retained JSON rows would also
remove the source from which its compatibility version is derived. Its
explicitly controlled raw local create/delete path therefore remains weaker
than secure family 22/23 execution and must not be presented as equivalent.

Primary evidence:

- `src/Godswar.Server/State/JsonGameStore.CharacterLifecycle.cs`
- `src/Godswar.Server/State/JsonGameStore.CharacterSnapshots.cs`
- `src/Godswar.Server/State/PostgresGameStore.CharacterLifecycle.cs`
- `src/Godswar.Server/ServerOptions.cs`
- `docs/data-architecture-b04-fail-closed-profiles-20260729.md`

## Verification

Automated coverage added by B11 includes:

- migration catalog order, checksum, columns, constraints, indexes, preflight
  refusal, empty/current upgrade, and lifecycle-state preservation;
- active-slot concurrency, monotonic revision changes, tombstone coexistence,
  restore blocking/expiry, purge eligibility, and checkpoint-owner clearing;
- command envelope bounds, canonical request identity, durable terminal
  results, exact duplicate replay, pending reconnect/lost-ack replay,
  UUID/hash conflict, concurrent command races, and upgraded version-1
  account dispatch of a first strict version-2 lifecycle event without a
  false gap;
- secure handler success, duplicate, rejection, missing-operation, untrusted
  username, snapshot refresh, historical successful receipts that suppress
  stale native success while settling the secure UUID, and current-preview
  behavior;
- character-selection phase guards for secure and raw create/delete, including
  secure rejection settlement and fail-closed raw behavior outside selection;
- PostgreSQL mixed-mode regressions proving raw runtime commands are rejected
  while the durable executor is active and broad compatibility mutations fail
  once lifecycle stream state exists; and
- network-shim frame classification, canonicalization, pending-retry UUID
  reuse, fresh identity after terminal resolution, malformed packets, and
  family conflicts.

Final verification evidence:

- Release solution build: **PASS** (0 warnings, 0 errors);
- complete managed protocol suite: **PASS** (242 passed, 0 failed);
- focused migration, command, handler, and data-boundary checks:
  **PASS** for each group;
- native Release network-shim build and offline checks: **PASS**; and
- mandatory disposable PostgreSQL B03 gate:
  **PASS** (39/39 required checks, 0 failed, 0 skipped; 4/4 migration
  scenarios; cleanup passed; 417,218 ms).

The B03 gate is updated to require 32 migrations headed by migration 031 and
the PostgreSQL character lifecycle contract. Its machine-readable final report
is `artifacts/b03/b11-final-result.json`.

Local operations verification also applied migration 031 to the development
database and confirmed successful server startup.

## Rollback

Migration 031 is additive and must remain in migration history after
deployment. Do not remove its columns or indexes, rewrite its checksum, reset
`character_lifecycle_version`, or turn a tombstone back into an unversioned
hard delete.

Application rollback must keep the B11-aware active-row filters and tombstone
semantics, even if the secure handler is disabled. First drain sessions and
disable any purge entry point; keeping tombstones is the safe failure mode.
Do not deploy an unmodified pre-B11 binary while tombstones exist: it does not
understand `lifecycle_state`, may load a deleted row as playable, and retains
the former hard-delete behavior. A schema rollback that physically deletes
tombstones is not a supported recovery action.

The secure shim can be restored through its existing checksummed installer
rollback. With the PostgreSQL durable lifecycle executor active, removing the
shim makes client create/delete unavailable and fail closed; it does not expose
the weaker raw mutation path. Restore the shim to regain PostgreSQL
create/delete. Shim rollback does not undo already committed PostgreSQL
lifecycle rows or evidence.

## Known limits and B12 handoff

- The installed client supports only `SingleCharacterV1`; B11 does not add
  multiple character slots.
- Restore and purge are service-only. No player-facing UI, GM endpoint, or
  automatic purge scheduler is introduced.
- A deleted character's globally unique name stays reserved until purge.
  Product policy for name reuse after purge still needs explicit review.
- An active replacement blocks restoration of an older tombstone.
- PostgreSQL raw TCP create/delete is unavailable while the durable executor is
  active. The JSON raw local path remains a compatibility mode without durable
  cross-reconnect command identity.
- Shim pending entries and resolved tombstones are bounded in-process state
  (16 entries in each pool with a 10-minute lifetime). A resolved tombstone
  retains only operation ID and family; it deliberately cannot recover or
  match the completed intent. A complete client-process restart or expired
  pending entry also cannot recover the previous UUID from disk. A fresh UUID
  safely receives current business state, but cannot replay an old success
  receipt by intent alone.
- Lifecycle idempotency does not replace B15's full player-ownership fence for
  valuable transactions across multiple server processes.
- B11 records lifecycle outbox events, but B13 exporter/dashboard work and
  B19 reconciliation/restore operations remain pending.
- Physical purge depends on existing foreign-key cascades. It must remain a
  controlled, observable service operation with backup and audit retention.

The next dependency-ordered ticket is B12. It should apply the same durable
identity, PostgreSQL transaction, receipt, revision, and outbox principles to
progression rewards and pet mutations without coupling those systems to the
network transport.
