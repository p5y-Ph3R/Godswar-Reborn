# B09 secure native equipment/bag transfer increment

Date: 2026-07-30

Source base: `8b2a708ff5cf3429c03a92dbd10cfbefa2976efc`

Status: increment implemented and verified; B09 remains open

## Outcome

Explicit drag/drop between one equipment slot and one kit-bag slot now has a
secure family-15 design and implementation:

```text
stock StorageItem equipment/bag transfer
  -> family-15 native operation UUID
  -> authenticated TLS 0x0101 marker
  -> permanent replay lookup before direction or Ride-state inference
  -> bounded application command envelope
  -> locked PostgreSQL equip-or-unequip transaction
  -> authoritative equipment, bag, stat, and appearance refresh
  -> authenticated family-15 terminal 0x0102 result
```

Opcode `10052` contains an equipment slot and a bag slot but no direction bit.
The server therefore infers direction only from the two exact authoritative
states locked for the command:

- empty equipment plus occupied bag means equip;
- occupied equipment plus empty bag means unequip;
- both empty is a permanent non-mutating `BothEmpty` result; and
- both occupied is a permanent non-mutating `BothOccupied` result.

This family deliberately does not swap two occupied locations. An unequip
uses the exact requested empty bag slot; it does not search for another empty
slot. A committed transfer updates one existing `character_items` row and
preserves its PostgreSQL item-instance ID and complete item state.

No schema migration is required. The implementation reuses the permanent
command inbox, command audit, character inventory revision, immutable
inventory ledger, strict outbox, compatibility audit, and opening economy
baseline introduced by migrations 025 through 028.

## Exact packet evidence and native identity

Equipment/bag transfer is secure command family `15` and stock opcode `10052`
(`0x2744`). The native shim assigns family-15 identity only to an exact
80-byte framed packet:

| Full-packet offset | Width | Meaning |
|---:|---:|---|
| 0 | 2 | declared length, exactly 80 |
| 2 | 2 | opcode, exactly 10052 |
| 8 | 2 | equipment slot, zero through 20 |
| 10 | 2 | equipment/bag sentinel, exactly `0xFFFF` |
| 12 | 2 | bag page, zero through three |
| 14 | 2 | index within the page, zero through 23 |
| 16-79 | 64 | opaque stock-client data, excluded from identity |

The canonical bag slot is `page * 24 + index`, in the range zero through 95.
The server later rejects reserved equipment slots 13 and 14 as
`WrongEquipmentSlot`; the native parser accepts the bounded wire range zero
through 20 so those terminal outcomes can still receive and replay an
authenticated result.

The literal captured golden vector is
`captures/service-unequip-fixed.log:471-473`. Its clear packet is 80 bytes,
uses equipment slot 10, sentinel `0xFFFF`, page zero, and bag index one.
Additional 80-byte client requests appear at:

- `captures/working-multiplayer-20260514-193356.log:5645-5647`;
- `captures/working-multiplayer-20260514-193356.log:6056-6058`;
- `captures/working-multiplayer-20260514-193356.log:6431-6433`;
- `captures/working-multiplayer-20260514-193356.log:7061-7063`; and
- `captures/service-equip-check.log:12453-12455`.

The opaque tail changes between stock-client requests and is intentionally
not trusted or hashed. Exact length, opcode, sentinel, slot, page, and index
checks keep kit-bag movement, ground deletion, truncations, and other opcode
shapes out of family 15.

The native retry identity binds the authenticated principal fingerprint,
authenticated character, family 15, equipment-slot role, and bag-slot role.
The same pair reuses one UUID across equivalent opaque tails and reconnects.
Changing either role produces another UUID. Identity is not based on client
direction because the packet has none.

The shared registry remains bounded to 16 entries with a ten-minute lifetime.
It requires both an authenticated principal and selected character before it
can issue identity. Applied, Replayed, Rejected, and Conflict results settle
the pending UUID through the authenticated `0x0102` result path.

Malformed, unsupported-length, missing-principal, and missing-character
requests cannot create family-15 identity. If an authenticated peer supplies
a UUID beside an unsupported transfer length, the managed handler rejects it
and returns before the tokenless compatibility path. This prevents a
secure-to-legacy downgrade.

## Application command and Ride guard

`EquipmentBagTransferCommandEnvelope` accepts only `SecureTlsLegacy` or
`SecureCommand` provenance. Its canonical version-1 request binds:

- the server-derived account and character;
- the client operation UUID;
- the equipment slot and kit-bag slot;
- the server-captured compact state of both locations; and
- one server-observed `MountRuntimeBlocked` bit.

Each compact state is strict UTF-8, bracketed, free of control characters,
and limited to 512 bytes. Infrastructure reparses each value through
`CompactItemEntry` and requires byte-for-byte canonical form. The two state
hash inputs carry distinct role tags and lengths.

`MountRuntimeBlocked` is valid only for equipment slot 20. It is not supplied
by the game client. Before first execution, the handler sets it when the Ride
skill cast is pending or the character has the active mounted runtime status.
The bit is part of the canonical request hash, so the same UUID cannot change
that observation after execution begins.

