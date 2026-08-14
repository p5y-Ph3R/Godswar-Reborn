# Pet system foundation

## Scope and terminology

The stock client uses the word **merge** for two unrelated operations. The
server must keep them separate:

- **Owner merge / Unite**: the summoned pet contributes stats and pet-skill
  effects to its owner. The pet remains owned and is not consumed.
- **Pet merge / Inosculate**: a primary pet absorbs a secondary pet. The
  secondary pet is consumed only after the transaction commits.
- **Rebirth / Samsara**: the pet returns to level 1, Basic Savvy is retained,
  and Rebirth acceleration advances independently.
- **Soul Contract / Indenture**: an independent staged bonus of +3 through +8
  on every displayed Savvy total without rewriting raw Basic. The stock server requires it for rebirth, while the
  detailed Pet Manager instructions say it does not affect pet merge.

The foundation now includes relational pet state, the owned-pet login
bootstrap, authoritative Carry, Summon, and Recall transitions, and the
database-published owner-Merge character-stat overlay. Pet-to-pet Merge,
Rebirth, and the remaining Pet Manager crafting operations remain later
slices.

Pet aptitude is a separate 1-16 authoritative ladder. Numeric IDs remain
stable for wire and database compatibility. Values 1-5 and 11-14 retain their
stock names, while the project deliberately reorders values 6-10 as **Calm**
(6), **Grumpy** (7), **Brave** (8), **Zealous** (9), and **Smart** (10).
Values 15 and 16 are project extensions named **Celestial** and
**Transcendent**. PostgreSQL stores the mapping in
`pet_aptitude_templates` and references it from owned pets.

For project gameplay, aptitude is the pet's **quality tier**. It selects the
Basic Savvy hatch budget and the base Growth Rate bracket unlocked by a
Phoenix's Feather. Every newly hatched pet starts with a Weak base-rate roll,
regardless of quality. This is a project rule, not a claim about stock balance.

The three values must not be conflated:

- **Basic Savvy** is the immutable hatch allocation plus later pet-to-pet
  Merge gains.
- **Effective Growth Rate** is `base_growth_rate + growth_acceleration`.
- **Cumulative Added Value** is `effective Growth Rate * current pet level`.

The stock Pet Detail label **Added-value** correctly names the second displayed
vector. Raw Growth Rate appears on the Phoenix result page, not in that vector.

| Aptitude/quality | Phoenix Growth Rate bracket | Total Basic Savvy at hatch |
|---|---:|---:|
| Weak | 0.01-0.10 | 25-34 |
| Fool | 0.10-0.25 | 35-44 |
| Cowish | 0.25-0.50 | 45-54 |
| Moderate | 0.50-1.00 | 55-69 |
| Rational | 1.00-2.00 | 70-84 |
| Calm | 2.00-4.00 | 85-104 |
| Grumpy | 4.00-7.00 | 105-124 |
| Brave | 7.00-11.00 | 125-149 |
| Zealous | 11.00-16.00 | 150-174 |
| Smart | 16.00-23.00 | 175-200 |
| Overbearing | 23.00-31.00 | 2,125-2,524 |
| Ferocious | 31.00-40.00 | 2,525-2,974 |
| Almighty | 40.00-50.00 | 2,975-3,474 |
| Godly | 50.00-62.00 | 3,475-4,024 |
| Celestial | 62.00-75.00 | 4,025-4,624 |
| Transcendent | 75.00-100.00 | 4,625-5,324 |

The low-to-mid ladder deliberately compresses Weak through Smart into the
25-200 total range. Overbearing through Transcendent retain their established
2,125-5,324 ranges, so the gap between Smart and Overbearing is intentional.
Existing pets keep their exact Savvy vectors under the explicit
`legacy-high-savvy-range-v1` compatibility policy; the V3 hatch policy applies
only to newly generated Savvy rolls.

The base Growth Rate total is rolled to hundredth precision and distributed
across Agility, Strength, Accuracy, Technique, Wisdom, and Luck. Each rate
remains within 12% of an even share, uses at most six decimal places, stays
positive, and sums to the rolled total. Hatch always uses the Weak
`0.01-0.10` bracket. Phoenix reset replaces the six base rates with a roll from
the pet quality's row. Base rate and Rebirth acceleration remain separate
durable inputs.

### Basic Savvy, Growth Rate, and Added Value

The Basic Savvy total is a separate whole-point roll. The server begins with a
near-even hundredth-precision distribution, randomizes the stat order, and
moves randomly selected amounts between paired attributes. Every resulting
attribute remains within 12% of the exact six-stat mean and the six values sum
back to the exact rolled total. This bounded random vector is the pet's
immutable quality-derived birth-savvy baseline.

