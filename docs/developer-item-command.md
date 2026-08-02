# Developer material command

Mount generation uses a separate server-authoritative catalog and command
namespace; see `docs/developer-mount-command.md`. It does not widen the material
allowlist described below.

The item command is disabled by default and is authorized by exact account ID.
It can only grant item IDs present in the server's narrow developer-item
allowlist; arbitrary item IDs are rejected. The allowlist combines the
forging/Gear Enhancement materials below with the five Holy Boxes published by
the active database-backed Holy Suit content revision.

The allowlist contains 23 ordinary forging materials, all 51 shipped Gear
Enhancement materials, and all 21 native Attribute Dusts. Their type, texture,
icon, native stack cap, and binding state are resolved again inside the store;
command text cannot override them.

The checked-in configuration keeps it disabled. This workstation's ignored
`.env` enables it for the local test accounts `3`, `7`, `13`, and `347`. To
enable or extend the allowlist elsewhere, edit
`game.developerCommands.allowedAccountIds` in the active settings file, or set:

```text
GODSWAR_DEVELOPER_COMMANDS_ENABLED=true
GODSWAR_DEVELOPER_ACCOUNT_IDS=3,7,13,347
```

Restart the server after changing either setting.

The command is a local development tool, not an authentication boundary. Keep
the server loopback-bound while it is enabled; the compose default publishes
login and game ports only on `127.1.1.110`.

Use the local chat box with either a native ID or material alias:

```text
/item add 4230 99
/item add crystal1 99
/item add crystal 1 99
/item add sapphire4pieces 99
/item add sapphire5pieces 99
/item add emerald5 99
/item add crystal5pieces 99
/item add strengthdust 99
/item add shielddust 99
/item add magicdust 99
/item add strengthstone 99
/item add quartzplate1 99
/item add flamespark 99
/item add watergrain 99
/item add holybox1 1
/item add emptyholybox5 1
```

### Dedicated Ruby command

Ruby has a short developer command that does not require `/item add` or
`/gmitem`:

```text
/ruby 1
/ruby 2 99
/ruby 3 99 op=35b344af-7906-4e71-ac04-d2eaf501de63
```

Its syntax is `/ruby <level> [quantity] [op=<UUID>]`. Level `1`, `2`, or `3`
resolves the existing Ruby item IDs `4200`, `4201`, or `4202`, respectively.
Quantity defaults to `1` and uses the same `1..999` limit as the general
developer-item command. Ruby levels `4` and `5` are rejected because neither
exists in the original client data; the command does not synthesize them.

The optional `op=` value must be a nonzero D-format UUID. On PostgreSQL it
provides the same durable idempotency guarantee as `/item add`: retrying the
same operation cannot grant a second copy. `/ruby` remains subject to the same
disabled-by-default switch and exact account-ID allowlist documented above.

`holybox1` through `holybox5` (and the explicit `emptyholybox1` through
`emptyholybox5` aliases) create bound Holy Boxes with `item_exp = 0`. They do
not create pre-filled experience. Their item IDs, names, stack caps, and bound
rules come from the process-pinned PostgreSQL item-content revision rather than
from a second command-only list. Each box has a native stack cap of one.

`/item` is the clearest stock-client form. The client masks `/gmitem` to
`/******` before sending its Talk packet; the server recognizes that exact
masked wire form too, so the original `/gmitem add ...` spelling also works.
Both forms remain protected by the same enabled flag and exact account-ID
allowlist.

To remove every item from the current character's kit bag, use the deliberately
explicit form:

```text
/item clearbag confirm
/item clearbag confirm op=00000000-0000-0000-0000-000000000001
```

The final `confirm` token is mandatory. An optional nonzero D-format UUID can
be supplied as the final `op=` token. With PostgreSQL, that form stores one
permanent command result: an exact retry returns the original outcome and
cannot clear items acquired after the first evaluation. The tokenless form is
retained only as a local legacy-compatibility command and has no cross-reconnect
retry guarantee.
This operation clears only the 96 kit-bag slots. It does not change equipped
gear, warehouse/storage items, silver, gold, stats, skills, map state, or any
other character data. The PostgreSQL store records one recoverable
`character_item_audit` entry with source `developer-clearbag` for each removed
bag row. The JSON store persists the canonical empty bag, so starter potions are
not restored on the next login.

Quantity defaults to `1` and is limited to `999` per command. Existing stacks
are filled to their native cap, then empty bag slots are used. Most materials
cap at `99`; the eight weapon-specialization stones `9950`-`9957` cap at `1`.
If the full quantity cannot fit, nothing is written.

Gear Enhancement aliases use the normalized client name, for example
`strengthstone`, `stoneofvitality`, `quartzplate1`, `flamespark`, and
`watergrain`. Numeric IDs are also accepted. The intentional client-data gap
`9939` remains rejected.

## Forging-material catalog

