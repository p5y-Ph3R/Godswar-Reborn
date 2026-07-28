# Pet system foundation

## Scope and terminology

The stock client uses the word **merge** for two unrelated operations. The
server must keep them separate:

- **Owner merge / Unite**: the summoned pet contributes stats and pet-skill
  effects to its owner. The pet remains owned and is not consumed.
- **Pet merge / Inosculate**: a primary pet absorbs a secondary pet. The
  secondary pet is consumed only after the transaction commits.
- **Rebirth / Samsara**: the pet returns to level 1, trainable added savvy is
  reset to its immutable rarity floor, and rebirth progression advances.
- **Soul Contract / Indenture**: an independent operation that improves a
  pet's initial savvy. The stock server requires it for rebirth, while the
  detailed Pet Manager instructions say it does not affect pet merge.

The foundation now includes relational pet state, the owned-pet login
bootstrap, and authoritative Carry, Summon, and Recall transitions. Pet
Manager crafting, inventory consumption, merge/rebirth executors, and the live
character-stat overlay remain later slices.

Pet aptitude is a separate 1-16 authoritative ladder. Values 1-14 retain the
stock names from `PETAPTITUDE1` through `PETAPTITUDE14`; values 15 and 16 are
project extensions named **Celestial** and **Transcendent**. PostgreSQL stores
the mapping in `pet_aptitude_templates` and references it from owned pets.

For project gameplay, aptitude is the pet's **quality tier** and is the
authoritative input to its base growth budget/range. The resulting Agility,
Strength, Accuracy, Technique, Wisdom, and Luck growth rates remain a
per-pet distribution so operations such as Phoenix's Feather can redistribute
them. Numeric Rank is a separate property, and rebirth growth acceleration is
an additional modifier; neither replaces the quality-derived base growth.
Exact tier budgets, random ranges, rounding, and the extension values for
Celestial and Transcendent use the explicit project-authored `project-v2`
balance below rather than pretending to be recovered stock behavior.

| Aptitude/quality | Total base-growth bracket |
|---|---:|
| Weak | 0.01-0.10 |
| Fool | 0.10-0.25 |
| Cowish | 0.25-0.50 |
| Moderate | 0.50-1.00 |
| Rational | 1.00-2.00 |
| Calm | 2.00-4.00 |
| Smart | 4.00-7.00 |
| Zealous | 7.00-11.00 |
| Grumpy | 11.00-16.00 |
| Brave | 16.00-23.00 |
| Overbearing | 23.00-31.00 |
| Ferocious | 31.00-40.00 |
| Almighty | 40.00-50.00 |
| Godly | 50.00-62.00 |
| Celestial | 62.00-75.00 |
| Transcendent | 75.00-100.00 |

The server rolls the total to hundredth precision, then distributes it across
the six growth rates. Each individual rate remains within 12% of an even
share, uses at most six decimal places, stays positive even for a `0.01`
Weak roll, and must sum back to the exact rolled total. The
brackets widen as aptitude rises, making each upper quality materially more
valuable. Adjacent brackets intentionally share one boundary value but have
strictly increasing expected totals. Base growth is stored independently from
rebirth growth acceleration.

### Basic and rarity-added savvy at birth

At hatch, each basic/initial-savvy attribute is exactly `1 ×` its matching
base-growth attribute. For example, `16.767423` Agility growth produces
`16.767423` basic Agility savvy. This relationship is stored per stat as the
immutable birth baseline; later legitimate operations may raise current
initial savvy without changing that historical baseline.

Each successful level advancement adds that stat's immutable base-growth rate
to current basic/initial savvy. Therefore, at level `L`, an otherwise
unmodified pet has `basic savvy = birth basic savvy + ((L - 1) * base
growth)`. The authoritative level-up transaction applies all six increments
together with the EXP deduction and level change; growth acceleration remains
reserved for rebirth policy and is not silently added by ordinary leveling.

The egg's aptitude selects a separate project-authored `project-v2`
**rarity-added-savvy** total:

| Aptitude/quality | Total rarity-added-savvy bracket |
|---|---:|
| Weak | 250-349 |
| Fool | 350-449 |
| Cowish | 450-574 |
| Moderate | 575-699 |
| Rational | 700-849 |
| Calm | 850-1,024 |
| Smart | 1,025-1,224 |
| Zealous | 1,225-1,474 |
| Grumpy | 1,475-1,774 |
| Brave | 1,775-2,124 |
| Overbearing | 2,125-2,524 |
| Ferocious | 2,525-2,974 |
| Almighty | 2,975-3,474 |
| Godly | 3,475-4,024 |
| Celestial | 4,025-4,624 |
| Transcendent | 4,625-5,324 |

