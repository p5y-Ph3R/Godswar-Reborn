# Pet system foundation

## Scope and terminology

The stock client uses the word **merge** for two unrelated operations. The
server must keep them separate:

- **Owner merge / Unite**: the summoned pet contributes stats and pet-skill
  effects to its owner. The pet remains owned and is not consumed.
- **Pet merge / Inosculate**: a primary pet absorbs a secondary pet. The
  secondary pet is consumed only after the transaction commits.
- **Rebirth / Samsara**: the pet returns to level 1, its added savvy is reset,
  and its rebirth progression advances.
- **Soul Contract / Indenture**: an independent operation that improves a
  pet's initial savvy. The stock server requires it for rebirth, while the
  detailed Pet Manager instructions say it does not affect pet merge.

The first server slice deliberately implements persistent state, catalogs,
validation plans, and transaction boundaries before any legacy client packet
is accepted. It does not yet include a store executor, inventory transaction,
live character-stat overlay, or client packet handler. No owned-pet opcode or
golden packet exists in the repository yet.

Pet aptitude is a separate 1-16 authoritative ladder. Values 1-14 retain the
stock names from `PETAPTITUDE1` through `PETAPTITUDE14`; values 15 and 16 are
project extensions named **Celestial** and **Transcendent**. PostgreSQL stores
the mapping in `pet_aptitude_templates` and references it from owned pets.

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

| Rebirth being performed | Required pet level |
|---:|---:|
| 1 | 50 |
| 2 | 80 |
| 3 | 100 |
| 4 | 110 |
| 5 and later | 120 |

Additional rules:

- The pet must be summoned.
- The pet must have a Soul Contract.
- One available rebirth is consumed.
- The pet returns to level 1.
- Initial savvy remains; added savvy is reset.
- Levels above the minimum are converted to pet EXP.
- Rebirth Spirit (`10104`) improves the result.
- Reborn Harpyia (`10098`) is the restricted equivalent accepted only when
  the pet is bound.
- At most five standard and restricted rebirth spirits may be used in total.
- Model evolution occurs at rebirth counts 8 and 20.

The exact extra-level EXP conversion and randomized savvy result still require
golden original-server captures.

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

The repository has no verified owned-pet opcodes. Before client-visible wiring,
capture the original server for:

- pet list and details;
- egg opening;
- summon and dismiss;
- all Pet Manager menu/action requests and responses;
- owner merge, pet merge, and rebirth success and rejection;
- pet skills;
- another player observing a pet;
- map changes, logout, reconnect, and duplicate login.

Record opcode, direction, complete frame lengths, field offsets, NPC
index/sub-ID, pet object discriminator, response code, and packet ordering.
Do not reuse monster spawn opcode `10020` or ambiguous item opcode `10049`
without that evidence.

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