At level `L`, each cumulative Added Value is
`(base_growth_rate + growth_acceleration) * L`. A level-up leaves Basic and
both rate inputs unchanged, then recomputes all six Added Values atomically
with the EXP deduction and level change. Rebirth returns to level 1, adds its
roll to acceleration, and recomputes Added for level 1. Owner Merge consumes
the player-visible `Basic + Added` totals.

The egg already owns its rarity; hatching does not roll a second rarity. The
server maps the authoritative egg instance's quality directly to the matching
aptitude and rejects undefined values without consuming the egg. It rolls the
quality's Basic budget and a separate Weak base-rate budget. Basic, base rate,
zero initial acceleration, and level-1 Added are persisted together. No higher
hidden Growth roll exists.

Completed-Rebirth widening and versioned Phoenix preview semantics are
specified in `pet-rebirth-balance.md`.

The Pet Manager point-reset page is client type `NpcFunPett` / dialog `36`.
Its Growth choice is sub-ID `101`, description/reset page `[112,117]`, and
exact reset action `117`. The server selects the summoned owned pet, consumes
one authoritative Phoenix's Feather (`11005`), rolls all six base rates in the
pet quality bracket plus the count-derived modifiers, recomputes Added at the
pet's current level, and commits both rate vectors, Added, revisions,
inventory, inbox/outbox, and the `reveal_growth` audit atomically. Native
pages are `127` (missing feather),
`128` (no summoned pet), and `130` plus six proposed effective-rate rows
(success). A retry replays the committed result without another roll or
consumption. Basic
Savvy never changes in this operation.

Action `117` creates the paid durable preview. Patched A2 **OK** accepts the
latest session-fenced preview, while Cancel leaves Growth unchanged and A3
**Reset/Draw again** creates another preview. Page `130` shows the six proposed
effective rates. On accept, opcode `10286` refreshes the pet object's Basic and
cumulative Added vectors; no full `10237` collection rebuild is emitted.
Apply or inspect the two-locale patch with
`tools/PatchClientPetGrowthResetDialog.ps1`; its exact-hash guard refuses
unknown or mixed client files.

Magic Jade and Bind durability, validation, and projection are specified in
`docs/pet-magic-jade-runtime.md`.

The installed client has species-specific creation profiles only for aptitude
values `1,2,3,4,5,7,8,9,10,12,14`. Hatching also requires that exact
species-plus-aptitude profile so lifetime is not borrowed from a different
rarity. Values `6,11,13` and the project extensions `15,16` remain valid
growth tiers, but an egg with one of those rarities is preserved and rejected
until matching client profiles are deliberately authored and shipped.

Native eggs are non-stackable (`Overlap=1`). Hatching consumes exactly one egg,
enforces the native eight-pet limit, creates the starter skill, records rarity
and all six-stat results, and commits or rolls back atomically. Startup rejects
partial or non-positive six-rate vectors. Unrevealed pets require a Weak base
rate; Phoenix-revealed pets require their quality bracket.

Migration history remains immutable. Migrations 017-018 introduced and
reconciled the rate policy with before-images. Migrations 019-021 recorded an
earlier reversed interpretation of Basic and Added. Forward migration
`20260810_069_pet_growth_savvy_semantics_v2` archived affected rows and restored
the project rule without guessing incomplete data. Migration
`20260811_070_pet_initial_savvy_policy_v3` preserved existing six-stat values
under an explicit legacy policy. Migration
`20260811_071_pet_phoenix_growth_activation` archived and reconciled unrevealed
base rates to Weak while preserving Basic; revealed rates were retained.

The verified owned-pet bootstrap is opcode `10237` (`0x27FD`). Within each
`0xA8`-byte pet record, six little-endian `uint32` Basic Savvy fields occupy
offsets `0x6C..0x83`; six cumulative Added Values occupy `0x84..0x9B`. Both
use `value * 100` fixed point. Native copies place them in pet-bean vectors
`+0x84..+0x9B` and `+0x9C..+0xB3`. Pet Detail renders both directly, and its
derived-stat routine sums them without multiplying by level. Original-server
captures likewise show level-scaled Added values. Therefore neither `10237`
nor the second vector of extended `10286` is a raw Growth Rate channel.

[Client-derived rules](pet-system-client-derived-rules.md)

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
| 10106 | Pixie Tear | Reveals the summoned pet's six effective Growth rates; every successful check consumes one |
| 10107 | Spring Water | Adds a rebirth chance |
| 10108-10109 | Seal Jade | Empty jade consumed to create an owner-linked packed pet; right-clicking the packed jade unseals when shed capacity allows |
| 10110-10114 | Legacy talent-stick artifacts | Inert compatibility records; aptitude owns talents |
| 10130-10134 | Morning Dew 1-5 | Pet EXP consumables |
| 10140-10144 | Restricted Morning Dew | Bound-pet EXP consumables |
| 10145-10146 | Juice of Rebirth | Extra attempts after 30 rebirths |
| 11000 | Fairy's Feather | Resets the six base-savvy distribution |
| 11003 | Charm: Pet Call | One-per-character claimed Pet Manager charm |
| 11004 | Charm: Merge | One-per-character claimed Pet Manager charm; innate owner Merge still does not consume it |
| 11005 | Phoenix's Feather | Rerolls base Growth Rate in the pet quality's bracket |
| 11010 | Spring Water (Restricted) | Bound-pet rebirth chance |
| 11015 | Pet Gender Reverser | Consumed to change the summoned, bound, non-merged pet's sex |
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

