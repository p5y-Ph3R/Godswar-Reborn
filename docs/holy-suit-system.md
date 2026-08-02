# Holy Suit system

Status: authoritative PostgreSQL gameplay path plus a LocalDevelopment test
fixture. The policies below are the reviewed alpha rules; they are not claims
about undocumented behavior in the original server.

## Master Vestment Forger workflows

The stock client opens four native NPC pages. The server validates item
ownership, bag slot, item type, level, capacity, revisions, and duplicate
operations before changing authoritative state.

| Workflow | Inputs | Authoritative result |
|---|---|---|
| Storing EXP | one Holy Box and an EXP amount, or blank for Store Maximum | subtract player fighter EXP and add it to that box |
| Transferring EXP | regular gear first, filled Holy Box second | transfer the whole box balance to gear and consume the box |
| Ware Consuming | regular gear first, matching ware second | consume the required gear EXP/Prisms and ware, then advance Holy Suit tier/level |
| Transform EXP | number of Prisms | exchange 100,000,000 player EXP for each bound Experience Prism |

The local patched client's Transform EXP page visibly pre-fills `20` Prisms in
both supported locales, which represents 2,000,000,000 EXP. The stock native
button does not copy a Lua-set value into its packet field: it sends the blank
`-1` sentinel plus its zero commit scratch value. Only that exact confirmation
shape maps to the displayed 20-prism mouse-only default. The all-`-1` packet
remains page navigation, typed positive values remain unchanged, and the server
still validates the count, available character EXP, bag capacity, durable
operation identity, and replay behavior authoritatively.

Storing EXP requires player level 70. One request may fill the selected Holy
Box up to its remaining capacity; the largest current box therefore accepts
400,000,000 EXP at once. The alpha daily allowance is a fixed 2,000,000,000
EXP, independent of character level and measured per account, realm, and
Singapore calendar day (`Asia/Singapore`, UTC+8). It resets at 00:00 Singapore
time. An active `battle_pass` entitlement bypasses only that daily allowance;
it does not bypass available player EXP, box capacity, or level checks. Daily
usage is recorded even while the bypass is active.

The stock client's blank Store field sends `-1`. The server reserves only
that wire sentinel as a mouse-only Store Maximum request; a typed zero remains
invalid. Inside the locked PostgreSQL transaction, Store Maximum resolves to
the smallest of the current 400,000,000 safety ceiling, remaining Holy Box
capacity, available character EXP, and remaining daily allowance. Battle Pass
removes only the daily constraint. Audit evidence keeps the automatic request
(`0`) separate from the resolved applied EXP.

Opening the Storing EXP page reads the current account/realm/Singapore-day
usage and fixed credit from PostgreSQL; it does not reuse session memory. The
stock client encodes each displayed counter as `value * 10 + suffix` in a
signed 32-bit integer. Its largest safe visible value is therefore
`HolySuitDesignProtocol.MaximumEncodedCounter` (`214,748,364` EXP). The server
shows that saturated value for the 2,000,000,000 EXP allowance and for larger
usage, while PostgreSQL and the authoritative command path retain and enforce
the exact values. The same display sentinel represents an active Battle Pass;
the client display limitation does not weaken the backend cap.

Gear must be level 70 or above and its stored EXP cannot exceed
2,000,000,000. A transfer is all-or-nothing. The selected box is consumed
only after the complete amount fits. Ware upgrades are deterministic after
all preconditions pass. Bronze through Platinum consume gear EXP. Upgrades
into Mithril and higher automatically consume bound Experience Prisms from
the bag when the published transition requires them. One Prism represents
100,000,000 EXP; this implementation does not add the legacy client's
optional bound-Gold charge.

The target tier selects the ware, and the target Holy Suit level selects its
quantity. The supported wares are:

| Item ID | Ware | Stack cap |
|---:|---|---:|
| 9010 | Bronze | 99 |
| 9011 | Silver | 99 |
| 9012 | Gold | 99 |
| 9013 | Platinum | 99 |
| 9014 | Mithril | 99 |
| 9015 | Orichalcum | 99 |
| 9016 | Adamantium | 99 |

