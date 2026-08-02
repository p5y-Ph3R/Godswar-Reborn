# B09 advanced Holy Stone classification

Date: 2026-07-30

Status: classified and fail-closed; value mutation not implemented

## Decision

Holy Stone action `701` is now an explicit unsupported valuable-operation
boundary. The stock page transition renders the original two-slot Advanced
Drilling layout using response sub-IDs `107`, `207`, and `307`, but every
value-bearing action-701 shape is rejected before operation identity,
inventory, wallet, durable command, or legacy-store access.

This is a safety closure, not an implementation of advanced drilling. A
third- or fourth-socket mutation must not be invented from UI text alone.

## Confirmed client evidence

The original client data establishes the feature's meaning:

- `C:\Godswar Origin\Localization\en_us\UI\Base\LuaText.lua:3653`
  names action `301` **Equipment Drilling**.
- The same file at line 3656 names action `701` **Equipment Advance
  Drilling**.
- Lines 3682-3686 say the ordinary Artisan creates only two openings,
  Hephaestus creates the other two, Socket Spell III or IV is required,
  and the target must already have at least two sockets.
- `EquipName.dat:1002-1003` identifies item `4272` as Socket Spell III and
  item `4273` as Socket Spell IV.
- `EquipDescription.dat:1002-1003` says those items open the third and
  fourth equipment sockets with the Holy Stone Artisan.
- `ItemBaseAttribute.xml:1067-1068` confirms both are stackable consumable
  items with the same stock icon coordinates.
- PostgreSQL migration `038_packet_opcode_holy_stone.sql` and the pinned
  dialogue fixture expose action `701` in the Artisan's initial menu.

The existing literal capture
`captures/capture-proxy-20260514-173331.log` proves exact 92-byte
client-to-server shapes for action `101` Mount, `201` Remove, and `301`
basic Drill. It contains no observed action-701 client commit.

Static analysis did not recover the missing contract. `Origin.exe` has a
2013 PE build timestamp, while `GodsWar.map` is dated 2009 and does not map
the executable's current code addresses. Searches of the executable and
available source trees found no reliable action-701 serializer. Therefore
the following remain unknown:

- which of the eighteen signed arguments select the equipment and spell;
- whether the target encoding supports all equipment or only captured
  weapon reference `205`;
- whether Socket Spell III/IV is explicitly selected or server-selected;
- exact success and rejection response sub-IDs;
- exact consumption and client refresh sequence; and
- retry equivalence when an equipped item moves to the bag.

The UI's word “equipment” is especially important: reusing the current
weapon-only basic Drill executor could reject legitimate targets or mutate
the wrong slot.

## Implemented boundary

The native parser recognizes action `701` separately from implemented
families 16-18:

- an exact 92-byte Artisan/dialog-30 packet with all eighteen arguments
  `-1` remains an untagged page transition;
- any populated argument is `InvalidMutation`;
- a malformed declared length, frame length, or duplicated dialog is
  `InvalidMutation`; and
- no action-701 packet can produce a `LegacyHolyStoneCommand`, operation
  UUID, retry key, or secure command family.

The managed server also names action `701` without mapping it to
`HolyStoneCommandOperation` or `CommandFamily`. On an authoritative Holy
Stone dialogue route, the exact all-`-1` transition receives the native page
response. Any value-bearing shape receives the generic wrong-selection
response and records the low-cardinality reason
`missing_c2s_wire_capture`. Raw and secure requests cannot reach
`ApplyWeaponHolyStoneAsync` or `IHolyStoneCommandExecutor`. A forged
UUID-bearing request receives no fabricated terminal command result because
no validated family or request hash exists.

Unknown menu values remain unrelated. Only the specifically identified
valuable action `701` receives this boundary.

## Evidence required to enable the feature

Capture clear stock traffic for each step, with before/after inventory and
equipment snapshots:

1. Opening action `701` from the Artisan menu.
2. A successful third-socket operation using item `4272`.
3. A successful fourth-socket operation using item `4273`.
4. Wrong spell, missing spell, wrong socket count, maximum sockets, invalid
   gear kind, and insufficient-resource failures.
5. Equipped and bag targets for every gear kind the stock UI permits.
6. A disconnect/retry immediately before and after the authoritative
   commit.

The capture must establish literal packet bytes, argument roles, response
sub-IDs, item deletion acknowledgements, detail-page refreshes, and visual
refresh behavior. Only then should B09 add a new secure family, canonical
request hash, PostgreSQL inbox/audit/ledger/outbox transaction, authoritative
projection, and replay settlement.

## Verification scope

Automated checks cover:

- native all-`-1` page-transition classification;
- native populated, malformed-length, and malformed-dialog rejection;
- absence of native command decoding for action `701`;
- secure and raw page rendering plus value-shaped and UUID-bearing server
  rejection; and
- zero durable-executor, legacy-store, inventory, wallet, and fabricated
  command-result activity.

The existing PostgreSQL Holy Stone suite remains the regression proof for
families 16-18. No schema or content migration is justified for an
unsupported action that cannot safely mutate value.
