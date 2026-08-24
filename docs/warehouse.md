# Warehouse

The normal warehouse is character-owned storage for items that a player does
not want to keep in the kit bag. Item instances remain in
`public.character_items`; warehouse rows use `item_location = 3`, so moving an
item preserves its instance ID, attributes, sockets, quality, grade, binding,
experience, and stack count.

## Capacity

The installed client exposes nine compact tabs, `SB-1` through `SB-9`, with
40 cells per box. A newly created character, and every character upgraded by
migration 107, starts with one box (40 cells). Migration 111 publishes this
revision-3 Storage Box Key policy for the Warehouse Manager:

| Current cells | New cells | Storage Box Keys consumed |
| ---: | ---: | ---: |
| 40 | 80 | 1 |
| 80 | 120 | 2 |
| 120 | 160 | 3 |

Storage Box Keys can therefore open only `SB-2` through `SB-4`. `SB-5`
through `SB-9` are reserved for Battle Pass unlocks and the Warehouse Manager
reports the key maximum for those capacities. The audited structural client
ceiling remains nine boxes (360 cells). Capacity and `warehouse_revision` are
persisted on `character_base`. Every warehouse item move also advances the
character's authoritative `inventory_revision`.

The expansion policy is published from revisioned PostgreSQL tables and is
pinned by each worker at startup. The key item and cost for every level are
read from that policy; they are not compiled into the handler or client UI.
Publishing a successor requires the audited compare-and-swap settings boundary
and a coordinated worker restart. Compiled code enforces only the verified
one-to-nine-box shape and bounded key quantities.

## Storage Box Key

The current database policy uses item 4102 (`Storage1`, **Storage Box Key**).
It is published in the immutable item catalog as a bound, stackable consume
item with a maximum stack of 99. The Warehouse Manager searches the
character's kit bag and consumes the policy-selected quantity atomically;
there is no Gold cost.

This release defines and consumes the key but does not invent a shop, drop, or
quest source. Key acquisition is a separate content decision.

## Native protocol

- A normal Warehouse NPC click returns the captured special opcode-10067
  acknowledgement (`mode=0x20`). Its exact eight-byte opcode-10068 request
  opens `SB-1`; subsequent exact 12-byte requests carry the selected logical
  page.
- The client owns one physical 40-cell warehouse view. The server projects the
  selected logical box into four opcode-10034 chunks and includes the current
  database capacity in a proxy-only marker. Locked tabs remain selectable and
  show an empty projected page, matching the stock client's separate per-tab
  views. The client does not request a locked page, and the server rejects
  transfers outside the character's unlocked capacity.
- Whole-stack deposit, withdrawal, and warehouse-internal moves use opcode
  10059. The client host translates only that fixed transfer frame from the
  visible physical page to logical slots; it does not resize or globally
  remap the stock client's fixed item collection. An active cross-box drag
  retains its source page while the destination page changes.
- Explicit occupied destinations swap items, matching the stock client.
- Automatic placement scans in ascending slot order, fills compatible partial
  stacks, and uses the first empty cell it encounters.
- Warehouse Manager uses NPC function 106 and action 100.

Money storage and the separate award-storage collection are intentionally out
of scope.

## Safety and replay

Transfers and expansion are committed atomically with command inbox/audit,
inventory ledger, outbox, and revision evidence. Transfers do not replay a
native mutation packet: fresh and duplicate outcomes converge through an
authoritative kit-bag refresh followed by the currently selected 40-cell page
projection. This also preserves the selected tab after deposits, withdrawals,
same-box moves, and cross-box moves.

Warehouse access is issued only after a successful open and is bound to the
account, character, realm, map, visible canonical NPC, and a 15-minute lease.
It is cleared on an unrelated NPC click or map transition. A related Warehouse
Manager click preserves an existing lease because the stock client can keep the
storage window open, but the manager never issues warehouse access. Packed
sealed-pet items cannot enter normal warehouse storage.

## Deployment

Migrations 107 through 111, the item-content successor, dialogue V8, the
server binary, the nine-tab client assets, the network page host, and the
runtime fingerprint must be rolled out together. Stop/drain all realm workers,
take a verified PostgreSQL backup, start every realm on one new image, and
verify policy revision 3 contains only the four levels through 160 cells before
admitting players.
