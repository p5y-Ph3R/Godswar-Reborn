# Client rank effects: v2 role map and prototype gate

## Status

This is the evidence base for the second rank-effect design. The first design
(`reborn-greek-rank-effects-v1`) was rejected after its in-game appearance was
reviewed. The local development client was restored from
`C:\Reborn\backups\rank-effects-20260808T060956Z`; the restored targets match
their pre-install hashes. The quality/grade/elemental text palette is a
separate patch and was not rolled back.

The v1 package and its builder remain in the repository as historical tooling.
They are not an approved visual design and must not be installed.

The first v2 gate is now built and transactionally installed only in
`C:\Godswar Origin`: AR14 plus native Warrior WR10. The package passed its
offline role-contract test, the existing package-framework test, validation,
and a 60-target preflight. Post-install verification also passed. Rollback is
`C:\Reborn\backups\rank-effects-20260808T091548Z`. The B20H client and runtime
were not touched. AR10 through AR13 and the other three WR10 classes remain
unimplemented until this prototype passes an in-game visual review.

## What v1 got wrong

The legacy JCS files are not interchangeable decorative layers. Each mesh has
a specific spatial, animation, material, UV, and atlas role. V1 mixed low-rank
components into high-rank records and redirected every texture reference in a
record to one generic texture. That kept the files structurally valid but
destroyed the intended relationship between geometry and texture details.

The v2 rule is therefore: understand and preserve each native role first;
change only the role that is deliberately being redesigned.

## Armor effect roles

All audited high-rank records contain three slots, but the restored AR10-AR14
set is not one clean structural family: it includes historical mixed
placeholders and some 976/848 outer wrappers. V2 therefore selects the coherent
AR8/AR12 three-slot family as its base instead of assuming every current rank
is interchangeable. The figures below are for that selected base. Small
male/female binary differences do not change these roles.

| Slot | Geometry | UV range (approx.) | Material use | Animation | Actual role |
|---|---|---|---|---|---|
| `0` | 116 vertices, 100 faces | U `0.012..0.382`, V `0.009..0.493` | one material; all 100 faces use it | one matrix track, 31 keyed transforms over `0..4800` | Animated body shell/core halo. It samples the atlas's large left oval/ring and supplies the breathing/turning energy around the character. |
| `1` | 848 vertices, 768 faces | U `-0.594..0.497`, V `0.003..0.499` (intentional wrap) | two materials declared; material 0 has **zero faces**, material 1 has all 768 | static | Outer silhouette: the large phoenix/wing/crest shape. Vertex colours provide its white/grey/black intensity and edge fade. |
| `2` | 136 vertices, 128 faces | U `0.279..0.397`, V `0.020..0.349` | one material; all 128 faces use it | one matrix track, 31 keyed transforms | Animated inner rune/orbit/glyph. It samples the small central atlas details rather than the large halo. |

Material 0 in armor slot 1 is dead metadata. Editing its texture cannot alter
the visible effect, and assigning faces to it would be a structural change,
not a recolour.

AR9 is deliberately different. Its slot 1 has 976 vertices and 848 faces,
uses U `0.392..0.496` and V `0.030..0.472`, and forms the compact butterfly.
AR9 must remain byte-for-byte protected; it is not a source for v2 ranks.

### The 64x64 armor atlas is a layout, not a canvas

The stock `.gwo`/`.tga` payload is a TGA atlas whose regions have different
jobs:

1. The large left oval/ring feeds slot 0's animated core halo.
2. The upper-middle crest/wing/lightning detail feeds the outer silhouette.
3. The lower-middle rune, flare, or triangle feeds slot 2's inner glyph.
4. The far-right vertical strip supplies narrow edge, beam, or trail detail
   reached by wrapped UVs.

Any v2 atlas edit must preserve this layout and transparency behavior. A
full-frame concept image or a generic procedural texture is not a valid
replacement atlas.

## Weapon effect roles

V2 starts from the client's native WR10 files and existing effect IDs, not
renamed WR7 geometry. Male and female native WR10 JCS files are byte-identical
for the audited families.