Holy Suit points are derived from the Holy Suit levels of equipped regular
gear slots 0 through 11. Bag-only upgrades do not contribute until the item
is equipped.

## Holy Boxes

Boxes are non-stackable. A box becomes bound when EXP is stored in it.

| Item ID | Box | Maximum stored EXP |
|---:|---|---:|
| 9020 | Holy Box I | 100,000 |
| 9021 | Holy Box II | 1,000,000 |
| 9022 | Holy Box III | 10,000,000 |
| 9023 | Holy Box IV | 100,000,000 |
| 9024 | Holy Box V | 400,000,000 |

Item 9025 is the bound, stackable Experience Prism used by Mithril,
Orichalcum, and Adamantium upgrade transitions.

## Mutable inventory identity compatibility

The sealed manifest-v6 publication is the only current Holy Suit content
authority. Manifest v5 remains immutable history and a possible reviewed
rollback target; it is not the authority while the v6 pointer is published.
`item_templates` remains a legacy foreign-key identity table because
`character_items.prop_id` still references it; gameplay does not read Holy
Suit policy from that mutable table.

At server startup only,
`PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync` validates the
complete sealed v6 release and then checks the exact reviewed Holy Suit item
set (9010 through 9016 and 9020 through 9025). Within the same serializable
publication transaction, it inserts only missing identity rows by copying the
sealed definitions. It never updates an existing row. Any existing row that
differs from the sealed definition fails startup and rolls back every repair
insert made by that attempt. This is compatibility validation and repair, not
a recurring gameplay write or a second content authority.

Database write access to legacy `item_templates` is therefore an operational
trust boundary. Restrict it to migration/publication and approved offline
maintenance identities. A writer that changes a Holy Suit identity can stop
the next server startup, even though it cannot change the sealed runtime
publication.

## Local test2 fixture

[`tools/GrantHolySuitTestKit.ps1`](../tools/GrantHolySuitTestKit.ps1) is an
offline-only LocalDevelopment fixture. It is deliberately not registered as
a gameplay command or production GM/admin API.

It defaults to account 13 and character `test2`. In one serializable
PostgreSQL transaction it:

- verifies the current `item_templates` rows and the sealed, published v6
  item definitions for IDs 9010 through 9016 and 9020 through 9024;
- requires every requested ID's published Holy Suit role, tier, capacity,
  stack cap, and grant-bound flag to match the fixture exactly;
- refuses a character that still has a checkpoint owner;
- keeps the first existing matching bag row and removes matching duplicates;
- fills all five bound boxes to their documented capacities;
- sets each of the seven unbound ware stacks to 99;
- uses authoritative empty bag slots in ascending order for missing items;
- advances `inventory_revision` exactly once; and
- appends one `character_item_audit` row for every add, reset, or duplicate
  deletion.

The script also refuses to run while the `godswar-server` container is
running. Stop the server cleanly, run the fixture, then start the server:

```powershell
docker compose --profile legacy-raw stop server
./tools/GrantHolySuitTestKit.ps1
docker compose --profile legacy-raw up -d --wait server
```

Use `-AccountId`, `-CharacterName`, `-PostgresContainer`, `-ServerContainer`,
`-Database`, and `-DatabaseUser` only when the local topology differs. Use
`-WhatIf` to verify the Docker-side offline gate without mutating the
database.

Rerunning the fixture is state-idempotent: it resets the same twelve item
types and never leaves duplicate matching rows. A rerun still constitutes an
explicit fixture operation, so it advances the inventory revision and records
fresh compatibility audits.

This fixture does not create command-inbox, outbox, or inventory-ledger rows.
Those records identify authenticated gameplay commands; inventing them for
an offline fixture would misrepresent their trust boundary. The fixture's
evidence boundary is the single database transaction, one inventory-revision
advance, and `character_item_audit`. Do not use it against a live or
production database, and do not use it as a model for a production admin
grant service.