The server rolls one whole-point total and randomly assigns the distinct
bounded weights `80, 88, 96, 104, 112, 120` to the six attributes. A
largest-remainder allocation keeps hundredth precision and preserves the exact
total. The six values therefore are not equal, while no attribute is starved.
The immutable per-stat rarity allocation is also the floor retained when
rebirth clears later trainable added savvy.

The egg already owns its rarity; hatching does not roll a second rarity. The
server maps the authoritative egg instance's quality value directly to the
matching aptitude and rejects undefined values without consuming the egg.
It rolls base growth first, derives basic savvy directly from that vector,
then independently rolls and distributes the aptitude-based added-savvy
budget. All three complete vectors and both immutable savvy baselines are
persisted in the same transaction.

The installed client has species-specific creation profiles only for aptitude
values `1,2,3,4,5,7,8,9,10,12,14`. Hatching also requires that exact
species-plus-aptitude profile so lifetime is not borrowed from a different
rarity. Values `6,11,13` and the project extensions `15,16` remain valid
growth tiers, but an egg with one of those rarities is preserved and rejected
until matching client profiles are deliberately authored and shipped.

Native egg items are non-stackable (`Overlap=1`). The transaction therefore
requires exactly one egg in the authoritative slot, consumes it, enforces the
native eight-pet limit, creates the species starter skill, records the egg
rarity, growth policy, added-savvy policy, totals, and all six-stat results in
the audit, and commits or rolls back the entire operation atomically. Migration
017 gives pre-policy pets a
deterministic midpoint growth distribution only when all six existing growth
values are zero or missing; any pet with a nonzero value is preserved.
Migration 018 installs `project-v2` and moves only complete six-stat pets whose
old total is outside their revised rarity bracket to that bracket's balanced
midpoint. In-bracket pets retain their exact values. Every changed stat keeps
an atomic before-image in `pet_growth_reconciliation_archive`, including its
old growth and revision, so a later forward recovery migration can restore the
pre-v2 result without relying on a code rollback.
Startup rejects a partial or non-positive six-stat growth vector instead of
silently treating corrupt legacy state as a valid pet, and also rejects totals
outside the persisted aptitude bracket.

Migration 019 assigns a deterministic bracket midpoint only to complete
legacy initial-savvy vectors whose six values are all zero. It preserves every
nonzero vector, including progressed values above the birth range, and archives
each changed stat plus the parent pet revision/provenance before-image in
`pet_initial_savvy_reconciliation_archive`. After reconciliation the database
removes the legacy zero default, so every future pet creator must explicitly
provide its initial savvy.
Startup requires six distinct rows and a nonzero initial-savvy total, but does
not enforce the creation maximum as a lifetime cap.

Migration 020 corrects migration 019 without rewriting its applied checksum.
For every unprogressed `project-v1` pet, it archives the full before-image,
sets basic savvy to the matching base-growth vector, and deterministically
shuffles the preserved rarity total into a non-equal added-savvy vector. It
stores immutable per-stat birth and rarity baselines, advances each affected
revision exactly once, and aborts instead of guessing if an affected pet has
incomplete or already-progressed data.

The verified owned-pet bootstrap is opcode `10237` (`0x27FD`). Within each
`0xA8`-byte pet record, six little-endian `uint32` initial-savvy fields occupy
offsets `0x6C` through `0x83`; the six added-savvy fields occupy `0x84` through
`0x9B`. Both use `value * 100` fixed point. This is why birth savvy is
distributed at hundredth precision and is visible after the post-hatch pet
list refresh. Base growth is authoritative in PostgreSQL and hatch audit data
but is not encoded by this verified bootstrap; its client display and gameplay
use remain gated on capturing the native pet-detail/growth packet.

## Client-derived rules

### Owner merge

The original client requires:

- a summoned pet;
- the Merge talent;
- full pet energy;
- at least 40 amity;
- a pet that is not already merged.

The reverse operation is an immediate unmerge. The exact ongoing energy/amity
consumption rate is not present in plaintext client data and must be captured
before it is enabled.

`Pet_Alter.xml` exposes the 16 contribution effect IDs and their six-savvy
curves. The numeric tables are authoritative evidence, but the native
interpolation and rounding function has not yet been recovered. The server
stores normalized fixed-point decimal contributions so that a later verified
calculator can replace the preview calculation without changing persistence.

### Pet merge

- Both primary and secondary pets must be level 30 or higher.
- They must be different pets owned by the same character.
- The primary pet must be summoned.
- The primary pet survives; the secondary pet is sacrificed on success.
- Merged Spirit (`10103`) improves the result.
- Fused Harpyia (`10097`) is the restricted equivalent accepted only when the
  primary pet is bound.
- At most five standard and restricted merge spirits may be used in total.
- The operation improves the primary pet's rank and six initial savvy values.
- A locked, dispatched, sealed, or already-consumed pet is not eligible.

