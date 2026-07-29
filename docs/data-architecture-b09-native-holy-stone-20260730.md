# B09 secure native Holy Stone increment

Date: 2026-07-30

Source base: `7ac594b46d95fe6e94d0a4ee002d244f31643d3a`

Status: implemented and verified; B09 remains open

## Outcome

Captured Holy Stone Mount, Remove, and basic Drill mutations now have
separate secure operation identities and authoritative PostgreSQL
transactions:

```text
stock Holy Stone Artisan action
  -> native family-16, family-17, or family-18 operation UUID
  -> authenticated TLS 0x0101 marker
  -> exact managed wire decoding
  -> permanent replay lookup
  -> bounded command envelope with captured item-state digests
  -> locked PostgreSQL inventory-and-wallet transaction
  -> current authoritative projection
  -> stock result
  -> authenticated terminal 0x0102 result
```

The client proposes a target reference, and Mount also proposes one bag
material reference while Remove proposes one socket ordinal. It does not
authoritatively choose the item, socket contents, material effect, output
slot, cost, balance, or outcome. Those values are reloaded and validated
inside the PostgreSQL transaction.

No whole-character or client-capped loadout snapshot is written back.
Successful operations update only the affected authoritative item rows,
revisions, ledgers, audit, inbox, and strict outbox evidence. Basic Drill
also updates the authoritative Gold balance.

## Captured packet and command families

All three mutations use stock opcode `10069` (`0x2755`) and an exact 92-byte
little-endian frame. The shared layout is:

| Full-packet offset | Width | Meaning |
|---:|---:|---|
| 0 | 2 | declared frame length, exactly 92 |
| 2 | 2 | opcode, exactly 10069 |
| 4 | 4 | Holy Stone Artisan interaction ID |
| 8 | 4 | dialog index, exactly 30 |
| 12 | 4 | duplicated dialog index, exactly 30 |
| 16 | 4 | action sub-ID |
| 20 | 72 | eighteen signed 32-bit action arguments |

The exact mutation shapes and secure families are:

| Operation | Sub-ID | Used arguments | Secure family |
|---|---:|---|---:|
| Mount | 101 | arg 0 = `0`; arg 6 = target; arg 7 = material | 16 |
| Remove | 201 | arg 6 = target; arg 10 = one-based socket 1..4 | 17 |
| basic Drill | 301 | arg 6 = target | 18 |

Every unused argument must be `-1`. Target references `100..195` mean kit-bag
slots `0..95`; captured reference `205` means the equipped weapon. A Mount
material must be a distinct kit-bag reference in `100..195`.

Literal clear-packet evidence is in
`captures/capture-proxy-20260514-173331.log`:

- lines 4152-4153: Remove from equipped reference 205, socket ordinal 1;
- lines 4203-4204: Mount page/navigation request with all arguments `-1`;
- lines 4251-4252: Mount to equipped reference 205 from material reference
  107;
- lines 6126-6127: basic Drill of bag reference 107;
- lines 8630-8631: Remove from bag reference 112, socket ordinal 1;
- lines 8789-8790: a second all-`-1` Mount navigation request;
- lines 8993-8994: Mount to bag reference 112 from material reference 109;
  and
- lines 11525-11526: a second basic Drill request with the same exact shape.

These captures carry Sparta interaction ID 5083. Athens interaction ID 5225
is the server-owned equivalent Artisan endpoint from the world-content
mapping; it is not represented as a second captured packet in this log.

## Tri-state native identity

`ClassifyLegacyHolyStonePacket` deliberately returns one of three states:

- `UnrelatedOrNavigation`: another packet, another NPC action, or the exact
  benign Mount request whose eighteen arguments are all `-1`;
- `Commit`: one exact Mount, Remove, or Drill mutation; or
- `InvalidMutation`: a Holy Stone mutation-shaped request with an invalid
  length, dialog duplication, action alias, reference, socket ordinal, or
  unexpected argument.

This distinction prevents a malformed valuable command from falling through
as ordinary traffic while preserving the stock page-navigation exchange.
Client-to-server sub-IDs `106`, `206`, `306`, and `406` are fail-closed
aliases: they are server response/page values historically accepted by the
legacy handler, not authenticated commit shapes.

Only `Commit` can acquire an operation UUID. `InvalidMutation` does not
allocate identity and is not forwarded as a valuable mutation. Missing
authenticated principal, missing selected character, capacity exhaustion,
clock failure, and random-ID failure also cannot create usable identity.

The native retry key binds:

- authenticated principal fingerprint;
- authenticated character;
- family 16, 17, or 18;
- raw target reference; and
- Mount material reference or Remove socket ordinal, where applicable.

Drill binds only its target. The Artisan NPC is canonicalized out of the key
so an otherwise identical retry can retain its UUID after movement between
Sparta and Athens. The shared registry remains bounded to 16 entries with a
ten-minute pending lifetime. An authenticated terminal Applied, Replayed,
Rejected, or Conflict result settles the UUID.

