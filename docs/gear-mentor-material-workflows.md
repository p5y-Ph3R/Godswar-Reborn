# Gear Mentor material workflows

The physical Gear Mentor (`Sparta_070` / `Athens_070`, dialog `4`) owns the
native decomposition and material-conversion screens in `NpcFunBreak.lua`.
The client selects bag items and displays native result messages; inventory
eligibility, quantities, binding, capacity, and mutations are authoritative
server decisions.

## Implemented menu operations

| Menu | Confirmation route | Input | Authoritative result |
|---:|---:|---|---|
| `1` | Client-local page; final action `1` | One to three gear items | Decompose each eligible item into matching Attribute Dust |
| `4` | Client-local page; final action `4` | One Attribute Dust stack | Consume 99 dust and create one matching Attribute Stone |
| `8` | Client-local page; final action `8` | One Crystal stack | Downgrade one L5 Crystal to two L4, one L3 to four L2, or one L2 to eight L1 |
| `9` | Server page; client confirms with wire action `9`, normalized to authoritative operation `201` | One gem-piece stack | Consume 99 matching L4/L5 pieces and create one gem |

Menu `5` (Instructions) and menu `7` (Wash dust) remain reserved and return
native result `999` without mutating inventory. Instructions can therefore be
repurposed later without preserving guessed gameplay.

Every operation reloads the selected bag rows while holding the character
inventory lock. A missing, moved, replaced, or replayed selection is rejected.
Inputs are consumed and outputs inserted in one transaction, or nothing is
written. Output binding follows the consumed input, compatible stacks are
filled before empty slots, and insufficient bag capacity rejects the entire
operation. The stock client's pre-confirmation control-clear burst is accepted
only through an exact captured-item snapshot that expires after one second.
Live emulator logs confirmed the one-slot sequence as opcode `10193` selected,
opcode `10193` cleared, then the final scratch-tailed opcode `10069`; no
intermediate page request is sent for actions `1`, `4`, or `8`.

After a successful mutation, the server sends a native source-to-`FFFF`
deletion acknowledgement for every previously occupied slot whose item or
stack changed, followed by the complete authoritative bag snapshot. The stock
client does not evict an instantiated icon from detail/index refresh packets
alone; omitting these acknowledgements leaves consumed dust or decomposed gear
visible until relog even though the database transaction succeeded.

Decompose, Make Attribute Stone, Transform Crystals, and Combine Gem Pieces
now use family-scoped secure operation UUIDs and permanent PostgreSQL command
inboxes. For those operations, audit, item mutation, inventory revision,
immutable ledger entries, and strict outbox publication commit atomically
before the stock response. Decompose additionally persists each exact random
Dust outcome, so duplicate and reconnect replay cannot reroll it. Exact
retries replay the stored result; ambiguous failures leave the same native
UUID pending. Tokenless traffic remains an explicit compatibility path.

## Make Attribute Stones

Exactly 99 dust produce one stone from the same native family. The complete
client-backed mappings are:

| Dust IDs | Attribute Stone IDs | Families |
|---|---|---|
| `9900..9908` | `9930..9938` | Strength through Vigor |
| `9910..9919` | `9940..9949` | Accuracy through Restoration |
| `9920..9921` | `9958..9959` | Destruction and Penetration |

There are 21 valid dust types. Each has a native stack cap of 99 and uses its
shipped `Icon2.gwo` cell. Arbitrary items and dust stacks below 99 are rejected.

## Decompose gear

The client establishes these eligibility rules:

- character level 30 or higher;
- one to three genuine, non-stackable equipment items;
- equipment required level 50 or higher;
- Enhanced quality or Grade 2 or higher; and
- no Class Suit I-IV equipment.

The checked-in generated Class Suit catalog contains 248 exact client item IDs,
so the restriction does not rely on a loose name or ID range.

The client does not contain the original server's dust quantity/probability
table. Until a working-server capture establishes that table, the emulator uses
this explicit local rule:

1. Choose one dust family from the gear's appended attributes. If none maps to
   a native dust, choose from all 21 native dust families.
2. Produce `quality + grade - 1` dust, with quality and grade each treated as at
   least 1 and the result clamped to the native `1..99` stack range.
3. Preserve the decomposed gear's binding on its dust output.

This makes differently attributed gear yield different dust families and makes
higher quality/grade gear monotonically more valuable without claiming retail
drop-rate parity. No speculative Attribute Stone, Quartz Plate, or Flame Spark
bonus-roll probabilities are enabled.

## Transform high-quality Crystals

The shipped client exposes two downgrade recipes. The locally authored Level 5
tier extends the upper end with Level 5 and Level 4 conversions. The Level 5
conversion nearly preserves its forge contribution:

| Input consumed | Output created |
|---|---|
| `1 x Level 5 Crystal` (`4234`) | `2 x Level 4 Crystal` (`4233`) |
| `1 x Level 4 Crystal` (`4233`) | `2 x Level 3 Crystal` (`4232`) |
| `1 x Level 3 Crystal` (`4232`) | `4 x Level 2 Crystal` (`4231`) |
| `1 x Level 2 Crystal` (`4231`) | `8 x Level 1 Crystal` (`4230`) |

Level 1 Crystals are invalid inputs. Binding is preserved.

## Combine Level 4/5 gem pieces

All recipes consume exactly 99 matching pieces and produce one gem:

| Piece item | Result item |
|---|---|
| Level 4 Sapphire Pieces `4214` | Level 4 Sapphire `4213` |
| Level 4 Emerald Pieces `4224` | Level 4 Emerald `4223` |
| Level 5 Sapphire Pieces `4216` | Level 5 Sapphire `4215` |
| Level 5 Emerald Pieces `4226` | Level 5 Emerald `4225` |
| Level 5 Crystal Pieces `4235` | Level 5 Crystal `4234` |

The stock data has no Level 4 Crystal Pieces item, so no such recipe is
invented. The three local Level 5 piece definitions are coordinated across the
server catalog and both client locales. Their generated 36x36 shard icons use
`Icon4.gwo` cells Crystal `108,0`, Sapphire `144,0`, and Emerald `180,0`.
The corresponding complete Level 5 gems remain at `0,0`, `36,0`, and `72,0`.

Client material definitions and the menu's Level 4/5 wording are installed by
`tools/PatchClientGearMentorMaterials.ps1`. Deterministic icon sources are
generated by `tools/GenerateLevel5PieceIcons.py` and installed by
`tools/InstallLevel5ForgeIcons.py`.
