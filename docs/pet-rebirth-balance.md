# Pet rebirth balance

This document records the authoritative project balance for pet rebirth and
separates it from behavior that still requires original-server packet
captures.

## Eligibility and state transition

- Rebirth 1 requires pet level 50.
- Rebirth 2 requires pet level 80.
- Rebirth 3 requires pet level 100.
- Rebirth 4 requires pet level 110.
- Rebirth 5 and every later rebirth require pet level 120.
- The pet must be summoned, available, and not merged with its owner.
- The pet must have a Soul Contract and an available rebirth attempt.
- Rebirth returns the pet to level 1.
- Basic/initial savvy remains unchanged.
- Added savvy resets only to its immutable rarity-added-savvy floor. Training
  added after hatching is cleared.
- Growth acceleration is cumulative across rebirths.
- Model evolution occurs at rebirth counts 8 and 20.
- Rebirth 100 is the project maximum.

The exact conversion of levels above the required level into carried pet EXP
is not yet known and must not be accepted from the client.

## Materials

Every authored rebirth result requires exactly five units in total from:

- Rebirth Spirit (`10104`); or
- Reborn Harpyia (`10098`), the restricted equivalent accepted only for a
  bound pet.

Rebirth-attempt items are tiered separately:

| Rebirths enabled | Item | ID |
|---:|---|---:|
| 1-30 | Spring Water | 10107 |
| 31-60 | Juice of Rebirth | 10145 |
| 61-100 | Ambrosia of Rebirth | 11095 |

The installed client's instructions describe rebirth water as increasing the
available rebirth count. The inventory executor and the remaining Pet Manager
request/response packets are not wired until their original-server protocol
has been captured.

## Growth-acceleration roll

Each of the six attributes is rolled independently to hundredth precision.
The range applies when all five spirit units are used:

| Resulting rebirth count | Increase per attribute |
|---:|---:|
| 1-30 | 0.10-0.20 |
| 31-60 | Progressively rises from 0.10-0.20 to 0.30-0.40 |
| 61-100 | Progressively rises from 0.30-0.40 to 0.50-0.60 |

At rebirth 60, no individual attribute can gain more than `0.40`. At rebirth
100, no individual attribute can gain more than `0.60`. The server calculates
and validates the complete outcome; a client-supplied stat result is never
authoritative.

## Pet level and EXP evidence

Pet level 120 is confirmed as the terminal level by the installed client:

- `Localization/en_us/Settings/Sys/Pet_Alter.xml`;
- the level-120 rejection text in `Message.dat`;
- the rebirth instructions in `LuaText.lua`; and
- captured original-server level-120 pet records, whose next-level EXP is
  zero.

The complete original level 1-120 EXP table was recovered and independently
validated against the installed client and captured next-level denominators:

| Pet level | EXP needed for next level |
|---:|---:|
| 1 | 1,500 |
| 21 | 575,025 |
| 107 | 4,419,900 |
| 120 | 0 (maximum level) |

The 119 transition costs total exactly `252,947,820`. The authoritative
table, native `10285`/`10286` level-up exchange, overflow behavior, and source
evidence are recorded in `pet-level-progression.md`.
