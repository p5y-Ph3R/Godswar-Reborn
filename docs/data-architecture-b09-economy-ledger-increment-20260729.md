# B09 economy ledger foundation and first durable inventory command

Date: 2026-07-29
Roadmap dependency: B08
Status: B09 increment complete; the full B09 migration remains open

## Outcome

This increment establishes the durable wallet/inventory evidence model and
migrates one bounded inventory operation through it. A developer material
grant carrying an explicit client operation UUID now follows:

```text
authenticated and allowlisted `/item add ... op=<UUID>` intent
  -> validate the canonical command envelope
  -> lock the account-owned character aggregate
  -> create or verify the opening economy baseline
  -> replay the stored result or validate current bag capacity
  -> append immutable command audit and inbox result
  -> mutate authoritative `character_items`
  -> advance `character_base.inventory_revision`
  -> append before/after inventory ledger rows
  -> append a strict-sequence outbox event
  -> commit
  -> reload the current character projection
  -> refresh the client bag
```

The item mutation, aggregate revision, audit, inbox result, ledger entries,
and outbox event commit in one PostgreSQL transaction. An exact UUID/request
retry returns the stored receipt and does not grant the material twice. Reuse
of the UUID for a different item or quantity is a request-hash conflict.

This does not pretend that tokenless legacy commands are exactly-once.
Tokenless `/item add` remains a compatibility path and records the
`unsupported_legacy_retry` identity metric.

## Schema releases

Migration `20260729_027_economy_ledger_foundation` adds:

- non-negative `character_base.wallet_revision` and
  `character_base.inventory_revision`;
- non-negative, int32-bounded authoritative silver and gold checks;
- immutable opening wallet/inventory header snapshots in
  `character_economy_baseline`;
- immutable per-item opening snapshots in
  `character_inventory_baseline_items`;
- command-inbox-linked `character_currency_ledger` entries with checked
  arithmetic;
- command-inbox-linked `character_inventory_ledger` entries with bounded,
  versioned before/after JSON state; and
- exact baseline capture for all characters and item instances present at
  migration cutover.

Migration `20260729_028_economy_ledger_hardening` adds:

- validated location, slot, stack, experience, quality, grade, binding, and
  holy-socket domains for `character_items`;
- update/delete/truncate rejection for all baseline and ledger evidence; and
- report-only `character_wallet_reconciliation` and
  `character_inventory_reconciliation` views.

The persisted legacy bag domain remains `0..32767` because a required
historical B03 upgrade fixture contains a valid legacy row at slot 119. New
material grants independently enforce the actual 96-slot gameplay bag
boundary (`0..95`). This preserves upgrade compatibility without authorizing
new invisible-slot writes.

Opening evidence intentionally has no cascading character foreign key.
Deleting current player rows must not erase retained economy evidence. Ledger
rows instead have restrictive links to their command inbox and opening
baseline. Character lifecycle retention/purge policy remains B11.

## Runtime cutover behavior

Migration 027 captures every existing character. A character created by a
mixed-version server after that cutover may not yet have opening evidence.
The first tokenized inventory grant therefore creates the missing revision-0
wallet and per-item baseline inside the same locked transaction before it
mutates anything. It fails closed if an unbaselined character has already
advanced a durable economy revision.

The lazy cutover is intentionally located in the extracted PostgreSQL
application executor. It does not add another Npgsql call to the legacy
`State` layer and does not expand the architecture-ratchet debt.
Because mixed-version legacy delete paths do not all lock the character
aggregate, this rare missing-baseline path takes a PostgreSQL `SHARE` table
lock over `character_items` until baseline plus first mutation commit. That
blocks item DML but not readers, and is never taken once the character has a
baseline.

## Command identity and bounds

The optional final argument is:

```text
op=00000000-0000-0000-0000-000000000001
```

Only non-empty D-format UUIDs are accepted. The UUID is the operation scope.
The canonical request hash contains the item ID and quantity. Account and
character identity are always derived from the authenticated server session,
not from chat text.

The existing closed server material allowlist supplies binding and stack
limits. Quantity remains bounded to `1..999`, the active bag scan is bounded
to 96 slots, one command can append at most 256 ledger entries, and every
stored item-state document is bounded to 8 KiB.

## Application and infrastructure boundaries

Provider-neutral command types live under `Application/Inventory`:

- `DeveloperItemGrantCommand`;
- `DeveloperItemGrantCommandEnvelope`;
- `IDeveloperItemGrantCommandExecutor`;
- `DeveloperItemGrantExecutionResult`; and
- `DeveloperItemGrantExecutionReceipt`.

`PostgresDeveloperItemGrantCommandExecutor` owns the PostgreSQL transaction.
Its persistence codec stores and verifies a bounded canonical result. The
`DeveloperItemGrantOutboxConsumer` validates event identity and ordering but
does not create a second player-value authority.

