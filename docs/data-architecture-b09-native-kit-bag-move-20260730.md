# B09 secure native kit-bag move/swap increment

Date: 2026-07-30

Source base: `3844238791402ec07a433cb840b2087e6434e279`

Status: increment implemented and verified; B09 remains open

## Outcome

Dragging an item between two kit-bag slots now has a secure, replay-safe
authoritative path:

```text
stock StorageItem kit-bag move
  -> family-14 native operation UUID
  -> authenticated TLS 0x0101 marker
  -> permanent replay lookup before current-slot capture
  -> bounded application command envelope
  -> locked PostgreSQL move-or-swap transaction
  -> stock move acknowledgement + authoritative bag refresh
  -> authenticated family-14 terminal 0x0102 result
```

The client proposes an ordered source and destination slot. PostgreSQL
decides whether the operation is a move into an empty slot or a swap with an
occupied slot. A move preserves the source item-instance ID; a swap preserves
both item-instance IDs. Both forms advance the character's inventory revision
once and commit all durable evidence before the client receives success.

An empty source or a source/destination whose compact state no longer matches
the server-captured intent is a permanent, audited, non-mutating outcome. It
does not advance the revision or publish an inventory event.

No schema migration is required. The command uses the permanent inbox,
economy audit, inventory revision, immutable inventory ledger, strict outbox,
and opening economy baseline introduced by migrations 025 through 028.

## Exact packet and native identity

Kit-bag movement uses secure command family `14` and stock opcode `10052`.
The shim recognizes only two exact framed shapes:

- a 20-byte compact packet whose source page/index are at full-packet offsets
  `8`/`10`, destination page/index are at `12`/`14`, and offsets `16`/`18`
  are both `0xFFFF`; or
- an 80-byte detailed packet with the same four coordinates and an opaque
  stock-client tail.

Pages are limited to zero through three and page indices to zero through 23.
The canonical slot is `page * 24 + index`, in the range zero through 95.
The source and destination must be distinct for a durable operation.

The 80-byte form is supported by live captures, including both occupied-slot
and empty-slot movements. The 20-byte form is retained from managed and
synthetic compatibility evidence; no live 20-byte capture has yet been
identified. Exact sizes prevent the 28-byte ground-delete shape and equipment
transfer shapes from acquiring a move UUID. The detailed tail is deliberately
excluded from identity because live stock-client captures contain varying
opaque scratch/pointer bytes.

The retry key contains the authenticated principal fingerprint,
authenticated character, family 14, and the ordered source/destination pair.
Equivalent 20-byte and 80-byte requests reuse one UUID, including across a
reconnect. Reversing the pair or changing either slot produces a different
UUID. The registry remains bounded at 16 pending and 16 resolved entries with
a ten-minute lifetime.

Malformed, same-slot, unsupported-length, missing-principal, and
missing-character requests cannot create family-14 identity. If a hostile
secure peer nevertheless supplies a UUID with an unsupported frame, the
managed handler rejects it and returns before the legacy mutation path. This
prevents a secure-to-tokenless downgrade.

## Application contract

`KitBagItemMoveCommandEnvelope` accepts only authenticated
`SecureTlsLegacy` or `SecureCommand` provenance. Its canonical version-1
request binds:

- the server-derived account and character;
- the client operation UUID;
- the ordered source and destination kit-bag slots; and
- the server-captured compact state of both slots.

Each compact state is strict UTF-8, bracketed, free of control characters,
and limited to 512 bytes. Infrastructure reparses it through
`CompactItemEntry` and requires its byte-for-byte canonical representation.
The state digest uses explicit source/destination role tags and length
prefixes so concatenation, state swapping, and boundary ambiguity cannot
produce an equivalent request.

The durable receipt records the character, ordered slots, expected and
authoritative states, one finite result (`Moved`, `Swapped`, `EmptySource`,
`StaleSource`, or `StaleDestination`), revision, audit reference, and
optional outbox event ID. Only `Moved` and `Swapped` may carry an event ID and
an advanced revision.

## PostgreSQL transaction

`PostgresKitBagItemMoveCommandExecutor` performs one transaction:

1. validate secure provenance, bounds, hashes, and both canonical states;
2. lock the account-owned character row and capture its inventory revision;
3. check the permanent family-14 inbox before rereading either bag slot;
4. replay the stored receipt, or commit conflict evidence if the same UUID is
   presented for another ordered pair or request hash;
5. ensure the opening character-economy baseline;
6. lock both requested bag rows in ascending slot order;
7. resolve empty/stale outcomes from the exact locked compact states;
8. for a terminal rejection, commit audit and inbox evidence only;
9. for movement, persist canonical inbox/audit evidence for the next revision;
10. append one compatibility audit for each item that will move;
11. move one item directly, or swap two items through a private negative
    location-2 slot while matching the exact locked row ID and old position on
    every update;
12. preserve each item-instance ID and capture its full authoritative JSON
    after-state;
13. advance `inventory_revision` exactly once with an optimistic predicate;
14. append one ordered `move` ledger entry for a move, or two entries at the
    same revision for a swap;
