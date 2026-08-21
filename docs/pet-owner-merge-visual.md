# Pet Owner-Merge Visual

The project client selects the owner's Merge aura from pet aptitude and scales
that aura from completed rebirth count. The only combined selector rule is
Transcendent aptitude plus at least 90 completed rebirths, which selects the
dark octagram effect. Pet species, sex, level, equipment, and the
companion-model `Scale` value do not select or resize the Merge aura.

## Quality ladder and effects

The authoritative aptitude values are shared with `PetAptitudeCatalog`:

| Value | Quality | Merge effect |
| ---: | --- | --- |
| 1 | Weak | `e_he_0001_all.gwm` |
| 2 | Fool | `e_he_0001_all.gwm` |
| 3 | Cowish | `e_he_0001_all.gwm` |
| 4 | Moderate | `e_he_0001_all.gwm` |
| 5 | Rational | `e_he_0001_all.gwm` |
| 6 | Calm | `e_he_0001_all.gwm` |
| 7 | Grumpy | `e_he_0001_all.gwm` |
| 8 | Brave | `e_he_0001_all.gwm` |
| 9 | Zealous | `e_he_0001_all.gwm` |
| 10 | Smart | `e_he_0001_all.gwm` |
| 11 | Overbearing | `e_he_0001_all.gwm` |
| 12 | Ferocious | `e_he_0002_all.gwm` |
| 13 | Almighty | `e_he_0002_all.gwm` |
| 14 | Godly | `e_he_0003_all.gwm` |
| 15 | Celestial | `e_he_0003_all.gwm` |
| 16 | Transcendent | `e_he_0003_all.gwm`; at 90+ rebirths, `e_he_0004_all.gwm` |

New project pets begin at Smart. Values below Smart retain the first effect as
a defensive compatibility fallback.

Both locale copies of `Pet.xml` retain the native `Samsara=0`, `8`, and `20`
rows for every species and sex. The octagram patch adds one managed
`Samsara=90` row to each of the 90 profiles. Every new row is an exact clone of
that profile's `Samsara=20` row except for `Samsara="90"` and the unite path
`e_he_0004_all.gwm`.

The selector normally maps aptitude to `0`, `8`, or `20`. It maps to `90` if
and only if aptitude is exactly `16` and completed rebirths are at least `90`.
Thus Celestial at 90 or 100 and Transcendent at 89 still select effect `0003`;
all lower-quality mappings are unchanged.

## Effect 0002 palette

The stock `e_he_0002_all.gwm` is cyan/aqua-blue. The project color patch maps
it to a clearly royal-purple/violet glow with lavender-white highlights. It
does not replace the atlas artwork: each encoded TGA sample swaps only its red
and green channels. The transform is its own exact inverse.

The audited GWM remains 43,083 bytes. Its compressed X model, animation,
geometry, material, UVs, TGA dimensions, RLE packet boundaries, alpha bytes,
footer, and metadata tail remain byte-identical. Exactly 15,706 bytes change,
all at red/green channel positions in 8,706 encoded BGRA samples. Effects
`0001` and `0003` are not transaction targets.

The exact transformed atlas preview is
`assets/pet-owner-merge/e_he_0002_a-purple.png`. It is a preview of the
deterministic client bytes, not a replacement texture consumed by the patcher.

| Palette | SHA-256 |
| --- | --- |
| Stock cyan | `89B98361733C4D127CEE984EACD58D7EE1DA098728672B11CB673AA5BA70A2F2` |
| Project purple | `7947392068C9FF1ED3C76973C80D37CA6B214493A8EBB90CD1329D4B5DCA7BE9` |

## Effect 0004 octagram

The fourth effect is the reviewed dark-violet/black octagram in
`assets/pet-owner-merge/e_he_0004_all.gwm`. It is a distinct package rather
than a recolor of effects `0001` through `0003`:

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| Source PNG `e_he_0004_a-black-octagram.png` | 15,529 | `395002D7A9239FBC823972227B0FB3445C849074B732F665D56E8132B2401E01` |
| Client GWM `e_he_0004_all.gwm` | 20,816 | `0CF3D009356726F9A0A4691E2B03AD01557FDB8C7AAAF860E15170D66C0C1B4D` |

The GWM contains one 1,637-byte compressed-X model, one 18,727-byte
128-by-128 BGRA32 RLE TGA, 428 bytes of metadata, and an eight-zero-byte
trailer. Its internal identities are uniquely `e_he_0004_all` and
`e_he_0004_a.tga`; it contains no remaining `0003` identity. See
`pet-owner-merge-octagram-effect.md` for the deterministic build and package
audit. Its 623 RLE packets are scanline-bounded. The patcher recognizes the
superseded `97E14E...` package only to migrate an already-applied client to
this fixed package without rewriting the selector or XML files.

## Rebirth scale

| Completed rebirths | Aura scale |
| ---: | ---: |
| 0-29 | 1.00 |
| 30-59 | 1.25 |
| 60-89 | 1.50 |
| 90-100 | 2.00 |

The client applies this value through the native effect-wrapper scale-vector
routine at `0x00686610`. This scales the aura only. It does not change the
summoned pet model or any gameplay statistic.

Selecting effect `0004` does not create another size tier. The existing scale
ladder remains exact, so every quality at 90+ completed rebirths is 2.00; only
Transcendent also changes artwork.

## Wire contract

Opcode `10275` is a ten-byte little-endian frame:

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 2 | packet length, `10` |
| 2 | 2 | opcode, `10275` |
| 4 | 4 | owner object ID in the receiver's namespace |
| 8 | 1 | aptitude, `1..16` |
| 9 | 1 | completed rebirths, `0..100` |

