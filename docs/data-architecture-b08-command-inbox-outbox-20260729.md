# B08 PostgreSQL command inbox/outbox foundation

Date: 2026-07-29
Roadmap dependency: B03 and B07
Next roadmap ticket: B09 - inventory/currency ledger migration

## Outcome

B08 makes the PostgreSQL talent-upgrade slice durable across disconnects,
process restarts, and lost acknowledgements. One database transaction now
contains:

```text
authenticated talent-upgrade intent
  -> lock and validate the authoritative character/talent state
  -> read or create the command identity
  -> immutable command audit and stored result
  -> talent rank increment and server-calculated point debit
  -> versioned outbox event
  -> commit
  -> client acknowledgement
```

An exact duplicate reads the canonical stored receipt and does not repeat the
rank mutation, point debit, audit append, or outbox append. A transaction that
fails before commit leaves none of those rows behind. A failure after commit
can be retried through a new executor/process and returns the original result.

This is command-mutation idempotency, not an exactly-once event-delivery
claim. Outbox consumers remain at-least-once and must be idempotent. The first
talent consumer validates the committed event contract and identity; it does
not create a second durable player-value authority or an external projection.

## Schema release

Migration
`20260729_025_command_inbox_outbox_foundation` adds:

- `character_talents.outbox_revision`, a non-negative per-talent aggregate
  revision;
- `command_audit`, the permanently retained and immutable command audit;
- `command_inbox`, the permanent operation identity, request hash, canonical
  result, result hash, audit reference, and bounded duplicate/conflict
  evidence;
- `outbox_events`, the immutable event identity/payload plus mutable bounded
  lease, retry, delivered, and poison state;
- `outbox_consumer_positions`, the durable per-consumer/per-aggregate
  checkpoint and at-most-one in-flight lease.

Database constraints bound identifiers, digests, JSON objects, versions,
attempt counts, timestamps, terminal states, lease tuples, and retention
policy. Audit rows cannot be updated or deleted. Inbox identity and result
fields cannot change or be deleted; only their monotonic, capped duplicate and
request-conflict evidence may advance. Outbox identity and payload cannot
change or be deleted, while delivery state advances under guarded leases.
Consumer checkpoints cannot move backwards.

Audit, inbox, and outbox rows are linked with restrictive foreign keys. The
operation and event uniqueness constraints make conflicting inserts fail
inside the same authoritative transaction rather than becoming a second
write path.

Forward migration
`20260729_026_command_inbox_outbox_hardening` preserves the already-pinned
foundation checksum and closes database/application validation gaps. It
rejects the empty UUID for event identities, rejects control characters in
all command/outbox aggregate keys, and makes event/position inserts start in a
clean pending/zero state. Terminal events and active lease identities become
immutable; attempts and checkpoints may advance only through their exact
state-machine transitions; and consumer-position deletion remains forbidden.

Migration 026 also performs a fail-closed preflight over state left by
migration 025. Deferred constraint triggers then validate event/position lease
parity at transaction commit, allow the dispatcher to update the two rows in
either statement order, and require every delivered revision to be supported
by its durable consumer checkpoint. The preflight deliberately accepts a
valid stream whose older revision is delivered while a newer revision is
already leased.

## Talent transaction boundary

`PostgresTalentUpgradeCommandExecutor` implements
`ITalentUpgradeCommandExecutor` directly over the extracted application
contract. It:

1. validates the B07 envelope and canonical digest;
2. locks the account-owned `character_base` row;
3. reads the durable inbox by principal, aggregate, command family, and
   operation ID;
4. returns the verified stored receipt for an exact request-hash match, even
   if the character's profession changed after the original commit;
5. for a new operation, proves that the requested talent belongs to the
   character's current server-owned profession, then validates expected rank,
   rank cap, level, and persisted points;
6. inserts the audit and inbox result;
7. updates `character_talents`, its outbox revision, and
   `character_base."SkillPoint"`;
8. inserts the strict-sequence talent event; and
9. commits before the handler sends success.

The result payload is a versioned, bounded JSON object. Its SHA-256 result
hash is verified before replay, and the stored audit reference must match the
inbox foreign key. The receipt carries the character/talent identity, rank,
cost, remaining points, display value, aggregate revision, audit reference,
and event ID.

