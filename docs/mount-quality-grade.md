# Mount quality and grade compatibility

The stock client stores only ten quality-indexed values on mount and mount-gear
items. Sending a persisted quality above ten without extending those vectors can
make the item detail path read beyond the authored data.

`tools/PatchClientMountQualityVectors.ps1` extends every `mount`, `mounthead`,
`mountarmor`, `mountsoul`, `mountornament`, and `mountamulet` numeric quality
vector to 20 entries in both client locales.

Q1 through Q10 remain the stock client values except for the deliberately
redesigned mount `Speed` vector described below. Q11 through Q20 are a local
design because no native Q11-through-Q20 mount data is present. Mount gear uses
the average slope from the full authored Q1-through-Q10 prefix. Each extended
value is calculated directly from that unrounded slope; integer stats are
rounded away from zero. This avoids both cumulative rounding drift and
cross-tier inversions caused by extending one unusually large or small terminal
step. The patch fails if any vector decreases or a higher-level mount-gear tier
would fall below the preceding tier at any extended quality.

Native mount Speed and MaxHP vectors are flat within each item, but the client
authors clear progression between the level tiers of each mount family. MaxHP
keeps the conservative family-step extension. Speed uses a reviewed local
balance curve across all twenty qualities: `+0,+1,+2,+3,+4,+5,+6,+7,+8,+10,
+12,+14,+16,+18,+20,+22,+24,+26,+28,+30` percentage points. Every mount keeps
its authored Common base, so family and level distinctions remain intact.
Boundless adds 30 points rather than borrowing the next level tier's 1-point
increase. The fastest native 50% special mounts therefore cap at 80%, before
separately governed status effects, instead of growing without a ceiling.

The resulting level-80 Valorheart set is:

| Quality | Coronet Hit | Armor HP | Soul absorb | Ornament HP | Amulet dodge |
| ---: | ---: | ---: | ---: | ---: | ---: |
| Q1 | 13 | 2,960 | 74 | 1,110 | 12 |
| Q2 | 15 | 3,315 | 83 | 1,243 | 14 |
| Q3 | 17 | 3,670 | 92 | 1,376 | 15 |
| Q4 | 18 | 4,026 | 101 | 1,510 | 17 |
| Q5 | 20 | 4,381 | 110 | 1,643 | 18 |
| Q6 | 21 | 4,736 | 118 | 1,776 | 20 |
| Q7 | 23 | 5,032 | 126 | 1,887 | 21 |
| Q8 | 24 | 5,328 | 133 | 1,998 | 22 |
| Q9 | 25 | 5,624 | 141 | 2,109 | 23 |
| Q10 | 27 | 5,920 | 148 | 2,220 | 24 |
| Q11 | 29 | 6,249 | 156 | 2,343 | 25 |
| Q12 | 30 | 6,578 | 164 | 2,467 | 27 |
| Q13 | 32 | 6,907 | 173 | 2,590 | 28 |
| Q14 | 33 | 7,236 | 181 | 2,713 | 29 |
| Q15 | 35 | 7,564 | 189 | 2,837 | 31 |
| Q16 | 36 | 7,893 | 197 | 2,960 | 32 |
| Q17 | 38 | 8,222 | 206 | 3,083 | 33 |
| Q18 | 39 | 8,551 | 214 | 3,207 | 35 |
| Q19 | 41 | 8,880 | 222 | 3,330 | 36 |
| Q20 | 43 | 9,209 | 230 | 3,453 | 37 |

The level-80 Erebus Lion base progression is:

| Quality | Speed bonus | Max HP |
| ---: | ---: | ---: |
| Q1 Common | 0.24 | 3,700 |
| Q10 Mystic | 0.34 | 3,700 |
| Q20 Boundless | 0.54 | 4,000 |

Mount-gear append attributes use the same grade-indexed values as ordinary
character gear. G1 through G12 remain the stock client prefix. The local
G13-through-G25 extension uses the established ordinary equipment grade-score
profile, anchored at G12 = 100 and ending at G25 = 400. At every extended
grade, the chosen value is never lower than either the stock authored value or
the preceding grade. This preserves stronger native tails such as Hit `321`
(G25 `40`) and Miss `333` (G25 `41`) instead of weakening them to the generic
profile. Quality never selects an append-attribute value.