The client contains quality/restriction lookup tables and per-species
multipliers, but the final native roll formula is not exposed. A client-sent
rank, savvy gain, or success result must never be accepted as authoritative.
The generic message catalog also references a deputy-quality restriction and
a 30-level EXP-gap restriction. Their exact native comparison semantics are
not yet proven, so the planner does not pretend to implement them.

### Rebirth

The rebirth level gates, material tiers, exact five-spirit balance,
growth-acceleration ranges, level cap, and EXP evidence are maintained in
[Pet rebirth balance](pet-rebirth-balance.md). Rebirth preserves the immutable
rarity-added-savvy floor while clearing only later trainable additions.

### Soul Contract

- Contract Spirit (`10105`) may be inserted, maximum five.
- Client `Base_Alter` values for zero through five spirits are
  `300, 400, 500, 600, 700, 800`.
- A new contract replaces the previous contract result.
- The detailed stock-client pet-merge instructions explicitly say contract
  status has no effect on pet merge. An older generic merge rejection string
  conflicts with that instruction, so original-server packet capture remains
  the final compatibility check.
- Rebirth does require a contract: `PetCodeReturn114` explicitly rejects a
  rebirth when the pet has not signed one, and both Pet Manager NPC
  descriptions state that the contract enables rebirth.

## Core pet items

| ID/range | Client name | Purpose |
|---:|---|---|
| 10000-10003 | Herbivore food | Satiety/amity food |
| 10020-10023 | Carnivore food | Satiety/amity food |
| 10040-10043 | Omnivore food | Satiety/amity food |
| 10060-10061 | Mellow/Fine Wine | Universal amity recovery |
| 10080-10084 | Capture tools | Increasing pet-capture chance |
| 10090 | Effective Water | Adds 100 lifetime |
| 10097 | Fused Harpyia | Bounded pet-merge complement |
| 10098 | Reborn Harpyia | Bounded rebirth complement |
| 10099 | Pet Enhance Spring | Adds a pet skill slot |
| 10100 | Golden Apple Juice | Unseals a pet skill |
| 10101 | Strong Purge Potion | Removes a selected skill |
| 10102 | Weak Purge Potion | Removes a random skill |
| 10103 | Merged Spirit | Improves pet-to-pet merge |
| 10104 | Rebirth Spirit | Improves rebirth |
| 10105 | Contract Spirit | Improves Soul Contract |
| 10106 | Pixie Tear | Reveals concealed growth |
| 10107 | Spring Water | Adds a rebirth chance |
| 10108-10109 | Seal Jade | Empty/packed pet transfer |
| 10110-10114 | Talent sticks | Random Event, Dispatch, Work, Healing, Merge |
| 10130-10134 | Morning Dew 1-5 | Pet EXP consumables |
| 10140-10144 | Restricted Morning Dew | Bound-pet EXP consumables |
| 10145-10146 | Juice of Rebirth | Extra attempts after 30 rebirths |
| 11000 | Fairy's Feather | Resets the six base-savvy distribution |
| 11003 | Charm: Pet Call | Reusable summon/dismiss action |
| 11004 | Charm: Merge | Reusable owner-merge action |
| 11005 | Phoenix's Feather | Resets growth toward maximum potential |
| 11010 | Spring Water (Restricted) | Bound-pet rebirth chance |
| 11015 | Pet Gender Reverser | Changes a bound pet's sex |
| 11050-11094 | Magic Jade | Species 1-45 acquisition/change items |
| 11095 | Ambrosia of Rebirth | Adds a rebirth chance for rebirths 61-100 |

The client calls `10103` both “Merged Spirit” and “Merge Spirit.” The item
catalog name is retained as **Merged Spirit**.

## Pet skill families

The client has 1,655 runtime rows grouped into 67 named skill families. Runtime
skill IDs and skill-book item IDs are separate namespaces. Most book families
have tiers I-VI; the six savvy families have I-III books.

- HP/MP: Vital Boost, Life Totem, Frozen Blessing, Resolute Physique,
  Meditate, Mind Refresh, Magission.
- Attack: Sharp Claw, Tear, Feather Blade, Mystic Oracle, Pixie Dust,
  Dark Vengeance, Spirit Strength, Wild Strength.
- Defense: Holy Shield, Immortal Kiss, Sparkling Fog, Magic Barrier, Block,
  Ward.
- Hit/dodge: Eagle Eye, Focus, Evasion, Mesmerise, Power Surge, Bullseye,
  Resistance, Scurry.
- Critical/tenacity: Death Spike, Concentration, Discharge, Iceshot,
  Fury of Justice, Eclipse, Brace, Mean Streak.
- Damage: Solidify, Mentality, Penalty of Justice, Gnarl, Violent Strength,
  Magic Strength, Palm Sweep, Wild Bump.
- Mitigation: Wind Ward, Light Ward, Heart Ward, Sphinx's Enigma,
  Force Shield, Guard, Sacrifice.
