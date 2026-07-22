# Gear Enhancement material catalog

The shipped client defines 51 Gear Enhancement materials: 45 Attribute Stones
and six catalysts. Item ID `9939` is an intentional gap. All 51 templates are
seeded in PostgreSQL and belong to the closed developer-item allowlist.

Each successful operation consumes one selected Attribute Stone and one
catalyst. Attribute rank and gear grade are separate: the first nine stone
families have five Quartz ranks, while each resulting attribute template has
grade-indexed `L1` through `L25` values.

## Quartz-enhanceable Attribute Stones

These nine stones work with Add, Enhance, and Delete. Their template chains
represent Attribute ranks 1 through 5.

| Item ID | Material | Gear attribute | Attribute templates |
|---:|---|---|---|
| 9930 | Strength Stone | Physical Attack | 0-4 |
| 9931 | Shield Stone | Physical Defense | 10-14 |
| 9932 | Magic Stone | Magical Attack | 20-24 |
| 9933 | Spell Stone | Magical Defense | 30-34 |
| 9934 | Absorption Stone | Damage Absorption | 100-104 |
| 9935 | Health Stone | Maximum HP | 130-134 |
| 9936 | Mana Stone | Maximum MP | 140-144 |
| 9937 | Blood Stone | HP Restoration | 150-154 |
| 9938 | Vigor Stone | MP Restoration | 160-164 |

## Add/Delete-only Attribute Stones

| Item ID | Material | Gear attribute | Stack cap |
|---:|---|---|---:|
| 9940 | Accuracy Stone | Hit | 99 |
| 9941 | Psychic Stone | Dodge | 99 |
| 9942 | Fury Stone | Critical Bonus | 99 |
| 9943 | Tenacity Stone | Critical Resistance | 99 |
| 9944 | Impact Stone | Physical Damage % | 99 |
| 9945 | Fervor Stone | Magical Damage % | 99 |
| 9946 | Punishment Stone | Status/spell success (`State`) | 99 |
| 9947 | Purge Stone | Status resistance (`StateImmunity`) | 99 |
| 9948 | Guard Stone | Healing Received % (`AcceptCure`) | 99 |
| 9949 | Restoration Stone | Healing Done % (`Cure`) | 99 |
| 9950 | Primal Stone | Melee-weapon Physical Attack | 1 |
| 9951 | Courage Stone | Melee-weapon Hit | 1 |
| 9952 | Energy Stone | Melee Physical Damage % | 1 |
| 9953 | Rage Stone | Melee Critical Chance | 1 |
| 9954 | Holy Stone | Caster Magical Attack | 1 |
| 9955 | Blessing Stone | Caster Healing % | 1 |
| 9956 | Rune Stone | Caster Magical Damage % | 1 |
| 9957 | Force Stone | Caster Critical Chance | 1 |
| 9958 | Spirit of Destruction | Ignore Physical Defense % | 99 |
| 9959 | Spirit of Penetration | Ignore Magical Defense % | 99 |

The English client description calls Guard Stone "healing done," but its
native `AcceptCure` template and the backend calculation mean healing received.
Punishment and Purge can be stored on gear, but the current authoritative
character-stat aggregation does not yet apply template types 11 and 12.

## Catalysts

| Item ID | Material | Operation |
|---:|---|---|
| 9960 | Quartz Plate 1 | Attribute rank 1 to 2 |
| 9961 | Quartz Plate 2 | Attribute rank 2 to 3 |
| 9962 | Quartz Plate 3 | Attribute rank 3 to 4 |
| 9963 | Quartz Plate 4 | Attribute rank 4 to 5 |
| 9990 | Flame Spark | Add the selected stone family |
| 9991 | Water Grain | Delete the matching stone family |

Quartz Plates are valid only with item IDs `9930` through `9938`.

## Legendary Attribute Stones

These 16 stones are Add/Delete-only. Their eight templates are equipment-tier
variants, not Quartz ranks.

| Item ID | Material | Known family | Template variants |
|---:|---|---|---|
| 9970 | Stone of Vitality | Maximum Health | 300-307 |
| 9971 | Stone of Wisdom | Maximum Mana | 310-317 |
| 9972 | Stone of Precision | Hit Rating | 320-327 |
| 9973 | Stone of Evasion | Dodge Rating | 330-337 |
| 9974 | Stone of Strength | Physical Attack | 340-347 |
| 9975 | Stone of Sorcery | Magical Attack | 350-357 |
| 9976 | Stone of Wrath | Physical Damage % | 360-367 |
| 9977 | Stone of Arcana | Magical Damage % | 370-377 |
| 9978 | Stone of Renewal | Health Regeneration | 380-387 |
| 9979 | Stone of Serenity | Mana Regeneration | 390-397 |
| 9980 | Stone of Ruin | Ignore Physical Defense % | 400-407 |
| 9981 | Stone of Negation | Ignore Magical Defense % | 410-417 |
| 9982 | Stone of Force | Flat Physical Damage | 420-427 |
| 9983 | Stone of Essence | Flat Magical Damage | 430-437 |
| 9984 | Stone of Fury | Critical Hit % | 440-447 |
| 9985 | Stone of Impact | Flat Critical Damage | 450-457 |

The family mappings are known, but the original client data primarily assigns
the variants to mount-equipment level bands and permits multiple candidates on
some items. The current backend selects the first allowed intersection. Keep
these stones out of ordinary-gear test packs until the original tier-selection
rule is confirmed.

## Safe developer generation

Examples:

```text
/item add 9930 99
/item add strengthstone 99
/item add quartzplate1 99
/item add flamespark 99
/item add watergrain 99
```

The command and both persistence stores resolve the ID from the combined
allowlist. Account ownership, bag capacity, native stack cap, and native bound
state are revalidated server-side; arbitrary item-template IDs are rejected.