| Material | ID | Client name | Type | Stack cap |
|---|---:|---|---|---:|
| Ruby 1 | 4200 | Level 1 Ruby | consume item | 99 |
| Ruby 2 | 4201 | Level 2 Ruby | consume item | 99 |
| Ruby 3 | 4202 | Level 3 Ruby | consume item | 99 |
| Sapphire 1 | 4210 | Level 1 Sapphire | consume item | 99 |
| Sapphire 2 | 4211 | Level 2 Sapphire | consume item | 99 |
| Sapphire 3 | 4212 | Level 3 Sapphire | consume item | 99 |
| Sapphire 4 | 4213 | Level 4 Sapphire | consume item | 99 |
| Sapphire 4 pieces | 4214 | Level 4 Sapphire Pieces | consume item | 99 |
| Sapphire 5 (local) | 4215 | Level 5 Sapphire | consume item | 99 |
| Sapphire 5 pieces (local) | 4216 | Level 5 Sapphire Pieces | consume item | 99 |
| Emerald 1 | 4220 | Level 1 Emerald | consume item | 99 |
| Emerald 2 | 4221 | Level 2 Emerald | consume item | 99 |
| Emerald 3 | 4222 | Level 3 Emerald | consume item | 99 |
| Emerald 4 | 4223 | Level 4 Emerald | consume item | 99 |
| Emerald 4 pieces | 4224 | Level 4 Emerald Pieces | consume item | 99 |
| Emerald 5 (local) | 4225 | Level 5 Emerald | consume item | 99 |
| Emerald 5 pieces (local) | 4226 | Level 5 Emerald Pieces | consume item | 99 |
| Crystal 1 | 4230 | Level 1 Crystal | consume item | 99 |
| Crystal 2 | 4231 | Level 2 Crystal | consume item | 99 |
| Crystal 3 | 4232 | Level 3 Crystal | consume item | 99 |
| Crystal 4 | 4233 | Level 4 Crystal | consume item | 99 |
| Crystal 5 (local) | 4234 | Level 5 Crystal | consume item | 99 |
| Crystal 5 pieces (local) | 4235 | Level 5 Crystal Pieces | consume item | 99 |

The shipped client has no Ruby levels 4-5 or native Level-5 Sapphire, Emerald,
Crystal, or their pieces. IDs `4215`, `4225`, `4234`, `4216`, `4226`, and
`4235` are coordinated local extensions; they must not be granted to an
unpatched client. `MaterialBase5`/`4214` and `MaterialAppend5`/`4224` remain
Level-4 pieces. The complete Level-5 gem sprites use `Icon4.gwo` cells Crystal
`0,0`, Sapphire `36,0`, and Emerald `72,0`; their piece sprites use Crystal
`108,0`, Sapphire `144,0`, and Emerald `180,0`. The stock `Icon2.gwo` atlas is
left untouched.

All 21 Attribute Dusts accept their exact ID or normalized client name, such
as `strengthdust`, `shielddust`, `magicdust`, `dustofdestruction`, and
`dustofpenetration`. Dust IDs are `9900..9908`, `9910..9919`, and `9920..9921`;
all have a native stack cap of 99. See `docs/gear-mentor-material-workflows.md`
for their stone mappings and Gear Mentor recipes.

For ordinary forging, Level-4 Sapphire keeps its Q8..Q12 range and Level-4
Emerald keeps G10..G17. Level-5 Sapphire gives `+32` on current Q8..Q19, so Q19
is the final eligible input and a success produces Q20/Boundless. Level-5
Emerald gives `+32` on current G10..G24, so G24 is the final eligible input and
a success produces G25. Each selected Level-5 Crystal gives `+25`, with the
ordinary-forge quantity capped at 25. At G24, 24 crystals yield raw `87%`; the
maximum 25 yield raw `112%`, which the server clamps to `100%`.

The added Q13..Q19 probability adjustments are
`-255,-265,-275,-285,-295,-305,-315`; Q20 retains a zero terminal entry. Their
silver costs use the equipment economy unit times `35,40,45,50,55,60,65`.
The added G18..G24 adjustments are
`-395,-420,-445,-470,-495,-520,-545`; G25 retains a zero terminal entry. Their
silver costs use multipliers `55,60,65,70,75,80,85`.

These forge ceilings extend only the core numeric quality vectors and
`BaseFraction` to 20 entries, and the grade-indexed `AppFraction` to 25.
`MainAttribute` is an allowed-attribute list, while `ArmEffFraction`/`ArmEff`
and `DefendFraction`/`DefendEff` are independent rank tables; they must remain
byte-for-byte unchanged rather than being padded to Q20 or G25.

Install these coordinated client rules with
`tools/PatchClientForgeBoundlessGrade25.ps1`. The older Q13/G18 patch scripts do
not define the complete ceiling and must not be run afterward.