Pets support 12 learned-skill cells and a separate six-cell auto-cast bar.
Hatch defaults, the two-step slot-item progression, innate talent bits, and
the original Pet Manager dialogue maps (`31` Pet Raising and `36` Reset Pet's
Points) are maintained in
[Pet skills, talents, and manager dialogue](pet-skills-talents.md). The menu,
informational pages, and durable twelve-slot skill removal are wired; other
state-changing modal messages remain capture-gated until their native packet
layouts are verified.

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

The `10237` header has two independent one-byte counts:

| Offset | Field | Authoritative meaning |
|---:|---|---|
| `+4` | Opened shed-cell count | Persisted number of usable pet inventory cells; default `2`, maximum `8` |
| `+5` | Owned-pet count | Number of `0xA8` pet records that follow the header |

The stock client always renders eight physical pet cells. A new character has
two opened cells and six sealed cells. An opened cell may be occupied or empty,
so the opened-cell count must not be inferred from the number of owned pets.
For example, a character with one pet and the default two opened cells has one
available cell and six sealed cells. Opening additional cells is durable
character state and must be loaded independently of the owned-pet collection.

Hatching is capacity checked inside the authoritative inventory/pet
transaction. If the owned-pet count is already equal to the persisted opened
cell count, the request is rejected and the egg is not consumed. A successful
hatch consumes exactly one egg, creates the new pet, and makes it the carried
pet in the same transaction. Any previously carried pet is cleared. If that
previous companion was summoned in the world, its summoned state transfers to
the newly hatched pet so the old model is removed and the new companion model
is created only after commit. If no companion was summoned, the new pet is
carried but remains unsummoned.

The installed client's native routines also establish the basic presence
protocol:

| Direction | Opcode | Meaning | Layout |
|---|---:|---|---|
| C2S | 10239 | Carry/Take | 8 bytes; pet ID at `+4` |
| C2S | 10240 | Summon/Call Out | 8 bytes; pet ID at `+4` |
| C2S | 10241 | Recall/Dismiss | 8 bytes; pet ID at `+4` |
| S2C | 10244 | Live pet operation result | 9 bytes; pet ID at `+4`, result at `+8` |

Result `1` selects the carried pet, `5` recalls and removes its model, and `7`
summons and creates its model. Even results `2`, `6`, and `8` are the matching
failures. The server authenticates ownership, locks the character and pets,
commits `is_carried`/`is_summoned`, writes a pet-operation audit row, and only
then returns the native result. Exactly one carried and one summoned pet are
enforced by PostgreSQL. S2C `10244` is also the live in-world mechanism used
after a committed hatch to replace the previous companion: recall the old
summoned model, select the new carried pet, and call it out when summoned state
was preserved. These messages describe one committed state transition and are
not separate authoritative mutations.

Persisted presence is replayed only after the client has passed its world
readiness gate and map objects have loaded. It is replayed again after a map
transition, avoiding the native model constructor's unsafe early-world path.

S2C `10248` is the native world-ready pet restore packet, with pet ID at `+4`
and owner world-object ID at `+8`. The server sends it to the owner after
initial AOI readiness and after map transitions. Live-client verification
showed that `10248` alone does not reliably recreate the companion after a
fresh login, so persisted summoned presence is replayed as one `10244` Call
Out-success presentation followed immediately by `10248`. These packets do
not execute another authoritative mutation. The `10248` handler ignores
non-local owners, so another player's summoned-pet model remains an explicit
compatibility gap whose separate observer path still needs capture.

`10244` and `10248` therefore have deliberately different lifecycles. Use
`10244` for live Carry, Summon, Recall, and post-hatch companion replacement
while the character is already in the world. Use the verified `10244` success
plus `10248` pair only to restore the persisted carried/summoned companion
after login or a map transition has reached the world/AOI-ready boundary; the
pair is a presentation replay, not a generic pet mutation or database write.

Before wiring the remaining client-visible pet operations, capture the
original server for:

- individual pet detail refreshes;
- all Pet Manager menu/action requests and responses;
- the native server-to-client owner-merge visual response (the verified
  header-only action-bar request is opcode `10274` and is implemented);
- pet merge and rebirth success and rejection;
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
