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
functional when a mount-gear item carries valid attributes. Mount items do not
have a native append-attribute pool, which makes mount grade cosmetic until a
dedicated, server-authoritative grade design is implemented.

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
