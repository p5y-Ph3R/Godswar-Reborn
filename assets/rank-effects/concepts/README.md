# Rank-effect concept references

These sheets are visual targets, not files consumed directly by the client.
They are not texture atlases. A production texture must preserve the native
64x64 atlas regions, UV usage, transparency, and mesh-card role documented in
`../../../docs/client-rank-effect-v2-role-map.md`.

All references were generated with the built-in image-generation tool.

## V2 role-aware armor direction

Output: `armor-rank-redesign-v2-role-aware.png`

Prompt summary:

> Show AR10 through AR14 as readable ancient-Greek MMORPG character auras,
> constrained to the client's three native roles: animated core shell, static
> outer silhouette, and animated inner rune. Keep AR9's butterfly identity out
> of later ranks and make the progression distinct without hiding the player.

This is a composition and silhouette reference only. It must not be resized,
cropped, or copied into a `.gwo`/`.tga` atlas.

## V2 role-aware WR10 direction

Output: `weapon-rank10-redesign-v2-role-aware.png`

Prompt summary:

> Show class-specific Warrior, Champion, Priest, and Mage WR10 effects while
> retaining the native weapon model and attachment. Limit the authored effect
> vocabulary to the supported static outer corona and animated travelling
> spark/stream roles, with compact combat-readable class identities.

This is also concept-only. V2 preserves the native WR10 JCS models and authors
only supported corona and travelling-spark visual regions.

## Rejected v1 references

The following sheets belong to the rejected v1 direction. They remain for
history and must not be treated as implementation targets.

## Armor and progression reference

Output: `armor-weapon-rank-redesign-reference.png`

Prompt summary:

> Create an eight-panel ancient-Greek MMORPG VFX reference sheet on black.
> Show five non-wing armor silhouettes: Helios Aegis, Hecate's Veil, Gaia's
> Laurel, Ares' Eclipse, and Olympian Apotheosis. Show three progressively
> prestigious weapon-effect explorations: cyan Aether Temper, crimson-gold
> Titan's Wrath, and white-blue Zeus' Edict. Every silhouette must differ,
> remain combat-readable, and avoid the butterfly wings reserved for AR9.

The bottom row is early progression exploration. The final WR10 direction is
the class-specific sheet below.

## Class-specific WR10 reference

Output: `weapon-rank10-class-reference.png`

Prompt summary:

> Create four isolated ancient-Greek MMORPG weapon VFX concepts on black:
> Warrior Ares' Emberblade with a crimson-gold edge and ember crown; Champion
> Zeus' Stormlance with a white-blue lightning helix; Priest Apollo's Radiance
> with white-gold laurel arcs; and Mage Hecate's Aether with violet-cyan rune
> orbits. Keep effects aura-only, compact, class-distinct, and free of text,
> UI, characters, logos, or watermarks.