15. append one strict `inventory.kit_bag_item_moved` outbox event; and
16. commit before returning success.

Every position update also advances `updated_at`. Exact affected-row checks
protect every compatibility audit, movement leg, revision, ledger entry, and
outbox insert. Any failure rolls the entire transaction back, including the
temporary swap location.

Locking the character row serializes inventory mutations for that character.
Ordering the two item-row locks additionally keeps the pair deterministic.
The temporary slot is private to item location 2, uses a negative `smallint`
value, and is selected only when absent for that character.

The inventory ledger stores complete before/after row JSON and the exact
item-instance ID. A normal move produces ordinal zero; a swap produces
ordinals zero and one under the same inventory revision. Reconciliation views
therefore see both item locations without treating a swap as two independent
player commands.

## Replay, response order, and uncertainty

Every secure movement calls `TryReplayAsync` before reading either current
slot. This is essential for swaps: after the first commit, both live states
are reversed, so reading them first would reinterpret a retry as a new or
stale command.

For a newly committed `Moved` or `Swapped` result, the handler sends:

1. the stock opcode `10052` move acknowledgement;
2. a complete authoritative kit-bag refresh; and
3. the authenticated family-14 Applied result last.

An exact duplicate does **not** receive another stock move acknowledgement.
That acknowledgement is non-idempotent in the stock UI and could visually
swap the two slots back. Instead, replay sends the full bag refresh followed
by the authenticated Replayed result.

Durable empty/stale outcomes and definitive non-durable rejections omit the
stock acknowledgement, reconcile the bag, and send Rejected or Conflict.
The PostgreSQL reload replaces only the live character's kit bag and clears
bag-dependent unequip, Forge, and Gear Enhancer selections; it does not
overwrite position, vitals, equipment, currencies, mounts, or other runtime
ECS state.

Provider failure, cancellation, uncertain commit, invalid stored receipt, or
failed projection reload emits no terminal family-14 result. The native UUID
remains pending so a retry can discover the permanent inbox result instead of
guessing whether movement committed.

## Verification

The final frozen tree passed:

- `dotnet build GodswarServer.sln --no-restore -c Release`: zero warnings and
  zero errors;
- the focused family-14 contract, handler/replay, and shared outbox checks;
- the complete managed protocol harness: 217 passed, zero failed;
- focused disposable PostgreSQL success, swap, replay, conflict, stale-state,
  ownership, fault-injection, commit-uncertainty, and reconciliation checks;
- the strict Win32 Release network-shim rebuild with `/W4 /WX`;
- native offline checks and the complete native check suite; and
- the mandatory B03 PostgreSQL 17 gate, including the focused disposable
  family-14 transaction and every migration scenario.

The B03 run took 388,268 ms. It applied and verified all 29 migrations
through `20260729_028_economy_ledger_hardening`. The gate passed 29 of 29
required checks and all three migration scenarios, with cleanup passed. The
machine-readable report is
`artifacts/b03/b09-native-kit-bag-move-result.json` (12,858 bytes, SHA-256
`29C8EDD98D0FFAB26825403BE538ECFE7B743A1668716197907C93D7AA919A32`).
No `godswar_b03_*` database remained after the gate.

The rebuilt native artifacts were:

- `Net.dll` SHA-256
  `9E1AA27A28A0BDAC3A85796761A92B576E0CA96736D497DE760EB96E2BEF9821`;
  and
- `Godswar.NetShim.Checks.exe` SHA-256
  `ABD6680C5A111CF04E2396E7C7711FF1117513AF5488EBE39310D49D2AE9191A`.

Three independent read-only reviews found no blocking implementation,
routing, transaction, or test-coverage defect. The reviews also confirmed
that every changed/new file remains below 20 KiB and 600 lines.

## Rollback and remaining B09 work

Rollback requires a matched prior server and shim. Permanent family-14 inbox,
audit, ledger, compatibility-audit, and outbox rows are valid economy history
and must remain.

Tokenless raw legacy moves retain the old compatibility path so the stock
client can still operate before raw transport retirement. They do not gain
cross-reconnect idempotency, the new ledger/revision guarantees, or a strict
outbox event. A recognized tokenless move on a secure connection now fails
closed with an authoritative bag refresh and no mutation; it cannot bypass
the family-14 inbox.

The compact stock projection does not expose the PostgreSQL item-instance ID.
A delayed first execution therefore cannot distinguish an item removed and
replaced in a slot by a byte-for-byte identical item (the legacy ABA problem).
Different-state replacement and every retry after a durable result are
protected. Eliminating the remaining ABA limitation requires a controlled
client projection and command contract carrying an authenticated durable
item-instance token.

Useful nonblocking regression additions are literal captured 80-byte golden
vectors, an explicit 21-byte rejection, identical-compact-state swap rows, a
race between several first executions, and destination-side late replacement.

B09 remains open after this increment. Equip/unequip, Holy Stone operations,
rewards, and remaining inventory and currency compatibility paths still need
truthful operation-specific identity and authoritative durable transactions.