| Class | Native family / ID | Static outer-corona role | Animated travelling-spark role |
|---|---|---|---|
| Warrior | one-hand `0009` | slot 0: 78 vertices, 26 faces; U `0.517..0.835`, V `0.018..0.485`; all faces use material 1 | slot 1: 240 vertices, 80 faces; U `0.825..0.953`, V `0.049..0.186`; two animation tracks |
| Champion | two-hand `0009` | slot 0: 78 vertices, 26 faces; U `0.546..0.880`, V `0.079..0.461`; all faces use material 1 | slot 1: 240 vertices, 80 faces; U `0.897..0.995`, V `0.005..0.097`; two animation tracks |
| Priest | one-hand `0209` | **slot 1**: 52 vertices, 26 faces; U `0.510..0.769`, V `0.014..0.490`; static | **slot 0**: 240 vertices, 80 faces; U `0.897..0.995`, V `0.005..0.097`; two animation tracks |
| Mage | two-hand `0059` | slot 0: same 78/26 static family as Warrior | slot 1: same 240/80 animated family as Warrior |

The slot number is not the semantic contract: Priest reverses the order. Code
must identify and validate the native structure for each family.

In Warrior and Mage static slot 0, material 0 (`test07.tga`) is declared but
has zero assigned faces; material 1 is visible. Champion static slot 0 has the
same dead-material pattern (`male_weapontwohand_1401.tga` is material 0).
Priest's two slots each declare and use one material. V2 must neither author a
visible design into a dead texture nor accidentally revive a dead material.

For the first weapon prototype, the supported visual surfaces are limited to:

- the static outer corona around the weapon; and
- the animated travelling spark/stream along it.

Attachment frames, animation keys, slot order, topology, UVs, material-face
indices, and existing effect IDs remain native.

## Non-negotiable safety constraints

- Preserve AR9 and WR1 through WR9 exactly.
- Do not mix unrelated low-rank JCS slots into a high-rank effect.
- Do not replace all texture dependencies with one generic texture.
- Preserve mesh topology, UVs, vertex colours, normals, material-face indices,
  frame/attachment matrices, and animation timing unless a separately reviewed
  prototype proves a particular change safe.
- If sculpting the armor silhouette, change only slot 1 vertex positions with
  topology-preserving tooling; recompute normals and prove all other binary-X
  content unchanged.
- Start texture work from the stock rank/family atlas and edit its semantic
  regions intentionally.
- Keep private texture names scoped to one effect so a change cannot leak into
  AR9 or another weapon family.
- Concept PNGs are visual direction only. They are not client texture atlases.
- Do not change rank thresholds, server effect IDs, item rank calculations, or
  the separate quality/grade/elemental text palette in this work.
- Never install experiments into the B20H client or its observation runtime.

## Prototype-first test plan

1. Build only one armor prototype (AR14, because test2 exposes the cap). Keep
   slots 0 and 2 from the coherent base; sculpt or re-author only the
   high-rank slot 1 silhouette, then make role-preserving atlas edits if the
   silhouette needs them.
2. Build only one weapon prototype (Warrior WR10). Preserve the native `0009`
   files and author only the outer-corona and travelling-spark texture regions.
3. Run offline gates: MSZIP round trip, token parsing, resolved references,
   texture bounds, unchanged topology/UV/material/animation fingerprints,
   dead-material usage, protected-rank hashes, and exact rollback.
4. Install transactionally in the normal development client only, never the
   B20H client. Record and verify the rollback before launching.
5. Review in the original renderer at idle, walking, mounted, normal attack,
   skill casting, near/far camera distances, and crowded backgrounds. Compare
   AR9 versus AR14 and WR9 versus WR10 so progression remains readable without
   overwhelming the character or weapon.
6. Obtain explicit visual approval for those two prototypes before producing
   the remaining AR10-AR13 ranks or the remaining three WR10 classes.
7. Expand one rank/class at a time, repeating the same offline and in-game
   gates. Install into the normal development client only after final approval.

Static SVG/texture inspection can verify geometry and atlas intent, but only
the original client renderer can validate blending, billboarding, depth,
attachment, UV animation, and movement readability.

The current local prototype was produced with:

```powershell
python tools/BuildRankEffectV2Prototype.py `
  --client-root "C:\Godswar Origin" `
  --output-root "C:\Reborn\artifacts\rank-effect-v2-prototype-20260808-03"
python tools/TestRankEffectV2Prototype.py `
  --package-root "C:\Reborn\artifacts\rank-effect-v2-prototype-20260808-03"
```
