# Pet Owner-Merge Octagram Effect Package

## Canonical asset

The reviewed fourth owner-Merge effect is generated, not hand-edited:

| Input/output | SHA-256 | Bytes |
| --- | --- | ---: |
| Stock `e_he_0003_all.gwm` template | `D46D3741FBFCBB0E393B758F0B8674782032672CAB3CB49C8E671DFF974937D2` | 31,091 |
| Reviewed UV-framed PNG | `395002D7A9239FBC823972227B0FB3445C849074B732F665D56E8132B2401E01` | 15,529 |
| Canonical `e_he_0004_all.gwm` | `0CF3D009356726F9A0A4691E2B03AD01557FDB8C7AAAF860E15170D66C0C1B4D` | 20,816 |

The source PNG and canonical package are in `assets/pet-owner-merge`. The
builder is repository-only and refuses any output under `C:\Godswar Origin`:

```powershell
python tools/BuildClientPetOwnerMergeOctagramEffect.py --check
python tools/TestClientPetOwnerMergeOctagramEffect.py
```

Run the builder without `--check` only when the canonical output is absent.
It creates the output through a verified same-directory stage and refuses to
overwrite changed bytes.

## GWM construction

Effect `0003` is the conservative template because it contains one model and
one texture. Its record layout is:

| Field | Size/value |
| --- | --- |
| Record count | `uint32 1` |
| Record prefix | `uint32 0` |
| Compressed-X length | `uint32`, target `1,637` |
| Embedded TGA length | `uint32`, target `18,727` |
| Compressed binary-X model | target reference `e_he_0004_a.tga` |
| TGA | 128×128, RLE type 10, BGRA32, descriptor 8 |
| Per-record metadata | 428 bytes |
| Container trailer | eight zero bytes |

The builder expands and parses the binary-X token stream. It replaces the one
complete texture string token `e_he_0003_a.tga` with the equal-length
`e_he_0004_a.tga`, then recompresses it with the existing deterministic MSZIP
codec. It does not search and replace compressed bytes.

The expanded source and target models are both 6,292 bytes and differ at
exactly one byte: the texture identity changes from ASCII `3` to `4`. Their
structural fingerprint is identical:

`4b16a3ed82eab1b058f7a9eda6976fb9de1cadeace8bea924b67a5417b8e8ac1`

This keeps the six-vertex/two-triangle horizontal plane, animation keys,
transforms, material values, topology, and UV floats byte-exact. The 428-byte
metadata record changes only the two corresponding identity digits in
`e_he_0003_all` and `e_he_0003_a.tga`; the eight-byte trailer is unchanged.

## Texture and material convention

The preserved model UV bounds are approximately `U 0.01687..0.73473` and
`V -0.00831..0.70955`. They sample a roughly 96×96 tile near the top-left of
the 128×128 atlas. The reviewed PNG is already framed for that footprint; its
nonzero bounds are `x=4..91`, `y=0..90`. Do not center or resize it again.

Stock effects `0002` and `0003` use black RGB as the visually transparent
background. Every decoded stock texel has alpha 255. A literal RGB-black
octagram therefore emits no color and would be invisible with the preserved
effect material. To retain the reviewed transparent PNG appearance, the
builder:

1. decodes its straight RGBA pixels;
2. premultiplies RGB by PNG alpha;
3. converts RGBA to BGRA;
4. forces every output alpha byte to 255;
5. reverses rows for the source descriptor-8 bottom-left TGA convention;
6. encodes type-10 RLE packets independently within each scanline and copies
   the source header/footer.

The generated atlas has 4,092 nonzero RGB texels, RGB energy 343,631, alpha
set `{255}`, and the same `x=4..91`, `y=0..90` bounds. This makes transparent
areas black in the convention the native effect already consumes while
retaining the dark violet octagram highlights.

The exact TGA stream has 623 packets and zero packets crossing a scanline.
The superseded 20,366-byte package with SHA-256 `97E14E301888C41E774F8C4312312F96E3DAD2FC8B88D3836369D60F4A0BAC59`
had 533 otherwise-decodable packets, but 90 crossed scanlines. Easy3D rejects
that stream. It is recognized only as `LegacyCrossScanline` so an installed
copy can be safely migrated; it is not a canonical or installable asset.

## Installer transaction and composability contract

The builder does not install the package.
`PatchClientPetOwnerMergeOctagram.ps1` owns four resources: the new GWM,
both locale copies of `Pet.xml`, and its exact executable selector range. It
leaves effects
`0001`, `0002`, and `0003` byte-identical; `0002` may independently be either
the stock or project-purple hash.

Apply must run with `Origin.exe` closed and commit in dependency order:

1. verify all source hashes and create a read-back-verified backup set;
2. stage and validate every complete target;
3. re-read all live hashes to reject drift;
4. install the new GWM first;
5. atomically replace both `Pet.xml` files;
6. replace the executable selector last;
7. verify the complete installed state, restoring in reverse order on error.

Revert uses the inverse safety order: disable the executable selector, restore
both XML files, then move the exact fourth GWM into the backup directory. If
cleanup fails, retaining an unreachable GWM is safer than deleting a still
referenced asset.

Status is read-only and reports the coherent aggregate state plus its
manual-realm, character-Back, and effect-`0002` peers. Apply and Revert refuse
mixed, unknown, partially written, or hash-drifted states.
Windows has no atomic transaction across four filesystem paths, so the closed
client, dependency ordering, per-file atomic replacement, and verified reverse
rollback are all required.

If Status finds the selector and both XML files already applied with the exact
legacy package, it reports `AssetUpgradeRequired=True`. Apply then performs a
narrow asset-only migration: it verifies and backs up the legacy bytes, stages
the fixed package, atomically replaces only `e_he_0004_all.gwm`, and retains
the displaced legacy file in the verified backup. The executable, both XML
files, and effect `0002` are not rewritten. A failure after replacement
atomically restores the exact legacy bytes. The client must still be closed.

The current executable before the fourth selector can be any exact combination
of the independent manual-realm and character-Back patches:

| Current selector state | SHA-256 |
| --- | --- |
| Owner-Merge visual only | `74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C` |
| Plus manual realm selection | `9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA` |
| Plus character-Back guard | `C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9` |
| Plus both independent patches | `318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF` |

The fourth-selector patcher maps all four source hashes to four exact target
hashes while changing only its own patch plane. The manual-realm and
character-Back patchers must reciprocally recognize the selector-off and
selector-on states so Apply/Revert order does not matter.

`PatchClientPetOwnerMergeVisual.ps1` recognizes all eight exact
manual/Back/octagram composite hashes and reports the peer flags. Apply returns
`Already Patched` without rewriting a composite. Base Revert is deliberately
allowed only from exact visual-only `74ADEE...` plus `E55050...`; otherwise
it instructs the operator to Revert octagram first, Character Back and manual
realm selection in either order, and the base visual last. The independent
effect-0002 palette patcher remains compatible because it owns only that GWM's
encoded red/green samples.
