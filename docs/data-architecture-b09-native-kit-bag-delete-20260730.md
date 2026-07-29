# B09 secure native kit-bag item-delete increment

Date: 2026-07-30

Source base: `fae256034f5eaccdbb319a4aa10e0d292188a8de`

Status: increment implemented and verified; B09 remains open

## Outcome

Dragging an item from the kit bag onto the ground and confirming deletion now
has a secure, replay-safe path:

```text
stock StorageItem ground-delete
  -> family-13 native operation UUID
  -> authenticated TLS 0x0101 marker
  -> replay lookup before current-slot capture
  -> bounded application command envelope
  -> locked PostgreSQL item-delete transaction
  -> stock delete acknowledgement + authoritative bag refresh
  -> authenticated family-13 terminal 0x0102 result
```

The client proposes only a kit-bag slot. The server captures the exact
authoritative compact item state and binds it to the command before
persistence. The transaction deletes only the same locked item instance in
the same account-owned character and slot. A retry therefore cannot delete a
different item that was later placed into the reused slot.

An already-empty slot and a slot whose item no longer matches the captured
state are permanent, audited non-mutating results. They do not advance the
inventory revision or publish an inventory event.

No schema migration is required. The command uses the permanent inbox,
economy audit, inventory revision, immutable inventory ledger, strict outbox,
and opening economy baseline introduced by migrations 025 through 028.

## Exact packet and native identity

Kit-bag item deletion uses secure command family `13`.

The shim recognizes only this exact stock-client shape:

- opcode `10052`;
- exact 28-byte framed packet;
- source page at full-packet offset `8`, in the range zero through three;
- source page index at offset `10`, in the range zero through 23; and
- destination page and index at offsets `12` and `14`, both `0xFFFF`.

The canonical bag slot is `page * 24 + index`, in the range zero through 95.
Other opcode-10052 shapes, including kit-bag moves and equipment transfers,
remain tokenless compatibility traffic. Scratch bytes outside the validated
fields neither create nor alter delete identity.

The retry key contains the authenticated principal fingerprint,
authenticated character, family 13, and canonical kit-bag slot. An exact
duplicate reuses its UUID across a reconnect for the same principal and
character. A different slot receives a different UUID. A terminal
Applied, Replayed, Rejected, or Conflict result settles the pending entry and
leaves a bounded tombstone so duplicate terminal results are harmless.

The native registry remains fixed at 16 pending and 16 resolved entries, with
a ten-minute operation lifetime. Missing principal or character state,
capacity exhaustion, random failure, clock failure, malformed length, invalid
page/index, and non-delete destinations cannot produce a family-13 operation
marker.

## Application contract

`KitBagItemDeleteCommandEnvelope` accepts only authenticated
`SecureTlsLegacy` or `SecureCommand` provenance. Its canonical version-1
request binds:

- the server-derived account and character;
- the client operation UUID;
- one kit-bag slot from zero through 95; and
- the exact canonical compact item state captured by the server.

The expected state is strict UTF-8, bracketed, free of control characters,
and bounded to 512 bytes. Infrastructure reparses it through
`CompactItemEntry` and requires a byte-for-byte canonical representation
before opening the mutation path. The operation UUID forms the operation
scope; the slot and complete expected state form the request hash. Reusing
the same UUID with different canonical content through the execution path
produces a permanent request-hash conflict rather than another deletion.

The durable receipt records the character, slot, one finite status
(`Deleted`, `EmptySlot`, or `StaleSelection`), expected and authoritative
compact states, inventory revision, audit reference, and optional strict
outbox event ID. Only `Deleted` may carry an outbox identity and an advanced
revision.

## PostgreSQL transaction

`PostgresKitBagItemDeleteCommandExecutor` performs one transaction:

1. validate secure provenance, bounds, hashes, and canonical compact state;
2. lock the character row and verify account ownership;
3. check the permanent family-13 inbox before reading current bag state;
4. replay the stored receipt or record a same-UUID request-hash conflict;
5. ensure the opening character-economy baseline;
6. lock the requested kit-bag row and capture its item instance and complete
   database state;
7. compare the exact authoritative compact state with the command snapshot;
8. for an empty or stale slot, persist audit and inbox evidence only;
9. for an exact match, persist the canonical `Deleted` audit and inbox
   evidence with its next inventory revision and event ID;
10. delete exactly the locked item instance from exactly the requested slot
    and append the compatibility `character_item_audit` row;
11. advance `inventory_revision` exactly once with an optimistic revision
    predicate;
12. append one immutable `delete` inventory-ledger transition from the full
    item row to null, with reason `client_ground_delete`;
13. append one strict `inventory.kit_bag_item_deleted` outbox event; and
14. commit before returning a terminal result.

