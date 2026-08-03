# Five ordinary plus three Class Suit attribute fields

Status: implementation contract for the patched Reborn server and original
client. The elemental catalog, values, colors, resonance rules, and future
stone candidates are specified in
[Class Suit elemental attribute roadmap](class-suit-elemental-attribute-roadmap.md).

## Player-facing rule

- Gear Enhancement can add up to five ordinary appended attributes.
- Class Suit III/IV gear can additionally hold one profession-specific Class
  Suit attribute and up to two elemental attributes.
- The elemental attributes must belong to different elements. Fire Power plus
  Fire Resistance on the same gear is invalid; Fire plus Water is valid.
- These three dedicated fields do not consume ordinary enhancement slots and
  cannot be enhanced with Quartz Plate. Their effective values use gear grade
  `1..25`.
- The current Delete dialog does not identify a dedicated target. Until that
  dialog is extended, deletion is deterministic: elemental slot 2, elemental
  slot 1, then the profession-specific slot.
- Converting gear to Common removes all three dedicated fields. Upgrading
  Class Suit III to IV preserves them.

## Durable representation

`character_items.attribute1..attribute5` remain authoritative for ordinary
attributes and retain their matching level fields. The dedicated fields are:

- `class_attribute1`: nullable profession-specific Class Suit attribute;
- `elemental_attribute1`: nullable first elemental attribute; and
- `elemental_attribute2`: nullable second elemental attribute.

`class_attribute2` is retained only as a deprecated compatibility column and
must be `NULL` after migration 054. The migration aborts instead of discarding
player value if any table, baseline snapshot, or inventory ledger still holds
a legacy second profession-specific attribute.

Database constraints and authoritative planners allow these fields only on the
reviewed Class Suit III/IV item catalog. They reject invalid grades, unknown
IDs, elemental slot 2 without slot 1, duplicate elements, dedicated IDs hidden
in ordinary slots, and stale or replayed material selections.

## Native 72-byte GWA3 item record

The native item-record stride and its identity fields are unchanged.

| Offset | Size | Meaning |
|---:|---:|---|
| `+4..+20` | 20 | Five native ordinary attribute IDs |
| `+52` | 4 | Signed profession-specific Class Suit ID; `-1` is empty |
| `+56` | 4 | Element slot 1 in low 16 bits and slot 2 in high 16 bits; `0xFFFF` is empty |
| `+60` | 4 | Exact little-endian marker `GWA3` (`47 57 41 33`) |
| `+64` | 4 | Existing state/source identity, unchanged on the wire |
| `+68` | 4 | Existing owner/slot identity, unchanged on the wire |

The client decodes neither extension field unless the marker is exact. It
validates both packed elemental IDs against `480..500`, requires different
elements, and fails closed on malformed state. Empty items and items without a
dedicated attribute carry no extension marker. Native Holy Boxes retain their
stored-EXP use of offset `+56` and can never carry `GWA3`.

The patched client keeps the fixed item-object allocation intact. It stores the
three decoded template pointers in audited tagged sidecar fields and masks the
tags before dereferencing. Expanding native five-entry loops is forbidden
because doing so overwrites adjacent item-object state and causes crashes.

## Compatibility and integrity

- Legacy records and exact `GWA2` records remain supported as read paths during
  migration. New authoritative writes use `GWA3`.
- The server owns eligibility, slot capacity, duplicate-element validation,
  material consumption, persistence, grade scaling, and resonance totals.
- Expected-state strings, transaction ledgers, move/equip operations, forging,
  inspection identities, snapshots, and reconciliation include all dedicated
  fields.
- The client computes the visible Elemental Resonance panel from the twelve
  equipped authoritative item records; the server independently computes the
  typed profile from the same canonical equipment state.
- Elemental Power, Resistance, and Penetration are not converted into global
  physical or magical modifiers. Combat activation waits for authoritative
  skill-element mapping and the separately reviewed combat patch.
- A stock client cannot display or safely round-trip `GWA3`, so the matching
  patched client must be released before the new server protocol is enabled.

## Roadmap-only Class Suit stones

Styx, Nemesis, Aegis, Nike, Asclepius, Chronos, Moirai, and Thanatos stones are
reserved in the elemental roadmap. Their IDs and safety rules are documented,
but they are not seeded, obtainable, or active in this release.