- Sustain/reflect: Blood Chant, Extraction, Primal Spirit, Lifedrain, Prick,
  Ocean Sphere, Tiger's Roar, Spiky Armor, Imp Trick.
- Savvy: Agility, Strength, Accuracy, Technique, Wisdom, Luck.

Pets expose six client skill slots. The five genius/talent abilities are Random
Event, Quest Dispatch, Work, Healing, and Merge.

This slice catalogs all five talents and all skill families, but persistence
currently models only the Merge talent and occupied skill slots. Unlocked slot
progression and the other four talents belong in the next forward-only schema
migration once their creation/default behavior is captured.

## Trust boundary and transaction contract

Clients may submit only intent and selected object/item identifiers. For every
mutation, the server must:

1. authenticate the character;
2. lock the character, pets in ascending pet-ID order, and inventory rows in
   ascending row-ID order;
3. re-read pet ownership, revision, lifecycle, levels, materials, and counts;
4. calculate the result with server-owned policy and randomness;
5. apply all pet, inventory, and audit mutations in one transaction;
6. consume the secondary pet only after all validation succeeds;
7. publish refreshed stats only after commit.

No level, stat gain, rank, material quantity, roll, or success flag received
from the client is authoritative.

## Protocol capture gate

The owned-pet login bootstrap is now verified and implemented as S2C opcode
`10237` (`OwnedPetList`). Its 8-byte header is followed by one fixed
`0xA8`-byte record per pet. The canonical populated capture is
`captures/working-multiplayer-20260514-193356.log`, the native handler is
`0x0069C950`, and its record-copy routine is `0x006A6340`. The server loads
the complete relational pet snapshot and sends this packet after inventory
bootstrap and before the skill list/enter-complete boundary.

The installed client's native routines also establish the basic presence
protocol:

| Direction | Opcode | Meaning | Layout |
|---|---:|---|---|
| C2S | 10239 | Carry/Take | 8 bytes; pet ID at `+4` |
| C2S | 10240 | Summon/Call Out | 8 bytes; pet ID at `+4` |
| C2S | 10241 | Recall/Dismiss | 8 bytes; pet ID at `+4` |
| S2C | 10244 | Pet operation result | 9 bytes; pet ID at `+4`, result at `+8` |

Result `1` selects the carried pet, `5` recalls and removes its model, and `7`
summons and creates its model. Even results `2`, `6`, and `8` are the matching
failures. The server authenticates ownership, locks the character and pets,
commits `is_carried`/`is_summoned`, writes a pet-operation audit row, and only
then returns the native result. Exactly one carried and one summoned pet are
enforced by PostgreSQL.

Persisted presence is replayed only after the client has passed its world
readiness gate and map objects have loaded. It is replayed again after a map
transition, avoiding the native model constructor's unsafe early-world path.

S2C `10248` is the native world-ready pet restore packet, with pet ID at `+4`
and owner world-object ID at `+8`. The server sends it to the owner after
initial AOI readiness and after map transitions; the client atomically selects
and calls out the already-loaded pet. The handler ignores non-local owners, so
another player's summoned-pet model remains an explicit compatibility gap
whose separate observer path still needs capture.

Before wiring the remaining client-visible pet operations, capture the
original server for:

- individual pet detail refreshes;
- egg opening;
- all Pet Manager menu/action requests and responses;
- owner merge, pet merge, and rebirth success and rejection;
- pet skills;
- another player observing Carry, Summon, Recall, and map transitions;
- map changes, logout, reconnect, and duplicate login.

Record opcode, direction, complete frame lengths, field offsets, NPC
index/sub-ID, pet object discriminator, response code, and packet ordering.
Do not reuse monster spawn opcode `10020` or ambiguous item opcode `10049`
without that evidence. Unresolved fields in the `10237` record also remain at
their independently captured working-client baseline instead of being assigned
unverified server fields such as pet energy.

## Authoritative client evidence

- `Localization/en_us/Settings/Sys/Pet.xml`
- `Localization/en_us/Settings/Sys/Pet_Alter.xml`
- `Localization/en_us/Settings/Sys/Pet_Confect.xml`
- `Localization/en_us/Settings/Sys/Pet_Skill.xml`
- `Localization/en_us/Settings/Sys/ItemBaseAttribute.xml`
- `Localization/en_us/Text/EquipName.dat`
- `Localization/en_us/Text/EquipDescription.dat`
- `Localization/en_us/Text/Message.dat`
- `Localization/en_us/Text/Message_Pet.dat`
- `Localization/en_us/UI/Base/text.lua`
- `Localization/en_us/UI/Base/LuaText.lua`
- `Localization/en_us/UI/XML/Pet*.xml`
- `Localization/en_us/UI/XML/NpcFun/NpcFunPet.lua`