The game handler composes the application executor only for PostgreSQL.
After commit or duplicate replay it reloads through the existing extracted
`ICharacterSnapshotReader`; it does not introduce a new broad-store read or
write. JSON remains a local compatibility provider and rejects the tokenized
durability claim rather than silently weakening it.

## Failure semantics

- Invalid envelope or non-allowlisted item: no durable command is consumed.
- Missing/wrong-owner character: no durable command is consumed.
- Insufficient bag capacity: no durable command is consumed.
- Failure at any pre-commit probe: item, revision, audit, inbox, ledger, and
  outbox changes all roll back.
- Failure immediately after commit: retrying the same UUID returns the
  original verified receipt and current bag state without another grant.
- Same UUID plus a different request: the conflict counter advances, no item
  changes.
- Concurrent exact retries: the character lock serializes the aggregate; one
  commits and the other returns duplicate.
- Outbox delivery: remains at-least-once with strict aggregate revisions;
  consumers must remain idempotent.

## Verification

Completed on 2026-07-29:

```text
Release solution build                         PASS, 0 warnings / 0 errors
Full protocol suite                            PASS, 197 / 197
Developer-grant PostgreSQL campaign             PASS, included in B03
B03 mandatory PostgreSQL 17 gate                PASS, 20 required checks
B03 migration scenarios and cleanup             PASS, 3 / 3; cleanup passed
Architecture ratchet                            PASS, 0 new/stale violations
Changed-file maintainability limit              PASS, 0 over 20 KB or 600 lines
Local godswar schema release                    PASS, 27 -> 29 migrations
Local opening reconciliation                    PASS, wallet 9/9; inventory 9/9
```

The three B03 scenarios reached 29 migrations and exact head
`20260729_028_economy_ledger_hardening`:

- fresh bootstrap: `0 -> 29`;
- restored historical prefix 008: `9 -> 29`; and
- current-schema idempotence: `29 -> 29`.

The machine-readable B03 receipt is
`artifacts/b03/b09-postgres-ci-result.json`, SHA-256
`E3C0E8142C99B10B969E85FB9872257C43FEF28B3355C5C612DFF9A5632391BC`.
It records PostgreSQL 17, 20 required checks, all three scenarios, and
successful disposable-database cleanup. It was produced from the B09 working
tree on source base `564be46aa205eaaeadfba87fa3949cd28192ee3f`.

The pinned migration checksums are:

- migration 027:
  `EBDC2A157F6D1900AB35BB61A68C4BE7F97F8B707B9A21EEA644EE64A754C05F`;
- migration 028:
  `1EDBA2FFF56F6A5DFB4C17B00BF909D68F915D8B8183ACA44100BACC8BF4B544`.

Before the local release, a PostgreSQL custom-format backup was written to
`artifacts/b09/reborn-b09-pre-migration.dump` (1,472,511 bytes), SHA-256
`8327DD2F341868F649784ED214FFC709C2AA8FC1DD68D295C868E0A781334E4E`.
The rebuilt server applied migrations 027/028 through normal startup and
published both raw local-development listeners. The immediate report-only
readback found all 9 current characters reconciled in both views.

## Remaining B09 work

B09 is not complete across the whole economy:

- ordinary inventory move/equip/delete/consume;
- forge and Gear Mentor add/enhance/delete;
- item decomposition/transformation/combination;
- mount/pet item operations;
- silver and gold debits/credits;
- zodiac activation and rewards; and
- remaining GM grants

still use legacy operation shapes without a trustworthy cross-reconnect retry
token and do not automatically inherit this ledger transaction.

Those paths must move one operation at a time behind application contracts.
For repeatable operations such as forge, a client/shim operation ID or another
protocol-compatible stable identity is required first. A server-generated
GUID or packet timestamp is not an acceptable substitute because it cannot
distinguish a retry after a lost acknowledgement from a new legitimate
attempt.

The reconciliation views are report-only during this staged migration. They
are expected to reveal drift caused by still-unmigrated legacy writers; they
must not auto-repair or conceal it. B19 owns bounded reconciliation and
repair tooling after the authoritative writers are migrated.

Migrations 027/028 were measured only against the small development and B03
fixtures. Migration 027 holds its schema change and opening baseline backfill
in one transaction; migration 028 immediately validates its item constraints.
On a production-sized inventory those operations can hold locks or scan for
longer than an acceptable deployment window. A production rollout therefore
requires measured row counts/lock duration and an approved quiesced window or
a follow-up split using add-`NOT VALID`, backfill, and `VALIDATE CONSTRAINT`.
The local verification results are not a production downtime guarantee.