Permanent inbox replay happens before reading either current slot and before
observing current Ride state. This matters because a successful retry has
reversed occupancy, and a Ride status may change between attempts. A stored
result remains the authority instead of being reinterpreted from new runtime
state.

After exact state comparison, a one-empty/one-occupied slot-20 command with
the Ride guard set becomes terminal `RideRuntimeBlocked`. It commits audit and
inbox evidence only: no item movement, revision, ledger entry, or outbox
event. A retry returns that exact stored result. A direct re-execution using
the same UUID but a changed Ride bit conflicts by request hash.

The receipt records character, both slots, expected and authoritative compact
states, inventory revision, audit reference, optional outbox event, and one
finite semantic result:

- `Equipped` or `Unequipped`;
- `StaleEquipment` or `StaleKitBag`;
- `BothEmpty` or `BothOccupied`;
- `ItemNotEquipment`, `WrongEquipmentSlot`,
  `ProfessionRestricted`, or `LevelRestricted`;
- `MountDependencyBlocked` or `MountUnsupported`; or
- `RideRuntimeBlocked`.

Only `Equipped` and `Unequipped` may carry an outbox event and advanced
inventory revision.

## PostgreSQL authority and transaction

`PostgresEquipmentBagTransferCommandExecutor` performs one transaction:

1. validate secure provenance, command bounds, request identity, and both
   canonical compact states;
2. lock the account-owned `character_base` row and read profession, fighter
   level, and inventory revision;
3. read the permanent family-15 inbox before either mutable slot;
4. replay the exact stored receipt, or commit conflict evidence when the UUID
   is reused for another slot pair or request hash;
5. ensure the character's opening economy baseline;
6. lock the requested equipment and bag rows in deterministic
   location/slot order;
7. reject a physical row that decodes as an empty compact item;
8. compare expected equipment first and expected bag second, making
   `StaleEquipment` deterministic when both differ;
9. apply the Ride terminal guard before direction and eligibility;
10. infer equip or unequip only when exactly one locked location is occupied;
11. for equip, query database-authoritative `item_templates` and validate
    equipment kind, exact slot, ring slot 8/9 flexibility, class, and level;
12. validate enabled mounts, mount level versus equipped mount gear, mount
    gear versus the equipped mount, and mount removal versus slots 15-19;
13. for a terminal outcome, commit canonical command audit and inbox evidence
    only;
14. for transfer, persist result evidence for the next revision;
15. insert one compatibility audit for the exact source item;
16. update only the locked item-instance ID at its exact old location and
    slot, changing location, slot, and `updated_at`;
17. capture the full authoritative JSON after-state;
18. advance `inventory_revision` once with an optimistic predicate;
19. append one ordinal-zero `move` inventory-ledger entry containing the
    exact item-instance ID and full before/after states;
20. append one strict `inventory.equipment_bag_transferred` outbox event; and
21. commit before returning success.

Equipment rows use location zero and kit-bag rows use location one. The
unique `(user_id, item_location, slot_index)` constraint protects each
destination. Because occupied destinations are terminal, family 15 needs no
temporary location and cannot accidentally reproduce right-click replacement
or kit-bag swap behavior.

The account-owned character lock serializes durable and existing compatible
inventory writers for that character. Mount dependency rows are additionally
locked in deterministic slot order. Every audit, update, revision, ledger,
inbox, and outbox write requires an exact affected-row count. A failure before
commit rolls the whole transaction back. A failure after commit remains
uncertain to the handler and is resolved through permanent replay.

The strict outbox consumer decodes the canonical receipt and verifies that
the result is `Equipped` or `Unequipped`, event ID matches, aggregate revision
matches, and aggregate key matches the character inventory.

## Empty physical rows fail closed

`prop_id = 0` decodes as an empty `CompactItemEntry`. A physical
`character_items` row is not allowed to masquerade as an absent location:
after locking, the executor detects any present equipment or bag row whose
decoded item is empty and throws before durable result evidence or mutation.

The transaction rolls back and the handler emits no terminal family-15
result, leaving the UUID pending for investigation or data repair. It does
not move the corrupt row, advance the inventory revision, append a ledger
entry, or publish an outbox event. This is deliberately fail-closed rather
than treating corrupt durable state as a legitimate empty slot.

The normal foreign key from `character_items.prop_id` to `item_templates.id`
prevents new unknown item IDs. The explicit runtime guard still protects
legacy, partially migrated, or manually corrupted databases.

## Replay, projection, and response order

The secure handler always calls `TryReplayAsync` before capturing current
equipment, bag, or Ride state. After a new transfer commits, the handler
reloads the PostgreSQL snapshot and, for the `Committed` disposition only,
verifies the exact post-transfer destination before sending the one-time
stock acknowledgement:

- `Equipped` requires the former bag item in the requested equipment slot
  and an empty requested bag slot; and
