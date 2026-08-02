# Class Suit IV weapon textures

The original client defines separate Class Suit III and IV item IDs, but the
shipped `Characters` texture and model files for each pair are byte-identical.
`Characters_New` contains the Tier III assets and no Tier IV assets. This tool
prepares distinct Tier IV textures without changing the client or modifying
model geometry.

## Visual direction

| Class | Tier III → IV | Tier IV palette |
|---|---:|---|
| Warrior | `1034` → `1035` | Olympian crimson, hot highlights, bright gold |
| Champion | `1434` → `1435` | Celestial cyan, silver-white, bright gold |
| Mage | `1834` → `1835` | Astral violet, magenta energy, cold platinum |
| Priest | `1734` → `1735` | Divine emerald, ivory highlights, bright gold |

The transform retains the source luminance and fine detail while remapping
saturated regions to a class palette. Existing gold is promoted to a brighter
Tier IV metal. Transparent pixels are untouched and every alpha byte is
preserved.

## Stage and verify

From `C:\Reborn`:

```powershell
python tools/GenerateClassSuitIvWeaponTextures.py --audit-stock
python tools/GenerateClassSuitIvWeaponTextures.py --check --audit-stock
```

The default output is:

```text
artifacts/class-suit-iv-weapons/staged/
├── Characters/          # eight legacy-resolution male/female textures
├── Characters_New/      # eight high-resolution male/female textures
├── previews/            # one PNG per class and renderer root
├── manifest.json
└── stock-duplication-audit.json
```

`artifacts/` is intentionally ignored because these files are reproducible and
some native high-resolution textures exceed the repository's 20 KB source-file
limit. All maintained generator files remain below that limit.

The command has no install mode. It rejects an output directory inside the
client root and only reads `C:\Godswar Origin`.

## Deterministic safeguards

Generation fails closed when:

- any Tier III source hash differs from the reviewed client;
- a male and female source texture differs within a renderer root;
- dimensions differ from the known legacy or high-resolution layout;
- fewer than 60 percent of visible pixels change;
- any alpha byte changes;
- a generated TGA cannot be decoded to the exact expected pixels;
- its encoding type, dimensions, orientation, or suffix changes;
- two class/root outputs unexpectedly share a hash; or
- a generated hash differs from the reviewed hash pinned in `constants.py`.

`--check` recomputes every output and compares it byte-for-byte with the staged
textures, previews, and manifest. `--audit-stock` additionally captures the
pre-install Tier III/IV texture and model state.

## Later installation boundary

This stage does not copy or alter `.jcs` files. The manifest includes a
`future_model_copy_plan` with pinned source hashes. A later, separately reviewed
installer must:

1. back up every target client file;
2. install the staged Tier IV `.gwo` textures in both renderer roots;
3. retain the existing `Characters` Tier IV `.jcs` files;
4. copy each `Characters_New` Tier III `.jcs` file to the Tier IV filename
   byte-for-byte, because those target model files are currently absent;
5. verify both genders in character selection, locally in-world, and from a
   second player's view; and
6. provide an atomic restore path.

No server item IDs, packets, database rows, or item templates need to change for
the 3D texture replacement. The server already transmits the independent Tier IV
IDs and the client derives the asset filename from that ID.
