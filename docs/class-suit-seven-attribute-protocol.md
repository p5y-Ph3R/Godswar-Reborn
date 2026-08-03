# Five ordinary plus two Class Suit attributes

Status: implementation contract for the patched Reborn server and client.

## Player-facing rule

- Gear Enhancement can add up to five ordinary appended attributes.
- Any Class Suit III or IV gear item can additionally hold up to two Class Suit
  attributes.
- The two Class Suit attributes are not ordinary enhancement slots. They cannot
  be enhanced with Quartz Plate and they do not reduce the five-slot ordinary
  allowance.
- The same Class Suit attribute cannot be added twice.
- The current Delete dialog does not identify a particular Class Suit stone.
  Until that dialog is extended, deletion is deterministic: slot 2 is removed
  first, followed by slot 1 on the next operation.
- Converting Class Suit gear to Common removes both Class Suit attributes.
  Upgrading Class Suit III to IV preserves both.

## Durable representation

`character_items.attribute1..attribute5` remain the authoritative ordinary
attributes and retain their matching level fields. Separate nullable
`class_attribute1` and `class_attribute2` columns own the Class Suit bonuses.
Class Suit values resolve from the equipment grade, so they have no independent
enhancement level.

The database accepts these fields only for the reviewed Class Suit III/IV item
IDs across all 62 conversion branches. Common gear, Class Suit I/II, materials,
Holy Boxes, and arbitrary item IDs fail closed.

The forward migration moves existing Class Suit IDs
`200, 201, 210, 211, 220, 221, 230, 231` out of the five ordinary fields and
compacts the remaining ordinary ID/level pairs. This preserves existing items
while freeing all five normal slots. If a legacy class attribute is attached to
anything outside the reviewed Tier III/IV set, migration 053 aborts for explicit
operator repair instead of silently dropping or exposing invalid player value.

## Native 72-byte item record extension

The record stride and existing fields are unchanged.

| Offset | Size | Meaning |
|---:|---:|---|
| `+4..+20` | 20 | Five native ordinary attribute IDs |
| `+52` | 4 | Class Suit attribute 1, signed little-endian ID or `-1` |
| `+56` | 4 | Class Suit attribute 2, signed little-endian ID or `-1` |
| `+60` | 4 | Little-endian marker `GWA2` (`47 57 41 32`) |
| `+64` | 4 | Existing state/source identity; unchanged |
| `+68` | 4 | Existing owner/slot identity; unchanged |

A client must ignore `+52/+56` unless the marker is exact. Empty items and items
without Class Suit attributes do not carry the marker. The server emits the
marker only for item IDs in the authoritative Class Suit III/IV conversion
catalog. Native Holy Boxes keep their existing stored-EXP value at `+56`; they
can never carry this marker.

The stock client is not compatible with the two extra display fields. The
patched client decodes them into separate storage and appends both tooltip
lines. The server applies their authoritative stats. Raising the native
five-entry client loops to seven is forbidden because the fixed item object
would overwrite its base-template pointer and crash.

## Compatibility and integrity

- The server remains authoritative for eligibility, duplicate checks, capacity,
  material consumption, stat calculation, and persistence.
- Item expected-state strings, transaction ledgers, move/equip operations,
  forging, inspection identities, and projections include both Class Suit fields.
- A stock client may ignore the extension, so the feature must not be enabled in
  a mixed-client production release. Deploy the matching patched client first.
- Operation 107 remains fail-closed; it is not used as a shortcut for the fifth
  ordinary slot or either Class Suit field.

## Additional Class Suit attribute candidates

Two unused content prototypes are suitable candidates after their combat rules
receive authoritative server implementations:

| Proposed stone | Proposed item | Attribute | Existing grade curve | Restriction |
|---|---:|---:|---|---|
| Styx Stone | `9986` | `460` (`VampiricPer`) | G1 `0.2%` to G25 `5.2%` | Class Suit III/IV gear |
| Nemesis Stone | `9987` | `470` (`ReflectDamagePer`) | G1 `0.1%` to G25 `2.6%` | Class Suit III/IV gear |

They are candidates, not enabled content. A future implementation needs
loadout-wide stacking caps (proposed starting limits: life steal `10%`, damage
reflection `15%`). Life steal must heal from final direct damage and cap at
missing HP. Reflection must use final direct damage received and must not
recursively reflect, crit, or trigger life steal. Destruction (`9958`, attribute
`240`) and Penetration (`9959`, attribute `250`) remain ordinary Gear Enhancement
stones.