## Secure and raw compatibility boundaries

On a secure session, Mount, Remove, Drill, and their fail-closed aliases are
never permitted to downgrade into the legacy mutation path:

- a UUID-bearing request must match the exact 92-byte operation shape and
  its expected family;
- a mutation without a UUID is rejected before legacy handling;
- an alias or malformed shape is rejected rather than reinterpreted; and
- the all-`-1` Mount page request remains navigation and receives no
  operation identity.

Only UUID-bearing secure mutations use `IHolyStoneCommandExecutor` and gain
permanent inbox replay, request-hash conflict protection, revisions, immutable
ledgers, and strict outbox evidence.

Raw legacy TCP retains an explicit compatibility boundary. It uses the same
exact `HolyStoneProtocol` argument decoder for canonical Mount, Remove, and
Drill shapes, but still calls `ApplyWeaponHolyStoneAsync`. It has no client
operation UUID, permanent command receipt, or cross-reconnect idempotency.
Aliases, malformed lengths, bare bag references, unexpected arguments, and
unknown Holy Stone actions fail before store access. The compatibility
mutator also requires an existing empty socket and an allowlisted Fire Spirit,
consumes exactly one stacked material, rejects full-bag Remove without
clearing the socket, preserves the removed level, and limits basic Drill to
the same two Gold-priced sockets. Changed occupied bag slots are explicitly
evicted before the raw client is rehydrated.
The compatibility path must not be described as equivalent to the secure
transaction, and remains migration and reconciliation work under B09.

## Command envelope and authoritative transaction

`HolyStoneCommandEnvelope` accepts only authenticated secure command
provenance. Its canonical version-2 request binds:

- server-derived account and character;
- operation UUID and family;
- Mount, Remove, or Drill operation;
- one canonical equivalent-Artisan endpoint rather than a city-specific
  identity;
- target location and slot;
- Remove socket index or Mount material slot;
- strict, bounded canonical compact target and material states; and
- a role-tagged SHA-256 digest of the captured compact states.

The states are captured from the current server projection, then reloaded
and compared under lock. Reusing one UUID with different canonical content
is a permanent request-hash conflict.

`PostgresHolyStoneCommandExecutor` performs one transaction:

1. validate secure provenance, family, bounds, UUID, and canonical states;
2. lock the account-owned `character_base` row with `FOR UPDATE`, including
   Gold (`"Stone"`), `wallet_revision`, and `inventory_revision`;
3. read the permanent family-specific inbox before mutable item state;
4. replay the exact receipt or reject same-UUID canonical-content conflict;
5. ensure the opening economy baseline;
6. lock the target and Mount material rows in deterministic order, or the
   complete kit bag for Remove's server-selected empty output slot;
7. decode exact item rows and reject missing, stale, corrupt, or unexpected
   physical state;
8. require the target template to be a weapon;
9. plan the operation entirely from locked authoritative state;
10. persist terminal business rejection as audit and inbox evidence only;
11. for success, stage the canonical audit and permanent inbox receipt in the
    same transaction;
12. update only affected item rows while preserving existing item-instance
    IDs;
13. for Drill, debit Gold and advance `wallet_revision` exactly once;
14. advance `inventory_revision` exactly once;
15. append only real item and currency changes to immutable ledgers;
16. append one strict `inventory.holy_stone_changed` outbox event; and
17. commit before returning a terminal result.

All affected-row counts are checked. Cancellation, provider failure, a
before-commit failure, or an unknown after-commit outcome produces no
terminal command result. The UUID remains pending so replay can discover a
committed result without performing the mutation again.

Terminal business rejections persist a truthful receipt but do not update an
item, spend Gold, advance a revision, append mutation-ledger rows, or publish
an outbox event.

## Operation rules and material migration

Mount accepts only the client-authored Fire Spirit material IDs currently
proven by content:

| Material IDs | Effect IDs |
|---|---|
| 9060, 9061 | 1, 2 |
| 9062, 9063 | 5, 6 |
| 9064, 9065 | 7, 8 |
| 9066, 9067 | 3, 4 |
| 9088, 9089 | 17, 18 |

The target must already have a drilled, empty socket. Mount rejects an
unknown material, Heated Holy Stone 9030, a duplicate effect, no opened
socket, or no empty opened socket. A valid Mount consumes exactly one item
from the selected material stack, deleting the stack row only when its count
reaches zero. It spends no Gold and does not advance the wallet revision.

Remove uses the captured one-based socket ordinal and rejects an invalid or
empty socket. It clears that exact socket and creates one bound Heated Holy
Stone 9030 in the first authoritative empty kit-bag slot, carrying the
removed level in its Grade. A full bag rejects atomically. The target weapon
retains its item-instance ID, and the new output receives a stable database
item-instance ID. Remove spends no Gold.

Basic Drill opens only socket one or socket two:

- socket count 0 to 1 costs 230 Gold; and
- socket count 1 to 2 costs 2,300 Gold.