For the four level-80 Warrior attributes currently used on the test mount set:

| Grade | Attack 343 | Physical damage 363 | Ignore physical defense 403 | Flat physical damage 423 |
| ---: | ---: | ---: | ---: | ---: |
| G1 | 28 | 0.51% | 0.51% | 61 |
| G2 | 30 | 0.56% | 0.56% | 67 |
| G3 | 33 | 0.61% | 0.61% | 73 |
| G4 | 36 | 0.66% | 0.66% | 80 |
| G5 | 39 | 0.71% | 0.71% | 86 |
| G6 | 41 | 0.77% | 0.77% | 92 |
| G7 | 44 | 0.82% | 0.82% | 98 |
| G8 | 46 | 0.86% | 0.86% | 103 |
| G9 | 48 | 0.90% | 0.90% | 108 |
| G10 | 51 | 0.94% | 0.94% | 113 |
| G11 | 53 | 0.98% | 0.98% | 118 |
| G12 | 55 | 1.02% | 1.02% | 122 |
| G13 | 64 | 1.1832% | 1.1832% | 142 |
| G14 | 73 | 1.3566% | 1.3566% | 162 |
| G15 | 83 | 1.5402% | 1.5402% | 184 |
| G16 | 94 | 1.734% | 1.734% | 207 |
| G17 | 105 | 1.938% | 1.938% | 232 |
| G18 | 116 | 2.1522% | 2.1522% | 257 |
| G19 | 128 | 2.3766% | 2.3766% | 284 |
| G20 | 141 | 2.6112% | 2.6112% | 312 |
| G21 | 154 | 2.856% | 2.856% | 342 |
| G22 | 168 | 3.111% | 3.111% | 372 |
| G23 | 183 | 3.3864% | 3.3864% | 405 |
| G24 | 201 | 3.723% | 3.723% | 445 |
| G25 | 220 | 4.08% | 4.08% | 488 |

Therefore the value rounded to `2.5%` in the previous client was the old G25
value, not the G1 value. G1 is `0.51%`. Both the client XML and server seed must
be updated together or tooltips and authoritative combat stats will disagree.

Native mount items do not have an append-attribute pool. The locally authored
Erebus Lion family is the deliberate exception: every Erebus tier copies the
`MainAttribute` pool from the same-level Valorheart Coronet (`14500..14508`).
The special level-120 Erebus item `16209` uses the level-120 `14508` pool.
This keeps the native level-to-attribute relationship without repurposing a
stock mount:

- attribute-ID suffixes follow the mount's required-level tier, not quality;
- level-80 Erebus `16204` permits suffixes 1 through 3, including Warrior
  offensive attributes `343`, `363`, `403`, and `423`;
- suffix 7 remains invalid for level 80 even when the mount is Q20/Boundless;
- G25 drives the permitted attribute values;
- Erebus base HP preserves the native flat Q1-through-Q10 prefix and rises to
  `4,000` at Q20; Speed follows the shared additive quality curve;
- the server resolves Ride speed from the equipped item's quality, so Q1 uses
  `0.24` while Q20 authoritatively publishes a `1.54` riding multiplier.

`character_stat_summary` reads every equipped `character_items` row, including
slots 15 through 20. The item snapshot writes all five attribute IDs plus the
full one-byte quality and grade. The progression checks cover those two paths;
the former repeated values were data, not a slot filter or packet truncation.

## Future Mount Feeder material

Reserve IDs `14210..14214` for **Hippocrene Gem I..V**, with developer aliases
`mountgem1..mountgem5`. These IDs are unused in both shipped locales. The gem
must be added as a new material rather than replacing Soul Stones or Golden Gem:

- Soul Stones `14201..14208` remain mount-level materials.
- Golden Gem `4259` remains the one-per-step mount-gear level material.
- Hippocrene Gem can be consumed by separate quality and grade actions only
  after their costs and success curves are defined.

Candidate unused `Icon4.gwo` cells are `(216,0)`, `(252,0)`, `(288,0)`,
`(324,0)`, and `(360,0)`. A distinct horseshoe/jewel visual should be authored
before adding these item templates to either locale.