Precondition failures do not consume the transition identity. The same
expected-rank operation may therefore succeed later after its level or point
precondition becomes true. A duplicate of an older successful operation still
returns its original receipt, then the handler sends current authoritative
rank/status correction when later upgrades have advanced the live character.

## Exact duplicate and conflict semantics

The B07 legacy operation identity and request hash are both derived from the
same validated talent transition. For a correctly constructed version-1
legacy envelope, the same operation ID with a different request hash is not a
normal client action: changing the meaningful talent/rank intent also changes
the operation ID.

The durable request-hash conflict branch remains deliberate defense in depth.
It protects against a faulty future adapter, contract-version mistake,
corrupt or manually inserted rows, and an internally inconsistent envelope.
It increments bounded conflict evidence and rejects the operation; it is not
advertised as a routine legacy-client retry outcome.

The inbox is permanent for this slice. An old committed operation therefore
cannot become new after an expiry window. PostgreSQL, not the process-local B07
registry, decides whether the command already committed.

## Durable dispatcher and ordering

`PostgresOutboxDispatcher` registers a bounded set of named consumers and
supports both persisted ordering policies:

| Database policy | Application policy | Behavior |
| --- | --- | --- |
| `strict` | `StrictSequence` | deliver only `current + 1`; defer a gap; mark an already-applied revision stale |
| `latest_wins` | `VersionedState` | deliver any newer version and advance the checkpoint; mark an older/equal version stale |

The talent stream uses `strict`. Policy disagreement between the registered
consumer, event row, and durable position fails closed.

Each claim transaction uses locked event and position rows with
`SKIP LOCKED`. A dispatch pass has a bounded work budget, but deliberately
leases only one event immediately before its callback. This avoids starting a
whole batch of lease clocks while later events wait behind a slow consumer.
Registered-stream position creation plus policy/lease validation run once per
bounded pass rather than once per callback.
The callback runs without an open database transaction. Only a successful
callback advances the durable checkpoint and marks the event delivered in one
completion transaction.

The resulting delivery guarantee is:

- concurrent pollers cannot hold the same stream position simultaneously;
- strict gaps are delayed without calling the consumer;
- stale events are completed without replaying their side effect;
- consumer exceptions and timeouts schedule bounded exponential retry;
- attempt exhaustion moves the event to a durable poison state;
- process cancellation after claim intentionally leaves the lease for normal
  expiry recovery;
- cancellation of the supervised runner is a normal graceful stop rather than
  a host fault;
- a crash after consumer success but before checkpoint may redeliver, so
  consumers must tolerate at-least-once delivery;
- expired event and position leases are recovered together, then retried or
  poisoned according to the durable/effective attempt limit.

There is no relative ordering assumption between TCP, UDP, command
acknowledgements, and outbox delivery. Aggregate revision and durable consumer
position are the ordering authority.

## Runtime composition and rollback

`PostgresApplicationDataRuntime` owns one shared Npgsql data source for the
extracted character snapshot reader, talent command executor, and outbox
dispatcher. The legacy broad PostgreSQL store still owns its existing pool
until later slices remove it.

`Program.cs` supplies the PostgreSQL executor to raw-development and secure
game handlers. The dispatcher joins the server's supervised runtime tasks; an
unexpected dispatcher fault cancels the shared shutdown token instead of
silently leaving a dead background worker.

Dispatcher settings are bounded and validated through
`Storage.Outbox`, with matching `GODSWAR_OUTBOX_*` environment overrides for
enablement, pass budget, poll interval, lease, attempt limit, retry delays,
gap delay, and command timeout. The lease must be longer than the callback
timeout.

The operational rollback is to disable the dispatcher. This stops callbacks
but deliberately keeps PostgreSQL talent mutation, audit, inbox result, and
event append active; pending events remain durable for later recovery. A code
rollback must retain a binary compatible with migrations 025 and 026 and must
not delete or rewrite their permanent evidence rows.