Empty-slot and stale-selection results commit their permanent audit and inbox
receipt without deleting an item, advancing the revision, appending a
mutation-ledger row, or publishing an event. Because that non-mutating result
is permanent, a later item placed into the slot remains safe from the old
operation UUID.

The transaction requires exactly one item deletion, compatibility audit,
revision advance, ledger append, and outbox insert for a successful command.
Any cardinality mismatch fails the transaction.

## Replay, response order, and uncertainty

Every secure delete first calls `TryReplayAsync`. This ordering is essential:
the original attempt may already have deleted the item, so rereading the
current empty slot first would change a committed success into a different
intent. Only a replay miss captures the current in-memory item state and
creates a new command envelope.

For a durable deletion, the handler sends:

1. stock opcode `10052` delete acknowledgement;
2. a complete authoritative kit-bag refresh; and
3. authenticated family-13 result `0x0102` last.

For durable `EmptySlot` or `StaleSelection`, the stock delete
acknowledgement is omitted, followed by the authoritative bag refresh and a
Rejected family-13 result. Exact replay reports Replayed while preserving
the original receipt and revision. A request-hash conflict or other
definitive non-durable rejection refreshes the bag and sends Conflict or
Rejected with revision zero.

The PostgreSQL projection reload replaces only the live character's kit-bag.
It does not overwrite position, vitals, equipment, Silver, mount state, or
other runtime ECS state. It also clears stale Forge, Gear Enhancer, and
unequip-follow-up selections after a successful reload.

Provider unavailability, cancellation, an uncertain commit result, or a
failed authoritative projection reload emits no stock terminal
acknowledgement and no `0x0102`. The native UUID remains pending so the next
retry can discover the permanent inbox result instead of guessing whether
the item was deleted.

Tokenless raw legacy deletion retains the prior compatibility path and is
counted as unsupported legacy identity. It does not gain cross-reconnect
idempotency.

## Verification

The final frozen tree passed:

- `dotnet build GodswarServer.sln --no-restore -c Release`: zero warnings
  and zero errors;
- the complete managed protocol harness: 214 passed, zero failed;
- focused family-13 command-contract, handler/replay, outbox-consumer, and
  disposable PostgreSQL transaction checks;
- success, exact and concurrent replay, request conflict, wrong owner,
  empty-slot, stale-selection, late replacement-item safety, rollback, and
  commit-uncertainty transaction cases;
- the strict Win32 Release network-shim build with `/W4 /WX`;
- native offline checks and the complete native check suite; and
- the mandatory B03 PostgreSQL 17 gate, including the focused disposable
  kit-bag delete transaction and all migration scenarios.

The B03 run took 272,340 ms. It applied and verified all 29
migrations through `20260729_028_economy_ledger_hardening`. The gate passed
28 of 28 required checks and all three migration scenarios, with cleanup
passed. The machine-readable report is
`artifacts/b03/b09-native-kit-bag-delete-result.json`
(12,473 bytes, SHA-256
`084E544516F53800B584FD640AA09A8969BD77C9052500D71568F5C2965DC5B4`).
No `godswar_b03_*` database remained after the gate.

The rebuilt native artifacts were:

- `Net.dll` SHA-256
  `5348E47D3341F0B9B5C6488A2615E296F86342D3E2285BABD505D0652DB0A75B`;
  and
- `Godswar.NetShim.Checks.exe` SHA-256
  `1C2638B356D17C4FC18DB915C5489E2F9CE187BAF97E6686BADB42E0FD737466`.

## Rollback and remaining B09 work

Rollback requires a matched prior server and shim. Permanent family-13 inbox,
audit, ledger, compatibility-audit, and outbox rows are valid economy history
and must remain.

The native retry identity lasts ten minutes while the PostgreSQL inbox is
permanent. Only a client retaining the original UUID can address that
permanent receipt after reconnect; dragging a currently present item out
after settlement is a new intent and receives a new UUID.

The stock packet exposes only a slot, and the current compact item projection
does not contain the PostgreSQL item-instance ID. A delayed first execution
therefore cannot distinguish an item that was removed and replaced in the
same slot by a byte-for-byte identical item (an ABA replacement). Items with
different compact state and all retries after a durable result remain
protected. Eliminating this residual limitation requires a controlled client
projection and command contract carrying an authenticated durable
item-instance token. A hostile secure client that reuses one UUID for another
slot is non-mutating and remains unresolved at the handler boundary rather
than being counted as a request-hash conflict; the stock shim does not
generate that shape.

B09 remains open after this increment. Tokenless kit-bag move/swap,
equip/unequip, Holy Stone operations, other inventory mutations, rewards,
and remaining currency compatibility paths still require truthful
operation-specific identity and durable authoritative transactions.