- `Unequipped` requires an empty requested equipment slot and the former
  equipment item in the requested bag slot.

This committed-only check runs before any response. A mismatch emits neither
the stock acknowledgement nor the authenticated terminal result, so the
operation remains unresolved and can be recovered through permanent replay.
After the check succeeds, a newly committed transfer sends:

1. refreshed local status;
2. the stock opcode-10052 transfer acknowledgement exactly once;
3. authoritative equipment, complete bag, appearance, detail, and nearby
   player refreshes; and
4. the authenticated family-15 Applied result last.

The PostgreSQL snapshot reload replaces both live equipment and kit bag,
recalculates equipment-derived stats, clamps current HP/MP to the new maxima,
updates the live registry, and clears stale Forge, Gear Enhancer, and pending
unequip selections. It preserves runtime position, wallet, and unrelated
world state.

An exact duplicate does not receive the stock transfer acknowledgement. That
acknowledgement is non-idempotent in the original UI and could reverse the
visual transition. Replay also deliberately skips the committed-only exact
destination verification: a later legitimate operation may already have
changed either slot after the original family-15 result. Replay instead sends
the current authoritative projections followed by the historical family-15
Replayed result.

Durable terminal rejections and definitive non-durable conflicts refresh
authoritative state without the stock acknowledgement, then send Rejected or
Conflict. Provider failure, cancellation, invalid stored evidence,
after-commit uncertainty, or projection-reload failure emits no terminal
family-15 result. The native operation remains pending so another attempt can
discover the permanent result.

## Tokenless and right-click limitations

Tokenless opcode-10052 transfers retain the existing compatibility handler.
This preserves original-client operation when secure identity is absent, but
does not provide cross-reconnect idempotency, permanent family-15 inbox
receipts, inventory revision/ledger guarantees, or a strict outbox event.
This remains an explicit reconciliation gap.

Right-click equip is opcode `10051`, normally an exact 92-byte packet. The
same opcode and byte shape is also used for pet egg hatching; the server
distinguishes the action only after reading the authoritative bag item.
Captured evidence appears at
`captures/working-multiplayer-20260514-193356.log:5856-5858`.

The native shim cannot safely decide whether those untrusted bytes mean equip
or hatch. It therefore does not assign family 15 to opcode 10051. Right-click
equip and pet hatching remain on their existing server-selected compatibility
paths. Extending durability here requires a broader authoritative bag-item
activation command or a controlled client protocol change; native heuristics
or item-hint trust would misclassify eggs and are not acceptable.

## Verification status

Final frozen-tree verification passed:

- Release server solution build: **0 warnings, 0 errors**;
- complete managed protocol harness: **220 passed, 0 failed**;
- strict Win32 Release native build: **passed with `/W4 /WX`**;
- native offline suite: **passed**;
- complete native suite: **passed**;
- mandatory B03 PostgreSQL gate: **30 required checks and three scenarios
  passed in 350,490 ms**;
- migration proof: **29 migrations applied through
  `20260729_028_economy_ledger_hardening`**;
- cleanup proof: **passed**, with no `godswar_b03_*` or
  `godswar_b09_equippg_*` database remaining; and
- three independent read-only reviews: **no blockers found**.

The B03 evidence artifact is
`artifacts/b03/b09-native-equipment-bag-transfer-result.json`, exactly
13,227 bytes with SHA-256
`86E2EAA99FD86A39892A629CF268B940E178ABAB670B54644F86F41510E4E702`.

Frozen native artifact hashes:

- `Net.dll`:
  `B88EE20720E2841AD68A5BD40586EA005962B9BAFFB165782F7C91DF056737D4`;
  and
- `Godswar.NetShim.Checks.exe`:
  `CE007CF5D2F0AFA4665D811A351FCE72B9FB6DCF46C852B06640EAE894414984`.

Coverage includes literal captured-vector parsing; strict packet-shape and
cross-opcode separation; principal, character, reconnect, pair-role,
capacity, expiry, codec, and settlement behavior; managed hashing, Ride-bit
binding, replay-first routing, projection and response ordering, outage,
cancellation, and downgrade rejection; and disposable PostgreSQL success,
eligibility, stale-state, conflict, concurrency, rollback, after-commit,
stable-ID, ledger, outbox, corruption, and reconciliation scenarios.

## Rollback and remaining B09 work

Rollback requires a matched prior server and native shim. Already committed
family-15 inbox, audit, ledger, compatibility-audit, and outbox rows are valid
economy history and must remain.

The compact stock projection does not carry the PostgreSQL item-instance ID.
A delayed first execution therefore cannot distinguish an item removed and
replaced with a byte-identical item in the same slot: the legacy ABA
limitation remains. Different-state replacement and every retry after a
durable result are protected. Eliminating ABA requires an authenticated
durable item-instance token in a controlled client projection and command.

B09 remains open. Tokenless equipment transfers, right-click equip,
Holy Stone operations, rewards, and remaining inventory and currency
mutations still need truthful retry identity and authoritative durable
transactions.