Rolling old and new game-server binaries together is not safe for this talent
slice. An older binary can still reach the legacy PostgreSQL talent mutation
without creating inbox/outbox evidence. Deploy by draining the old process and
using one compatible release for the owning player population. B15's durable
owner generation and completion of the remaining mutation migrations are
required before mixed-version ownership can be designed safely.

## JSON compatibility boundary

JSON remains an explicit local-development compatibility provider. It
continues through the B07 expected-rank and process-local attempt path and
does not gain a durable inbox, canonical stored-result replay, outbox, or
cross-process idempotency guarantee.

`PostgresApplicationDataRuntime` is not composed under JSON storage, and the
startup log reports that the PostgreSQL outbox is unavailable. This is a
documented limitation, not silent fallback from a failed PostgreSQL command.

## Observability

`PostgresCommandMetrics` emits only bounded family, consumer, outcome, and
reason labels:

- `godswar_command_inbox_transactions_total`;
- `godswar_command_inbox_transaction_duration_ms`;
- `godswar_outbox_events_total`;
- `godswar_outbox_retries_total`;
- `godswar_outbox_poison_total`;
- `godswar_outbox_sequence_gaps_total`;
- `godswar_outbox_dispatch_duration_ms`;
- `godswar_outbox_backlog`;
- `godswar_outbox_oldest_age_seconds`.

Player/account IDs, operation IDs, event IDs, aggregate keys, request/result
hashes, payloads, endpoints, and credentials are not metric labels.

## Repository evidence

| Concern | Repository location |
| --- | --- |
| Migration registration and bounded schema | `src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.cs`; `PostgresSchemaMigrationCatalog.CommandInboxOutbox.cs`; `PostgresSchemaMigrationCatalog.CommandInboxOutbox.Guards.cs`; `PostgresSchemaMigrationCatalog.CommandInboxOutboxHardening.cs`; `PostgresSchemaMigrationCatalog.CommandInboxOutboxHardening.LeaseConsistency.cs` |
| Provider-neutral talent command contract | `src/Godswar.Server/Application/Talents/ITalentUpgradeCommandExecutor.cs`; `TalentUpgradeExecutionResult.cs` |
| Provider-neutral event and ordering contracts | `src/Godswar.Server/Application/Messaging/IOutboxEventConsumer.cs`; `OutboxEventMessage.cs`; `OutboxOrdering.cs` |
| Atomic PostgreSQL command transaction | `src/Godswar.Server/Infrastructure/Talents/PostgresTalentUpgradeCommandExecutor.cs`; `.Inbox.cs`; `.Mutation.cs` |
| Canonical result/event codec and first consumer | `src/Godswar.Server/Infrastructure/Talents/TalentUpgradePersistenceCodec.cs`; `TalentUpgradeOutboxConsumer.cs` |
| Durable dispatcher | `src/Godswar.Server/Infrastructure/Messaging/PostgresOutboxDispatcher.cs`; `.Claims.cs`; `.Completion.cs`; `.Recovery.cs`; `.Models.cs` |
| Bounded settings and metrics | `src/Godswar.Server/Infrastructure/Messaging/PostgresOutboxDispatcherOptions.cs`; `PostgresCommandMetrics.cs`; `src/Godswar.Server/ServerOptions.cs` |
| Shared extracted-data runtime and supervision | `src/Godswar.Server/Infrastructure/PostgresApplicationDataRuntime.cs`; `src/Godswar.Server/Program.cs` |
| Handler acknowledgement/replay correction | `src/Godswar.Server/Game/GameClientHandler.Talents.cs` |
| Migration contract coverage | `tests/Godswar.Server.ProtocolChecks/PostgresInboxOutboxMigrationChecks.cs` |
| Atomic command, replay, race, and fault coverage | `tests/Godswar.Server.ProtocolChecks/PostgresTalentInboxOutboxIntegrationChecks*.cs` |
| Dispatcher state-machine and schema-hardening coverage | `tests/Godswar.Server.ProtocolChecks/PostgresOutboxDispatcherIntegrationChecks.SchemaHardening.cs`; `.SchemaStateMachine.cs` |
| Mandatory PostgreSQL smoke registration | `tests/Godswar.Server.ProtocolChecks/Program.cs`; `tools/InvokeB03PostgresCiGate.ps1` |

## Verification scope

The B08 verification set covers:

