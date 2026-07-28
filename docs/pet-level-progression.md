# Pet Level Progression

## Authoritative rules

- Pets start at level `1` and cap at level `120`.
- The full level 1-to-120 cost is `252,947,820` EXP.
- The first advancement, level 1 to level 2, costs `1,500` EXP.
- One Upgrade click advances exactly one level and spends only that
  transition's cost.
- Every committed advancement adds each attribute's persisted
  `base_growth_rate` to that attribute's current basic/initial savvy. Thus a
  level-1 Strength value and growth rate of `9` becomes `18` at level 2,
  `27` at level 3, and so on.
- Level, EXP, all six basic-savvy values, the parent revision, and all six
  stat revisions commit in one PostgreSQL transaction. Added savvy, birth
  baselines, rarity-added savvy, base growth, and growth acceleration are not
  changed by leveling.
- Excess EXP carries forward. A pet at level 120 can retain nonzero EXP, but
  its next-level requirement is `0`.
- The server accepts only a pet ID from the client. Ownership, availability,
  level, EXP, cost, resulting level, and remaining EXP are authoritative.

The complete 119-transition table is maintained once in
`PetExperienceCatalog.cs`. Runtime validation and protocol checks require:

- exactly 119 positive, strictly increasing costs;
- a sum of exactly `252,947,820`;
- level 1 cost `1,500`;
- captured level 21 cost `575,025`;
- captured level 107 cost `4,419,900`; and
- level 120 next cost `0`.

With the test grant of `300,000,000` EXP, advancing from level 1 through level
120 spends `252,947,820` and retains `47,052,180`.

## Legacy protocol

Upgrade uses a verified request/update pair:

| Direction | Opcode | Length | Fields |
|---|---:|---:|---|
| C2S | `10285` (`0x282D`) | 8 | `u32 petId` at `+4` |
| S2C | `10286` (`0x282E`) | 20 | Stock prefix: `u32 petId` at `+4`, `u8 level` at `+8`, three reserved zero bytes, `u32 remainingExp` at `+12`, `u32 nextLevelCost` at `+16` |
| S2C | `10286` (`0x282E`) | 44 | Current compatible extension: the unchanged 20-byte prefix followed by six authoritative `u32` basic-savvy totals at `+20..+40`, in stat-code order `1..6`, encoded as `value * 100` |

The server locks the owned pet row and all six stat rows, validates and commits
one advancement plus its six growth increments, records exact before/after
state in the `level_up` audit, and only then sends `10286`. Rejected requests
do not receive `10286` because the native handler always treats that packet as
a successful level-up and displays its success notification.

The guarded client patch is documented in
[Pet level savvy client refresh](pet-level-savvy-client-patch.md). It copies
the six appended totals into the same native fields populated by the full pet
bootstrap, allowing the open Pet window to refresh without rebuilding its pet
collection.

## Evidence

The installed client proves both packet layouts and the one-click/one-level
behavior. Existing original-server `10237` captures independently verify the
level 1, 21, 107, and 120 next-level values and show that level-120 pets keep
overflow EXP.

The full progression table was recovered from the 2015 GodsWar Online
Pet EXP Guide:

<https://niminiforever.blogspot.com/2015/04/godswar-online-pet-exp-guide.html>

The embedded 720p table's cumulative level-120 value is `252,947,820`, and all
locally captured anchors match it exactly.
