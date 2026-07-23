# Mount quality and grade compatibility

The stock client stores only ten quality-indexed values on mount and mount-gear
items. Sending a persisted quality above ten without extending those vectors can
make the item detail path read beyond the authored data.

`tools/PatchClientMountQualityVectors.ps1` extends every `mount`, `mounthead`,
`mountarmor`, `mountsoul`, `mountornament`, and `mountamulet` numeric quality
vector to 20 entries in both client locales. It repeats the native Q10 endpoint:

- mount speed never increases merely because quality is displayed as Q20;
- mount and mount-gear native stats remain capped at their authored Q10 values;
- no existing item ID, model, texture, or append-attribute pool is repurposed.

Mount-gear append attributes already have values through level 25, so G25 is
functional when a mount-gear item carries valid attributes. Grade selects the
attribute value from `ItemAppendAttribute.xml`; quality does not increase an
append attribute.

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
- G25 drives the permitted attribute values, while Q11 through Q20 retain the
  repeated native Q10 mount base stat.

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