Insufficient Gold rejects atomically. Socket count two or greater returns
`MaximumSockets`; the server does not invent the stock client's advanced
third/fourth-socket materials or prices.

Migration `20260730_029_holy_stone_material_templates` seeds or reconciles
the authoritative `item_templates` rows for Heated Holy Stone 9030 and Fire
Spirits 9060-9067 and 9088-9089. It preserves the client-authored
`Icon2.gwo` coordinates, names, stack limits, and `PreStone` marker where
applicable. This is migration 029 and raises the catalog total to 30
migrations. Final application evidence belongs in the frozen PostgreSQL
gate receipt below.

## Replay, cross-city behavior, and response order

The secure handler checks the permanent inbox before requiring the current
map's NPC route or reading current target/material state. This permits a
retry after reconnect and permits the same UUID to replay through the
equivalent Artisan after transfer between Sparta and Athens.

Receipt validation treats `(5083, 30)` and `(5225, 30)` as equivalent
endpoints while still binding operation, family, character, target,
material, and socket roles. The stock reply uses the active request's
equivalent Artisan ID, not the city recorded by the original receipt.

For a newly committed result, durable terminal rejection, or exact replay,
the response order is:

1. stock opcode-10069 NPC result;
2. native clear acknowledgements for every previously occupied bag slot that
   differs from the reloaded authoritative projection;
3. authoritative local status, weapon, complete bag, equipment appearance,
   player detail, and nearby-player refreshes; and
4. authenticated family-16/17/18 terminal `0x0102` result last.

The committed projection is reloaded from PostgreSQL before any terminal
response and checked against the receipt's exact target, material, or output
after-state. It imports durable equipment and bag state, recalculates
equipment-derived stats, clamps live vitals where required, and preserves
runtime position and unrelated world state.

Replay sends no clear acknowledgement when the local and authoritative bags
already match. If a reconnect, stale-state rejection, conflict, or later
legitimate operation left a changed instantiated item, it clears that exact
slot before rehydration. A replay does not demand that the current projection
still equal the historical after-state.

Projection mismatch, invalid stored evidence, cancellation, provider
unavailability, and uncertain commit outcome emit no authenticated terminal
result. This avoids settling the native UUID before the client has a
truthful, usable authoritative view.

## Verification receipt

Development coverage is present for literal packet parsing, tri-state
classification, alias and malformed fail-closed behavior, family identity,
principal/character binding, capacity, expiry, settlement, secure downgrade
rejection, raw exact parsing, command hashing, replay-first routing,
cross-city replay, response ordering, material stacks, occupied sockets,
invalid materials, full-bag removal, Gold costs, stale state, concurrency,
rollback, after-commit uncertainty, ledger/outbox evidence, migration 029,
authoritative projection recovery, and changed-slot eviction on committed,
stale, replayed, conflict, invalid, precondition-failed, and raw outcomes.

Final frozen-tree verification passed:

- Release solution build: **0 warnings, 0 errors**;
- complete managed protocol harness: **223 passed, 0 failed**;
- strict Win32 Release network-shim build: **passed with `/W4 /WX`**;
- native offline and complete suites: **passed**;
- mandatory B03 PostgreSQL 17 gate: **31 required checks and three migration
  scenarios passed in 359,800 ms**;
- migration proof: **30 migrations applied through
  `20260730_029_holy_stone_material_templates`**;
- cleanup proof: **passed**, with no `godswar_b03_*` or `godswar_b09_*`
  database remaining; and
- three independent adversarial/read-only reviews: **no blockers remain**.

The B03 machine-readable artifact is
`artifacts/b03/b09-native-holy-stone-result.json`, exactly 13,607 bytes with
SHA-256
`C3BAB06E364041C4CCE669AA8A9D47578DFDFE2EC87801BB172C778E790A03E7`.

Frozen native artifact hashes:

- `Net.dll`:
  `9FA074F7EED1052DBA3841C6C335F4C81BB1C462FBAB6D6980E960566461FB62`;
  and
- `Godswar.NetShim.Checks.exe`:
  `1A6A85C76E4EA189EB6C6307C5B29348DD622E6223AB97CB8AC4F5F6B732714C`.

## Rollback and remaining B09 work

Rollback requires a matched prior server and native shim. Committed family
16-18 inbox, audit, ledger, compatibility-audit, and outbox rows are valid
economy history and must remain. Migration 029 is an idempotent content
upsert; do not delete its item templates while player inventory or immutable
history can reference them.

The native retry key lasts ten minutes while the PostgreSQL receipt is
permanent. After that window, only a client retaining the original UUID can
address the permanent receipt. A newly expressed operation receives a new
identity.

Known Holy Stone work that remains:

- capture and implement the stock advanced third/fourth-socket drill
  materials, prices, and packet semantics;
- capture any non-Fire Holy Stone material families before accepting them;
- migrate the raw compatibility transaction or retire it through controlled
  secure-client rollout; and
- complete B09 identity and authoritative transactions for the remaining
  tokenless inventory, reward, and currency mutations.