The owner receives local object ID `0x1448`; observers receive the owner's
world object ID. The active profile is retained in `GameSessionContext`, so a
late observer receives the same ten-byte start frame. Opcode `10282` remains an
eight-byte end frame and clears the retained profile.

The octagram rule consumes the two bytes already present at offsets 8 and 9.
It requires no packet, opcode, server-state, persistence, or observer-broadcast
change.

## Native patch invariants

The guarded source executable routes the `10275` handler at `0x006A16F0`.
The base visual patch owns the two call sites at file offsets `0x2A1729` and
`0x2A1780`, plus code cave `0x53E580..0x53E64F`. The dedicated octagram patch
leaves hook 1 at `0x2A1729` byte-identical. It owns only the nine-byte hook 2
range at `0x2A1780` and the same `0xD0`-byte cave, replacing the cave as one
exact managed unit.

The octagram selector is 74 bytes at cave offset `+0x00`. Hook 2 changes from
target `+0x40` to `+0x50`, leaving six zero bytes before the unchanged 72-byte
scale routine at `+0x50` and 56 trailing zero bytes. The executable conversion
changes exactly 139 bytes: one hook displacement byte and 138 cave bytes.
Apply and Revert reject any additional cave reference or partial byte state.

The completed-rebirth handoff uses one existing handler-stack byte. Let `S` be
the handler stack pointer at either hook site:

1. Hook 1 enters after `CALL`, so its stack is `S-4`. Writing
   `[esp+0x1A]` reaches `S+0x16`.
2. Hook 2 also enters at `S-4`. `pushfd` subtracts 4 and `pushad` subtracts 32,
   so its stack is `S-0x28`.
3. Reading `[esp+0x3E]` reaches `S-0x28+0x3E = S+0x16`, the exact byte written
   by hook 1.
4. Between the hooks, the native lookup pushes one argument and returns with
   `ret 4`; effect creation pushes two and returns with `ret 8`. The net stack
   delta is therefore zero, so both hook sites use the same `S`.

The patcher and its fixture test assert both instruction encodings and this
stack equation. This prevents the scaler from silently reading a neighboring
handler local.

The dedicated patch recognizes the complete cross-product of selector state,
manual realm selection, and the character-Back guard:

| Peer patches | Octagram off | Octagram on |
| --- | --- | --- |
| Neither | `74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C` | `8D15E202D8178927E69F06909659EA14DD7FD0EE8BE853BD3394E5EEE684D31F` |
| Manual realm | `9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA` | `4EF7A3A5F62BB739081CD76425D4AF14BEFDB03D1F36DABECF66624B1C4BA2DB` |
| Character Back | `C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9` | `FE01690D51B5A6C1FAEE48627372F35FFE9E110966E01F7D1EA96163EE8DEF61` |
| Both | `318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF` | `FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5` |

Both locale XML files move together between exact reverted hash
`E55050B49BB5DBED6F6A4A8D2BBB78237177A6FDA065155522034462C479748C`
and exact applied hash
`A6BBB855D8DC1092B867A9DED096C42348C991D847AB0EBB93C3127D9A8A96BE`.
The patch also accepts and preserves either audited effect-`0002` palette.

## Operations

Run the patcher with the client closed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\PatchClientPetOwnerMergeVisual.ps1 -Mode Status
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\PatchClientPetOwnerMergeVisual.ps1 -Mode Apply
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\PatchClientPetOwnerMergeEffectColor.ps1 -Mode Status
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\PatchClientPetOwnerMergeEffectColor.ps1 -Mode Apply
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\PatchClientPetOwnerMergeOctagram.ps1 -Mode Status
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\PatchClientPetOwnerMergeOctagram.ps1 -Mode Apply
```

The base visual `Apply` and `Revert` operations stage the executable and both
locale XML files. The color operation stages only `e_he_0002_all.gwm`. The
octagram operation transactionally owns only hook 2/cave, the 90 cloned rows
in both XML files, and exact creation or removal of `e_he_0004_all.gwm`. It
preserves the manual-realm, character-Back, and effect-`0002` states.

Octagram Apply commits the asset first, both XML files second, and the
executable last. Revert disables the executable first, restores both XML files,
then recoverably moves the exact asset into its verified backup directory.
Rollback is state-aware and uses same-directory stages plus atomic per-file
replacement: restoring an applied state makes the asset reachable before the
XML and executable, while restoring a reverted state disables the executable
before removing the asset. A foreign file that races into the effect-`0004`
path is preserved and causes the operation to fail.

When an already-applied client has the exact rejected cross-scanline package,
octagram Status reports `LegacyCrossScanline` and requires an upgrade. Apply,
with the client closed, atomically replaces only effect `0004` with the fixed
`0CF3D0...` package. It leaves the executable and XML files byte-identical,
keeps both verified legacy backups recoverable, and atomically restores the
legacy package if the asset commit fails.

The base visual tool reports every one of the eight manual/Back/octagram
combinations as `Patched`; Apply returns `Already Patched` without rewriting
shared ranges. Base Revert is allowed only from exact visual-only
`74ADEE... + E55050...`. Safe full teardown order is: Revert octagram first,
Revert Character Back and manual realm selection in either order, then Revert
the base visual. Any earlier base Revert fails with that instruction and does
not write the client.

All patchers verify exact hashes, create timestamped read-back-verified
backups, reject drift and mixed states, and restore the original complete
state on a write failure. The color `Revert` restores the exact stock GWM
without depending on a backup. Validate the isolated round trips with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\TestClientPetOwnerMergeVisualPatch.ps1
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\TestClientPetOwnerMergeEffectColorPatch.ps1
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\TestClientPetOwnerMergeOctagramPatch.ps1
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools\TestClientPetOwnerMergeOctagramUpgrade.ps1
python tools/TestClientPetOwnerMergeOctagramEffect.py
```