- migration presence, schema bounds, indexes, foreign keys, and immutable
  guards;
- forward hardening for empty event IDs, control-character aggregate keys,
  clean inserts, immutable terminal/active-lease state, exact attempt and
  checkpoint transitions, undeletable positions, migration preflight,
  deferred lease parity, and delivery/checkpoint coupling;
- first commit and canonical stored-result replay through a new executor;
- exact stored-result replay after a profession change while new ineligible
  operations remain rejected;
- two concurrent executors producing one mutation and one event;
- invalid ownership/intent, stale rank, insufficient level, and insufficient
  points without consumed inbox identity;
- recovery after those preconditions change;
- rollback at each pre-commit probe;
- lost acknowledgement simulated immediately after commit;
- dispatcher strict and latest-wins ordering, stale and gap handling;
- concurrent pollers, bounded retries, poison handling, and expired-lease
  recovery;
- consumer-success-before-checkpoint redelivery;
- graceful supervised-runner cancellation;
- architecture boundary, configuration, and changed-file-size gates.

## Verification receipts

Completed on 2026-07-29:

```text
Release solution build                         PASS, 0 warnings / 0 errors
PostgreSQL migration safety foundation          PASS, 1 / 1
Talent inbox/outbox PostgreSQL integration      PASS, 1 / 1
Dispatcher recovery/ordering integration        PASS, 1 / 1
Full protocol suite                             PASS, 195 / 195
B03 mandatory PostgreSQL 17 gate                PASS, 17 required checks
B03 migration scenarios and cleanup             PASS, 3 / 3; cleanup passed
Architecture ratchet                            PASS, 0 new/stale/rule violations
Changed-file maintainability limit              PASS, 0 over 20 KB or 600 lines
Local godswar schema release 026                PASS, 26 -> 27 migrations
```

The machine-readable B03 receipt is
`artifacts/b03/postgres-ci-result.json`, SHA-256
`7098DFC9C28B336A9B2E40C2A944D078EA4ABA212D46D8155E0F25CB1AEE5601`.
It records PostgreSQL 17.9, 27 migrations, migration head
`20260729_026_command_inbox_outbox_hardening`, 17 required checks, all three
migration scenarios, and successful disposable-database cleanup. The receipt
records source base `cc7f87f5c0c518622ac845d147ae33251e0b6201` and was
produced from the B08 working tree before this evidence document was finalized.

The pinned SQL checksums are:

- migration 025:
  `7213DCEE445B6D577C0281E22242EDA1EA84E72167B3DB8631C8F86102D3D007`;
- migration 026:
  `8BA9B0B0136429140610FEC13BC58F8DD45CD1C6306317FB849271163EB59709`.

## Limitations and next dependency

- Only talent upgrade uses the durable command transaction. Inventory,
  currency, forge, pet, reward, and lifecycle commands do not inherit its
  guarantees automatically.
- The first talent consumer validates and checkpoints the event but has no
  external projection side effect yet.
- At-least-once callback delivery requires every future consumer to implement
  destination-side idempotency.
- Poison events are retained and observable; automated repair/reconciliation
  remains B19.
- B08 deliberately adds no delivered/poisoned outbox pruning, partitioning,
  or archive job. Permanent inbox/audit evidence and retained outbox rows are
  durable, not bounded storage. Measure command/event volume and approve a
  retention/archive design before high-volume rollout; any future policy must
  preserve the rule that an old operation cannot become new after evidence is
  moved.
- Registered-stream validation is reduced to once per polling pass, but it
  still scans pending streams and the candidate index has not been benchmarked
  against a production-sized backlog. Capture query plans and a reproducible
  backlog/load baseline before high-volume rollout.
- Position/vitals checkpoint workers and stale-save rejection remain B10.
- The executor locks authoritative PostgreSQL rows, but it does not establish
  a player-process ownership generation. B15's PostgreSQL ownership fence
  remains required before multiple server processes can safely own and mutate
  the same player.
- Redis and MongoDB are not introduced by this slice.

B09 can now move inventory/currency operations behind operation-specific
PostgreSQL transactions using this inbox/outbox foundation, while preserving
their own economy ledger and audit invariants.
